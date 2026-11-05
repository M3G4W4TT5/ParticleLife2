using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using ParticleLife.MonoGame.Editor;
using ParticleLife.Viewer;

namespace ParticleLife.MonoGame;

internal sealed partial class ParticleLifeGame
{
    private readonly SpatialSampler spatialSampler = new();
    private readonly RegionTracker tracker = new();
    private SpatialObservation? observation;
    private IResidentSpatialObservation? observationOwner;
    private SpatialObservation? lastResidentObservation;
    private ModelParameters? residentObservationParameters;
    private double observationReceived, nextObservation;
    private long visualBaseline = -1;

    private void UpdateVisualRequests()
    {
        VisualToolsControls tools = surface.VisualTools;
        if (tools.StopTrackingRequested)
        {
            // Retain the currently displayed view when the transient follow target is removed.
            if (tracker.Active && tools.FollowTracking)
            { editor.SetAppearance(editor.Appearance with { Camera = displayedCamera }); }
            tracker.Stop(); tools.StopTrackingRequested = false;
            tools.TrackingStatus = "Tracking stopped.";
        }
        if (tools.RecenterRequested)
        {
            tools.RecenterRequested = false;
            if (tracker.Active) { tools.FollowTracking = true; }
        }
    }

    private CameraSettings TrackingCameraTarget()
    {
        CameraSettings target = editor.Appearance.Camera;
        if (!tracker.Active || !surface.VisualTools.FollowTracking) { return target; }
        ModelParameters parameters = editor.Publication.Configuration.Parameters;
        double x = tracker.Centre.X / parameters.WorldWidth, y = tracker.Centre.Y / parameters.WorldHeight;
        if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > 1e6 || Math.Abs(y) > 1e6) { return target; }
        return target with { CenterX = x, CenterY = y };
    }

    private void SuspendTrackingFollow()
    {
        if (!tracker.Active || !surface.VisualTools.FollowTracking) { return; }
        surface.VisualTools.FollowTracking = false;
        editor.SetAppearance(editor.Appearance with { Camera = displayedCamera });
    }

    /// <summary>Samples a leased complete CPU buffer only at the visible optional cadence.</summary>
    private void ObserveWorld(RenderFrameLease state)
    {
        ChangeObservationOwner(null);
        InvalidatePreviousDiscovery(state.BaselineVersion);
        VisualSettings settings = editor.Appearance.Visuals;
        bool enabled = settings.Heatmap != HeatmapKind.Off || settings.ShowStatistics || tracker.Active;
        if (!enabled)
        {
            observation = null;
            surface.VisualTools.ObservationStatus = "Observations off; no spatial sampling work.";
            return;
        }
        double now = TimestampSeconds;
        if (now >= nextObservation)
        {
            SpatialObservationRequest request = ObservationRequest(state.Configuration.Parameters,
                state.Configuration.Matrix.MaximumCutoff(state.Configuration.Parameters), settings);
            observation = spatialSampler.Sample(state.Positions, state.Velocities, state.Step, state.Time, request);
            observationReceived = now;
            nextObservation = now + settings.ObservationSeconds;
            tracker.Update(observation, state.Configuration.Parameters);
        }
        UpdateObservationStatus(now);
    }

    /// <summary>Polls a bounded device sample without capturing or waiting for particle state.</summary>
    /// <remarks>The request retains its source world parameters while the numerical owner continues.
    /// Optional sampling failures affect observation feedback only, never the simulation's valid state.</remarks>
    private void ObserveResidentWorld(IResidentSpatialObservation? source)
    {
        ChangeObservationOwner(source);
        InvalidatePreviousDiscovery(editor.Publication.BaselineVersion);
        VisualSettings settings = editor.Appearance.Visuals;
        bool enabled = settings.Heatmap != HeatmapKind.Off || settings.ShowStatistics || tracker.Active;
        if (source is null)
        {
            observation = null;
            surface.VisualTools.ObservationStatus = enabled
                ? "Spatial observations are unavailable on this device."
                : "Observations off; no spatial sampling work.";
            return;
        }
        double now = TimestampSeconds;
        try
        {
            if (!enabled)
            {
                if (source.IsSpatialObservationPending) { source.CancelSpatialObservation(); }
                observation = null;
                surface.VisualTools.ObservationStatus = "Observations off; no spatial sampling work.";
                return;
            }
            source.PollSpatialObservation();
            if (source.LatestSpatialObservation is { } sample && !ReferenceEquals(sample, lastResidentObservation))
            {
                lastResidentObservation = observation = sample;
                observationReceived = now;
                if (residentObservationParameters is { } parameters) { tracker.Update(sample, parameters); }
            }
            if (!source.IsSpatialObservationPending && now >= nextObservation)
            {
                SimulationConfiguration configuration = editor.Publication.Configuration;
                SpatialObservationRequest request = ObservationRequest(configuration.Parameters,
                    configuration.Matrix.MaximumCutoff(configuration.Parameters), settings);
                if (source.TryRequestSpatialObservation(request)) { residentObservationParameters = configuration.Parameters; }
                nextObservation = now + settings.ObservationSeconds;
            }
            UpdateObservationStatus(now);
            if (source.SpatialObservationError is { } error)
            { surface.VisualTools.ObservationStatus = "Observation unavailable: " + error; }
            else if (observation is null)
            { surface.VisualTools.ObservationStatus = "Waiting for the first completed GPU observation."; }
        }
        catch (Exception failure) when (ViewerErrors.IsExpected(failure))
        {
            nextObservation = now + settings.ObservationSeconds;
            surface.VisualTools.ObservationStatus = "Observation unavailable: " + failure.Message;
        }
    }

    /// <summary>Invalidates presentation data when CPU/GPU storage ownership changes.</summary>
    private void ChangeObservationOwner(IResidentSpatialObservation? source)
    {
        if (ReferenceEquals(source, observationOwner)) { return; }
        // The previous owner may already have been disposed by an explicit backend transfer.
        // Its resource lifetime is managed there; never call back into the retired device here.
        observationOwner = source;
        observation = lastResidentObservation = null;
        residentObservationParameters = null;
        nextObservation = 0;
        worldVisuals?.Clear();
    }

    private void InvalidatePreviousDiscovery(long baselineVersion)
    {
        if (visualBaseline == baselineVersion) { return; }
        visualBaseline = baselineVersion;
        observation = null; nextObservation = 0;
        lastResidentObservation = observationOwner?.LatestSpatialObservation;
        residentObservationParameters = null;
        tracker.Stop();
        worldVisuals?.Clear();
        surface.VisualTools.TrackingStatus = "New/reset discovery: tracking cleared. Pick a region to start again.";
    }

    private SpatialObservationRequest ObservationRequest(ModelParameters parameters, double cutoff, VisualSettings settings)
    {
        int columns = 32, rows = 32;
        if (settings.Heatmap == HeatmapKind.SpatialBins)
        {
            columns = (int)Math.Clamp(Math.Ceiling(parameters.WorldWidth / cutoff), 1, 128);
            rows = (int)Math.Clamp(Math.Ceiling(parameters.WorldHeight / cutoff), 1, 128);
        }
        if (tracker.Active) { columns = Math.Max(columns, 128); rows = Math.Max(rows, 128); }
        return new(default, parameters.WorldWidth, parameters.WorldHeight, columns, rows);
    }

    private void UpdateObservationStatus(double now)
    {
        if (observation is not SpatialObservation sample) { return; }
        surface.VisualTools.ObservationStatus = $"Step {sample.Step:N0}, t={sample.Time:G5}; received {now - observationReceived:F1}s ago. "
            + $"Mean/max speed {sample.MeanSpeed:G4}/{sample.MaximumSpeed:G4}; {sample.Outside:N0} outside display grid. Sample cost {sample.Milliseconds:F2} ms.";
        if (tracker.Active)
        {
            surface.VisualTools.TrackingStatus = tracker.TrackedCount == 0
                ? $"No occupied bin centres in the region at step {tracker.SampleStep:N0}. Centre retained; try a larger region."
                : $"Following {tracker.TrackedCount:N0} particles through display bins at step {tracker.SampleStep:N0}. "
                    + (surface.VisualTools.FollowTracking ? "Camera follow on." : "Camera follow suspended; indicator retained.");
        }
    }

    private void DrawObservation(WorldTransform transform)
    {
        if (observation is not SpatialObservation sample) { return; }
        VisualSettings settings = editor.Appearance.Visuals;
        GraphicsDevice.ScissorRectangle = Rectangle.Intersect(worldBounds, GraphicsDevice.Viewport.Bounds);
        batch.Begin(samplerState: SamplerState.LinearClamp, rasterizerState: clipped);
        if (settings.Heatmap != HeatmapKind.Off)
        {
            int maximum = 0;
            foreach (int count in sample.Counts) { maximum = Math.Max(maximum, count); }
            SpatialObservationRequest grid = sample.Request;
            for (int row = 0; row < grid.Rows; row++)
            {
                for (int column = 0; column < grid.Columns; column++)
                {
                    int count = sample.Counts[row * grid.Columns + column];
                    if (count == 0) { continue; }
                    Vector2d a = transform.ToScreen(new(grid.Origin.X + column * grid.Width / grid.Columns,
                        grid.Origin.Y + row * grid.Height / grid.Rows));
                    Vector2d b = transform.ToScreen(new(grid.Origin.X + (column + 1) * grid.Width / grid.Columns,
                        grid.Origin.Y + (row + 1) * grid.Height / grid.Rows));
                    double left = Math.Max(worldBounds.Left, a.X), top = Math.Max(worldBounds.Top, a.Y);
                    double right = Math.Min(worldBounds.Right, b.X), bottom = Math.Min(worldBounds.Bottom, b.Y);
                    if (right <= left || bottom <= top || !double.IsFinite(left + top + right + bottom)) { continue; }
                    float level = (float)(Math.Log(1d + count) / Math.Log(1d + maximum));
                    Color colour = Color.Lerp(new Color(48, 104, 150), new Color(245, 153, 81), level) * (0.15f + level * 0.4f);
                    canvas.Fill(new Rectangle((int)left, (int)top, Math.Max(1, (int)Math.Ceiling(right - left)),
                        Math.Max(1, (int)Math.Ceiling(bottom - top))), colour);
                }
            }
        }
        if (settings.ShowStatistics || settings.Heatmap != HeatmapKind.Off)
        {
            string text = $"OBS step {sample.Step:N0} · t {sample.Time:G4} · age {TimestampSeconds - observationReceived:F1}s · {sample.Milliseconds:F2}ms";
            canvas.Fill(new Rectangle(worldBounds.X + 8, worldBounds.Bottom - 25, Math.Min(600, worldBounds.Width - 16), 21), UiCanvas.Background * 0.88f);
            canvas.Text(text, worldBounds.X + 12, worldBounds.Bottom - 22, UiCanvas.Muted,
                Math.Max(0, (worldBounds.Width - 24) / 7));
        }
        batch.End();
    }

    private void DrawTracking(WorldTransform transform)
    {
        if (!tracker.Active) { return; }
        Vector2d centre = transform.ToScreen(tracker.Centre);
        double radius = transform.WorldLengthToPixels(tracker.Radius);
        if (!centre.IsFinite || !double.IsFinite(radius) || radius > 1e6 || Math.Abs(centre.X) > 1e7 || Math.Abs(centre.Y) > 1e7) { return; }
        GraphicsDevice.ScissorRectangle = Rectangle.Intersect(worldBounds, GraphicsDevice.Viewport.Bounds);
        batch.Begin(samplerState: SamplerState.LinearClamp, rasterizerState: clipped);
        Color colour = tracker.TrackedCount > 0 ? UiCanvas.Accent : UiCanvas.Error;
        Vector2 previous = new((float)(centre.X + radius), (float)centre.Y);
        for (int i = 1; i <= 64; i++)
        {
            double angle = i * Math.Tau / 64;
            Vector2 point = new((float)(centre.X + radius * Math.Cos(angle)), (float)(centre.Y + radius * Math.Sin(angle)));
            canvas.Line(previous, point, colour * 0.7f); previous = point;
        }
        canvas.Line(new((float)centre.X - 6, (float)centre.Y), new((float)centre.X + 6, (float)centre.Y), colour);
        canvas.Line(new((float)centre.X, (float)centre.Y - 6), new((float)centre.X, (float)centre.Y + 6), colour);
        canvas.Text("TRACK", (int)centre.X + 9, (int)centre.Y - 9, colour);
        batch.End();
    }
}
