using ParticleLife.Core.Experiments;
using ParticleLife.Core.State;

namespace ParticleLife.Viewer.Tests;

/// <summary>Checks that explicit quality choices remain presentation edits with overridable values.</summary>
public sealed class VisualQualityTests
{
    [Theory]
    [InlineData(VisualQualityPreset.Economy)]
    [InlineData(VisualQualityPreset.Balanced)]
    [InlineData(VisualQualityPreset.Luminous)]
    public void ManualQualityPresetsPreserveStateRulesPlaybackAndIndependentAppearance(VisualQualityPreset preset)
    {
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 8, 2, 88), diagnosticEverySteps: 17);
        ExperimentEditor editor = new(playback);
        playback.SingleStep();
        ParticleStateView state = playback.Observe();
        ParticleSnapshot before = new(state.Ids, state.Positions, state.Velocities, state.Species, state.Time, state.Step);
        AppearanceSettings original = editor.Appearance.WithPalette([0x123456, 0xFEDCBA]) with
        {
            Camera = new CameraSettings { CenterX = 0.4, Zoom = 1.5 },
            ParticleRadius = 4,
            UiScale = 1.25,
            Visuals = new VisualSettings { Velocity = VelocityShading.SpeedColour, SpeedScale = 0.75, Heatmap = HeatmapKind.Population }
        };
        editor.SetAppearance(original);
        PlaybackSettings playbackSettings = PlaybackSettings.Capture(playback);
        var configuration = editor.Configuration;
        editor.SetAppearance(original with { Visuals = VisualQuality.Apply(original.Visuals, preset) });
        AppearanceSettings applied = editor.Appearance;
        Assert.Equal(preset, VisualQuality.Match(applied.Visuals));
        Assert.Equal(original.Visuals.Velocity, applied.Visuals.Velocity);
        Assert.Equal(original.Visuals.SpeedScale, applied.Visuals.SpeedScale);
        Assert.Equal(original.SpeciesColors.ToArray(), applied.SpeciesColors.ToArray());
        Assert.Equal(original.Camera, applied.Camera);
        Assert.Equal(original.ParticleRadius, applied.ParticleRadius);
        Assert.Equal(original.UiScale, applied.UiScale);
        Assert.True(configuration.EquivalentTo(editor.Configuration));
        Assert.Equal(playbackSettings, PlaybackSettings.Capture(playback));
        Assert.Empty(editor.CaptureSession("quality changes").Events);
        AssertState(before, playback.Observe());

        VisualSettings custom = applied.Visuals with { Opacity = 0.123 };
        Assert.Null(VisualQuality.Match(custom));
        editor.SetAppearance(applied with { Visuals = custom });
        Assert.Equal(custom, editor.Appearance.Visuals);
        editor.Undo();
        Assert.Equal(applied.Visuals, editor.Appearance.Visuals);
        editor.Undo();
        Assert.Equal(original.Visuals, editor.Appearance.Visuals);
        AssertState(before, playback.Observe());
    }

    private static void AssertState(ParticleSnapshot expected, ParticleStateView actual)
    {
        Assert.Equal(expected.Time, actual.Time);
        Assert.Equal(expected.Step, actual.Step);
        Assert.Equal(expected.Ids.ToArray(), actual.Ids.ToArray());
        Assert.Equal(expected.Positions.ToArray(), actual.Positions.ToArray());
        Assert.Equal(expected.Velocities.ToArray(), actual.Velocities.ToArray());
        Assert.Equal(expected.Species.ToArray(), actual.Species.ToArray());
    }
}
