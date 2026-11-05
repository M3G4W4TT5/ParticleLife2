using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Viewer;

namespace ParticleLife.Viewer.Tests;

public sealed class SelectivePresetTests
{
    [Fact]
    public void CoefficientsImportUsesExplicitDirectedAlignmentAndRetainsAllOtherComponents()
    {
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 12, 3, 21));
        ExperimentEditor editor = new(playback);
        playback.SingleStep();
        var before = PlaybackControllerTests.Copy(playback.Observe());
        SimulationConfiguration configuration = editor.Configuration;
        AppearanceSettings appearance = editor.Appearance;
        GenerationSettings generation = editor.Generation;
        PresetDefinition source = Source();
        editor.ApplySelectivePreset(source, PresetComponents.Coefficients, [1, -1, 0]);
        Assert.Equal(source.Configuration.Matrix[1, 0], editor.Configuration.Matrix[0, 2]);
        Assert.Equal(source.Configuration.Matrix[0, 1], editor.Configuration.Matrix[2, 0]);
        Assert.Equal(configuration.Matrix[1, 0], editor.Configuration.Matrix[1, 0]);
        Assert.Same(configuration.Parameters, editor.Configuration.Parameters);
        Assert.Same(configuration.Matrix.Ranges, editor.Configuration.Matrix.Ranges);
        Assert.Same(appearance, editor.Appearance);
        Assert.Same(generation, editor.Generation);
        PlaybackControllerTests.SameState(before, playback.Observe());
        Assert.Equal(1, editor.SessionEventCount);
        editor.Undo();
        Assert.True(configuration.EquivalentTo(editor.Configuration));
        Assert.False(editor.CanUndo);
        editor.Redo();
        using Simulation replay = SessionReplay.Replay(editor.CaptureSession("selective coefficients"));
        PlaybackControllerTests.SameState(before, replay.Observe());
        Assert.True(editor.Configuration.EquivalentTo(replay.Configuration));
    }

    [Fact]
    public void PaletteAndGenerationImportNeverChangesRulesParticlesPlaybackOrOtherAppearance()
    {
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 12, 3, 21));
        ExperimentEditor editor = new(playback);
        editor.SetAppearance(editor.Appearance with { ParticleRadius = 8, UiScale = 1.5 });
        var before = PlaybackControllerTests.Copy(playback.Observe());
        SimulationConfiguration configuration = editor.Configuration;
        double speed = playback.PlaybackSpeed;
        uint retained = editor.Appearance.ColorForSpecies(1);
        int retainedCount = editor.Populations[1];
        PresetDefinition source = Source();
        editor.ApplySelectivePreset(source, PresetComponents.Palette | PresetComponents.Generation, [1, -1, 0]);
        Assert.True(configuration.EquivalentTo(editor.Configuration));
        PlaybackControllerTests.SameState(before, playback.Observe());
        Assert.Equal(speed, playback.PlaybackSpeed);
        Assert.Equal(8, editor.Appearance.ParticleRadius);
        Assert.Equal(1.5, editor.Appearance.UiScale);
        Assert.Equal(source.Appearance.ColorForSpecies(1), editor.Appearance.ColorForSpecies(0));
        Assert.Equal(retained, editor.Appearance.ColorForSpecies(1));
        Assert.Equal(new[] { 70, retainedCount, 30 }, editor.Populations.ToArray());
        Assert.Equal(source.Generation, editor.Generation);
        Assert.Equal(0, editor.SessionEventCount);
    }

    [Fact]
    public void PartialRadiiPreserveUnmappedEffectivePairsAndRejectInvalidPeriodicCandidateAtomically()
    {
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 12, 3, 21));
        ExperimentEditor editor = new(playback);
        SimulationConfiguration before = editor.Configuration;
        PresetDefinition source = Source();
        editor.ApplySelectivePreset(source, PresetComponents.Radii, [1, -1, 0]);
        Assert.Equal(source.Configuration.Matrix.CutoffRadius(1, 0, source.Configuration.Parameters), editor.Configuration.Matrix.CutoffRadius(0, 2, before.Parameters));
        Assert.Equal(before.Matrix.CutoffRadius(1, 0, before.Parameters), editor.Configuration.Matrix.CutoffRadius(1, 0, before.Parameters));
        Assert.Equal(before.Matrix.Values.ToArray(), editor.Configuration.Matrix.Values.ToArray());
        editor.Undo();
        Assert.Null(editor.Configuration.Matrix.Ranges);
        editor.ApplySelectivePreset(source, PresetComponents.Radii, [-1, -1, -1]);
        Assert.Null(editor.Configuration.Matrix.Ranges);
        editor.ApplyConfiguration(new(before.Parameters with { Boundary = BoundaryKind.Periodic }, before.Matrix, before.TimeStep));
        SimulationConfiguration periodic = editor.Configuration;
        long revision = editor.Revision;
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.ApplySelectivePreset(source, PresetComponents.Radii | PresetComponents.Palette, [1, 0, -1]));
        Assert.True(periodic.EquivalentTo(editor.Configuration));
        Assert.Equal(revision, editor.Revision);
    }

    [Fact]
    public void InvalidMappingsAndUnknownComponentsLeaveTheEditorUnchanged()
    {
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 12, 3, 21));
        ExperimentEditor editor = new(playback);
        SimulationConfiguration before = editor.Configuration;
        Assert.Throws<ArgumentException>(() => editor.ApplySelectivePreset(Source(), PresetComponents.Coefficients, [0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.ApplySelectivePreset(Source(), PresetComponents.Coefficients, [0, 2, -1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.ApplySelectivePreset(Source(), (PresetComponents)16, [0, 1, -1]));
        Assert.Same(before, editor.Configuration);
        Assert.False(editor.CanUndo);
        Assert.Equal(0, editor.SessionEventCount);
    }

    private static PresetDefinition Source()
    {
        PairInteractionRanges ranges = new(2, [0.1, 0.2, 0.1, 0.1], [0.6, 0.7, 0.8, 0.9]);
        return new("source", new SimulationConfiguration(new(), new InteractionMatrix(2, [0.2, 0.9, -0.7, -0.5], ranges), 0.005),
            [30, 70], new GenerationSettings { Seed = 44, Placement = PlacementKind.Ring, Speed = 2 },
            new AppearanceSettings([0x123456, 0xABCDEF]), new PlaybackSettings { Speed = 3 });
    }
}
