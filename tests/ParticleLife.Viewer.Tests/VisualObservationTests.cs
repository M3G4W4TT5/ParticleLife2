using System.Text.Json.Nodes;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.State;

namespace ParticleLife.Viewer.Tests;

/// <summary>Checks saved presentation settings and explicitly clocked, graphics-free observations.</summary>
public sealed class VisualObservationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "particlelife-visuals-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ResetAtTheSameClockPublishesAnExplicitNewBaselineEpoch()
    {
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Chains, 5, 2, 37), diagnosticEverySteps: 0);
        playback.SetPaused(true);
        ExperimentEditor editor = new(playback);
        using BackgroundExperiment owner = new(editor);
        using RenderFrameLease original = owner.AcquireRenderFrame()!;
        await owner.Submit("Reset at zero", value => value.Reset());
        using RenderFrameLease reset = owner.AcquireRenderFrame()!;
        Assert.Equal(original.Step, reset.Step);
        Assert.True(reset.Sequence > original.Sequence);
        Assert.True(reset.BaselineVersion > original.BaselineVersion);
        Assert.Equal(reset.BaselineVersion, owner.Publication.BaselineVersion);
        Assert.Equal(0, original.BaselineVersion);
    }

    [Fact]
    public void VisualSettingsAndPaletteSurvivePresetsAndSessionCompanionsWithoutChangingMotion()
    {
        ExperimentDefinition initial = PresetGenerator.Create(PresetKind.Random, 12, 2, 137);
        using PlaybackController playback = new(initial, diagnosticEverySteps: 0);
        ExperimentEditor editor = new(playback);
        for (int step = 0; step < 5; step++) { playback.SingleStep(); }
        ParticleSnapshot before = Capture(playback);
        VisualSettings visuals = new()
        {
            Opacity = 0.375,
            Blend = ParticleBlend.Additive,
            Velocity = VelocityShading.DirectionColour,
            SpeedScale = 0.234567891234,
            TrailHalfLifeSeconds = 1.75,
            GlowStrength = 2.25,
            GlowRadiusPixels = 19.5,
            Heatmap = HeatmapKind.SpatialBins,
            ObservationSeconds = 1.25,
            ShowStatistics = true
        };
        editor.SetAppearance(editor.Appearance with { Visuals = visuals });
        AppearanceSettings recoloured = editor.Appearance.WithPalette([0x003366, 0xABCDEF, 0x012345]);
        Assert.Equal(visuals, recoloured.Visuals);
        editor.SetAppearance(recoloured);
        AssertState(before, playback);
        Assert.Empty(editor.CaptureSession("appearance only").Events);

        PresetDefinition preset = PresetFile.Deserialize(PresetFile.Serialize(editor.CapturePreset("visual recipe")));
        Assert.Equal(visuals, preset.Appearance.Visuals);
        Assert.Equal(recoloured.SpeciesColors.ToArray(), preset.Appearance.SpeciesColors.ToArray());
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "session.json");
        editor.SaveSession(path, "visual discovery");
        using PlaybackController loadedPlayback = new(initial, diagnosticEverySteps: 0);
        ExperimentEditor loaded = new(loadedPlayback);
        loaded.LoadSession(path);
        Assert.Equal(visuals, loaded.Appearance.Visuals);
        Assert.Equal(recoloured.SpeciesColors.ToArray(), loaded.Appearance.SpeciesColors.ToArray());
        AssertState(before, loadedPlayback);
    }

    [Fact]
    public void LegacyAppearanceDefaultsVisualsAndInvalidVisualEditRetainsCurrentState()
    {
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 4, 2, 91), diagnosticEverySteps: 0);
        ExperimentEditor editor = new(playback);
        JsonObject root = JsonNode.Parse(PresetFile.Serialize(editor.CapturePreset("older appearance")))!.AsObject();
        root["appearance"]!.AsObject().Remove("visuals");
        Assert.Equal(new VisualSettings(), PresetFile.Deserialize(root.ToJsonString()).Appearance.Visuals);

        AppearanceSettings before = editor.Appearance;
        ParticleSnapshot state = Capture(playback);
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.SetAppearance(before with
        {
            Visuals = new VisualSettings { Velocity = (VelocityShading)123 }
        }));
        Assert.Equal(before, editor.Appearance);
        AssertState(state, playback);
    }

    [Fact]
    public void SpatialSamplesRetainBoundaryClockOutsideParticlesAndOwnedCounts()
    {
        SpatialSampler sampler = new();
        SpatialObservationRequest request = new(default, 1, 1, 2, 2);
        Vector2d[] positions = [new(-0.1, 0.5), new(0, 0), new(0.49, 0.49), new(0.5, 0.5), new(1, 1), new(1.1, 0.5)];
        Vector2d[] velocities = [new(3, 4), default, default, default, default, default];
        SpatialObservation sample = sampler.Sample(positions, velocities, 77, 0.125, request);
        Assert.Equal(77, sample.Step);
        Assert.Equal(0.125, sample.Time);
        Assert.Equal(6, sample.Count);
        Assert.Equal(2, sample.Outside);
        Assert.Equal(new[] { 2, 0, 0, 2 }, sample.Counts.ToArray());
        Assert.Equal(5, sample.MaximumSpeed);
        Assert.Equal(5d / 6, sample.MeanSpeed, 12);
        Assert.True(double.IsFinite(sample.Milliseconds) && sample.Milliseconds >= 0);

        positions[0] = new(0.75, 0.25);
        velocities[0] = default;
        SpatialObservation later = sampler.Sample(positions, velocities, 78, 0.25, request);
        Assert.Equal(new[] { 2, 0, 0, 2 }, sample.Counts.ToArray());
        Assert.Equal(2, sample.Outside);
        Assert.Equal(1, later.Outside);
        Assert.Equal(0, later.MeanSpeed);
        Assert.Equal(new Vector2d(0.75, 0.25), positions[0]);
    }

    [Fact]
    public void SpatialSamplingHandlesExtremeFiniteCpuWorldsAndVelocities()
    {
        SpatialObservationRequest request = new(default, 1e-300, 1e-300, 2, 2);
        SpatialObservation sample = new SpatialSampler().Sample([new(5e-301, 5e-301), new(1e300, 0)],
            [new(double.MaxValue, double.MaxValue), default], 9, 0.75, request);
        Assert.Equal(2, sample.Count);
        Assert.Equal(1, sample.Outside);
        Assert.Equal(double.MaxValue, sample.MaximumSpeed);
        Assert.True(double.IsFinite(sample.MeanSpeed) && sample.MeanSpeed > 0);
        Assert.Equal(new[] { 0, 0, 0, 1 }, sample.Counts.ToArray());
    }

    [Fact]
    public void PeriodicRegionTrackingCrossesSeamAndRetainsCentreWhenContactIsLost()
    {
        ModelParameters parameters = new() { Boundary = BoundaryKind.Periodic };
        SpatialObservationRequest grid = new(default, 1, 1, 4, 1);
        RegionTracker tracker = new();
        tracker.Start(new Vector2d(0.98, 0.5), 0.2);
        tracker.Update(new SpatialObservation(grid, [3, 0, 0, 1], 0, 0, 0, 44, 1, 0), parameters);
        Assert.Equal(4, tracker.TrackedCount);
        Assert.Equal(44, tracker.SampleStep);
        Assert.Equal(0.0625, tracker.Centre.X, 12);
        Assert.Equal(0.5, tracker.Centre.Y, 12);
        Vector2d lastCentre = tracker.Centre;

        tracker.Update(new SpatialObservation(grid, [0, 0, 0, 0], 0, 0, 0, 45, 1.1, 0), parameters);
        Assert.True(tracker.Active);
        Assert.Equal(0, tracker.TrackedCount);
        Assert.Equal(45, tracker.SampleStep);
        Assert.Equal(lastCentre, tracker.Centre);
        tracker.Stop();
        Assert.False(tracker.Active);
        Assert.Equal(lastCentre, tracker.Centre);
    }

    private static ParticleSnapshot Capture(PlaybackController playback)
    {
        ParticleStateView state = playback.Observe();
        return new(state.Ids, state.Positions, state.Velocities, state.Species, state.Time, state.Step);
    }

    private static void AssertState(ParticleSnapshot expected, PlaybackController playback)
    {
        ParticleStateView actual = playback.Observe();
        Assert.Equal(expected.Time, actual.Time);
        Assert.Equal(expected.Step, actual.Step);
        Assert.Equal(expected.Ids.ToArray(), actual.Ids.ToArray());
        Assert.Equal(expected.Positions.ToArray(), actual.Positions.ToArray());
        Assert.Equal(expected.Velocities.ToArray(), actual.Velocities.ToArray());
        Assert.Equal(expected.Species.ToArray(), actual.Species.ToArray());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
    }
}
