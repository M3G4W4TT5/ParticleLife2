using ParticleLife.Viewer;

namespace ParticleLife.MonoGame.Editor;

/// <summary>Owns optional observation, tracking and media drafts without changing numerical policy.</summary>
internal sealed class VisualToolsControls
{
    internal bool PickTrackingRegion { get; set; }
    internal double TrackingRadius { get; private set; } = 0.1;
    internal bool FollowTracking { get; set; } = true;
    internal bool StopTrackingRequested { get; set; }
    internal bool RecenterRequested { get; set; }
    internal bool ScreenshotRequested { get; set; }
    internal bool AnimationRequested { get; set; }
    internal bool StopAnimationRequested { get; set; }
    internal int CaptureWidth { get; private set; } = 720;
    internal int CaptureFramesPerSecond { get; private set; } = 10;
    internal int CaptureSeconds { get; private set; } = 4;
    internal string ObservationStatus { get; set; } = "Optional observations are off.";
    internal string TrackingStatus { get; set; } = "Select a circular region; tracking follows occupied display bins.";
    internal string CaptureStatus { get; set; } = "Captures save the canvas without panels. PNG retains colour; GIF uses a fixed 256-colour palette.";

    internal VisualToolsControls(EditorSession editor, UiSection appearance, UiSection discoveries)
    {
        VisualSettings Current() => editor.Appearance.Visuals;
        void Set(VisualSettings value) => editor.SetAppearance(editor.Appearance with { Visuals = value });
        QualityControls.AddTo(appearance, editor);
        UiNumber Number(string label, Func<double> get, Action<double> set, double initial, double minimum,
            double maximum, string help, bool integer = false) => new(label, get, set, initial, minimum, maximum,
                help, integral: integer, beginGesture: editor.BeginGesture, endGesture: editor.EndGesture);

        UiSection effects = new("Light and motion", false);
        appearance.Children.Add(effects);
        effects.Children.Add(Number("Opacity", () => Current().Opacity, value => Set(Current() with { Opacity = value }), 1, 0, 1,
            "Particle transparency. Zero hides particles without changing their state."));
        effects.Children.Add(new UiDropdown("Blend", ["Normal", "Additive"], () => (int)Current().Blend,
            value => Set(Current() with { Blend = (ParticleBlend)value }), "Normal transparency or accumulated light."));
        effects.Children.Add(new UiDropdown("Velocity shading", ["Species palette", "Speed brightness", "Speed colour", "Direction colour"],
            () => (int)Current().Velocity, value => Set(Current() with { Velocity = (VelocityShading)value }),
            "Colour uses completed velocity, without affecting motion or species."));
        effects.Children.Add(Number("Reference speed [L/T]", () => Current().SpeedScale,
            value => Set(Current() with { SpeedScale = value }), 0.1, 0.001, 2, "Speed colour reaches red at this speed; brightness reaches full intensity."));
        effects.Children.Add(Number("Trail half-life [s]", () => Current().TrailHalfLifeSeconds,
            value => Set(Current() with { TrailHalfLifeSeconds = value }), 0, 0, 8,
            "Zero turns trails off. Trails use presentation time and freeze when no completed state changes; camera changes clear them."));
        effects.Children.Add(Number("Glow strength", () => Current().GlowStrength,
            value => Set(Current() with { GlowStrength = value }), 0, 0, 4, "Zero disables the optional blurred particle halo."));
        effects.Children.Add(Number("Glow spread [px]", () => Current().GlowRadiusPixels,
            value => Set(Current() with { GlowRadiusPixels = value }), 10, 1, 64, "Presentation-only halo spread; larger values add overdraw."));

        UiSection observations = new("Observations and tracking", false);
        appearance.Children.Add(observations);
        observations.Children.Add(new UiDropdown("Heatmap", ["Off", "Population", "Spatial bins"], () => (int)Current().Heatmap,
            value => Set(Current() with { Heatmap = (HeatmapKind)value }),
            "Population uses 32 x 32 cells. Spatial bins follow the maximum cutoff, capped at 128 per axis; these are display bins, not neighbour truncation."));
        observations.Children.Add(new UiToggle("Sample speed statistics", () => Current().ShowStatistics,
            value => Set(Current() with { ShowStatistics = value }), "Optional completed-state measurement. The sample step, age and cost are shown."));
        observations.Children.Add(Number("Observation interval [s]", () => Current().ObservationSeconds,
            value => Set(Current() with { ObservationSeconds = value }), 0.5, 0.1, 10,
            "Longer intervals reduce observation work. Does not change simulation timestep or playback speed."));
        observations.Children.Add(new UiLabel(() => ObservationStatus, 72));
        observations.Children.Add(Number("Tracking radius [L]", () => TrackingRadius, value =>
        {
            if (!double.IsFinite(value) || value <= 0) { throw new ArgumentOutOfRangeException(nameof(value), "Use a positive finite radius."); }
            TrackingRadius = value;
        }, 0.1, 0.01, 1, "Choose a region radius, then click Pick region and a point on the canvas. Tracking is a coarse visual heuristic."));
        observations.Children.Add(new UiRow(new UiButton(() => PickTrackingRegion ? "Click canvas…" : "Pick region", () => PickTrackingRegion = true),
            new UiButton(() => "Stop tracking", () => { PickTrackingRegion = false; StopTrackingRequested = true; })));
        observations.Children.Add(new UiToggle("Follow region", () => FollowTracking, value => FollowTracking = value,
            "Follow the sampled centre. Manual pan or zoom suspends follow; the indicator remains."));
        observations.Children.Add(new UiButton(() => "Recenter on region", () => RecenterRequested = true));
        observations.Children.Add(new UiLabel(() => TrackingStatus, 64));

        UiSection captures = new("Screenshots and animation", false);
        discoveries.Children.Add(captures);
        captures.Children.Add(Number("Capture width [px]", () => CaptureWidth, value => CaptureWidth = Whole(value, 160, 1920),
            720, 320, 1080, "PNG and GIF width, 160..1920. Height follows the canvas aspect ratio, capped at 1920.", true));
        captures.Children.Add(Number("GIF frames / second", () => CaptureFramesPerSecond,
            value => CaptureFramesPerSecond = Whole(value, 1, 30), 10, 5, 20,
            "Frames are sampled from the displayed scene at wall-clock cadence. Capture overhead may reduce achieved playback; timestep is retained.", true));
        captures.Children.Add(Number("GIF duration [s]", () => CaptureSeconds, value => CaptureSeconds = Whole(value, 1, 10),
            4, 1, 10, "One through ten presentation seconds. A bounded asynchronous writer reports captured and skipped frames.", true));
        captures.Children.Add(new UiRow(new UiButton(() => "Save PNG", () => ScreenshotRequested = true),
            new UiButton(() => "Start GIF", () => AnimationRequested = true),
            new UiButton(() => "Finish GIF", () => StopAnimationRequested = true)));
        captures.Children.Add(new UiLabel(() => CaptureStatus, 88));
    }

    private static int Whole(double value, int minimum, int maximum)
    {
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value < minimum || value > maximum)
        { throw new ArgumentOutOfRangeException(nameof(value), $"Use a whole number from {minimum} through {maximum}."); }
        return (int)value;
    }
}
