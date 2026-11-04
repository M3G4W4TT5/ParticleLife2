using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using ParticleLife.Viewer;

namespace ParticleLife.Viewer.Tests;

public sealed class BuiltInRecipeEditorTests
{
    public static TheoryData<PresetKind> NewRecipes =>
    [
        PresetKind.CoreHalo, PresetKind.AlternatingBands, PresetKind.Ribbons, PresetKind.CyclicPursuit,
        PresetKind.ChaseCycle5, PresetKind.ChaseCycle6, PresetKind.SpeciesIslands, PresetKind.BiasedRibbons
    ];

    [Theory]
    [MemberData(nameof(NewRecipes))]
    public void CompleteRecipeMatchesHeadlessGenerationAndBecomesExactResetBaseline(PresetKind kind)
    {
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 12, 7, 19,
            new ModelParameters { WorldWidth = 2, ForceScale = 0.2, Cutoff = 0.15 }, 0.004,
            ForceBackend.UniformGrid, 2), diagnosticEverySteps: 0);
        ExperimentEditor editor = new(playback);
        editor.SetGeneration(new GenerationSettings
        {
            Seed = 7,
            Placement = PlacementKind.Disk,
            Velocity = VelocityKind.RandomDirection,
            Speed = 2
        });
        AppearanceSettings appearance = new([0x123456, 0xFEDCBA]) { ParticleRadius = 4 };
        editor.SetAppearance(appearance);
        PlaybackSettings settings = new() { Speed = 2, MaxStepsPerFrame = 6, DiagnosticEverySteps = 0 };
        editor.SetPlayback(settings);
        playback.SingleStep();
        editor.SetMatrixCoefficient(0, 0, 0.123);
        Assert.True(editor.CanUndo);

        editor.NewRecipe(kind);

        ExperimentDefinition expected = PresetGenerator.Create(kind, seed: 7, backend: ForceBackend.UniformGrid, workerCount: 2);
        Assert.Equal(ExperimentFile.Serialize(expected), ExperimentFile.Serialize(playback.Experiment));
        Assert.Equal(new GenerationSettings { Seed = 7 }, editor.Generation);
        Assert.Same(appearance, editor.Appearance);
        Assert.Equal(settings, PlaybackSettings.Capture(playback));
        Assert.True(playback.IsPaused);
        Assert.False(editor.CanUndo);
        Assert.False(editor.CanRedo);
        Assert.Equal(0, editor.SessionEventCount);
        Assert.Equal(420, editor.Populations.ToArray().Sum());
        Assert.Equal(editor.GetCurrentPopulations(), editor.Populations.ToArray());

        playback.SingleStep();
        editor.SetMatrixCoefficient(0, 0, -0.123);
        editor.Reset();
        Same(expected.InitialState, playback.Observe());
        Assert.Equal(expected.Matrix.Values.ToArray(), editor.Configuration.Matrix.Values.ToArray());
        Assert.False(editor.CanUndo);
    }

    [Theory]
    [MemberData(nameof(NewRecipes))]
    public void LiveRecipeMatrixPreservesMotionAndRoundTripsThroughUndoAndSession(PresetKind kind)
    {
        PresetInfo recipe = PresetCatalog.Get(kind);
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 21,
            recipe.DefaultSpeciesCount, 11, new ModelParameters { ForceScale = 0.2 }, 0.002), diagnosticEverySteps: 0);
        ExperimentEditor editor = new(playback);
        playback.SingleStep();
        ParticleSnapshot before = Copy(playback.Observe());
        SimulationConfiguration old = editor.Configuration;
        ExperimentDefinition baseline = playback.Experiment;

        editor.ApplyMatrixTemplate(kind, 11);

        Same(before, playback.Observe());
        Assert.Same(old.Parameters, editor.Configuration.Parameters);
        Assert.Equal(old.TimeStep, editor.Configuration.TimeStep);
        Assert.Same(baseline, playback.Experiment);
        double[] matrix = editor.Configuration.Matrix.Values.ToArray();
        editor.Undo();
        Assert.Equal(old.Matrix.Values.ToArray(), editor.Configuration.Matrix.Values.ToArray());
        editor.Redo();
        Same(before, playback.Observe());
        Assert.Equal(matrix, editor.Configuration.Matrix.Values.ToArray());
        playback.SingleStep();

        SessionDefinition saved = SessionFile.Deserialize(SessionFile.Serialize(editor.CaptureSession("recipe edit")));
        using Simulation replay = SessionReplay.Replay(saved);
        Same(Copy(playback.Observe()), replay.Observe());
    }

    [Fact]
    public void IncompatibleTemplateAndUnknownRecipeRetainStateAndHistory()
    {
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Snakes, 21, 7, 11));
        ExperimentEditor editor = new(playback);
        editor.SetMatrixCoefficient(0, 0, 0.25);
        playback.SingleStep();
        ParticleSnapshot before = Copy(playback.Observe());
        SimulationConfiguration configuration = editor.Configuration;
        long revision = editor.Revision;
        int events = editor.SessionEventCount;
        Assert.ThrowsAny<ArgumentException>(() => editor.ApplyMatrixTemplate(PresetKind.CoreHalo, 1));
        Assert.ThrowsAny<ArgumentException>(() => editor.NewRecipe((PresetKind)999));
        Same(before, playback.Observe());
        Assert.True(configuration.EquivalentTo(editor.Configuration));
        Assert.Equal(revision, editor.Revision);
        Assert.Equal(events, editor.SessionEventCount);
        Assert.True(editor.CanUndo);
    }

    [Theory]
    [MemberData(nameof(NewRecipes))]
    public void CuratedPresetFileMatchesCatalogueRulesAndPopulations(PresetKind kind)
    {
        PresetInfo recipe = PresetCatalog.Get(kind);
        PresetDefinition preset = PresetFile.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "presets", recipe.Key + ".preset.json"));
        ExperimentDefinition experiment = PresetGenerator.Create(kind, backend: ForceBackend.UniformGrid);
        Assert.True(preset.Configuration.EquivalentTo(new SimulationConfiguration(experiment.Parameters,
            experiment.Matrix, experiment.TimeStep, experiment.Backend, experiment.WorkerCount)));
        Assert.Equal(Enumerable.Range(0, experiment.Matrix.SpeciesCount)
            .Select(species => experiment.InitialState.Species.ToArray().Count(value => value == species)), preset.Populations.ToArray());
        Assert.Equal(new GenerationSettings { Seed = 1 }, preset.Generation);
        if (recipe.IsExperimental)
        {
            Assert.Contains("experimental", preset.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static ParticleSnapshot Copy(ParticleStateView state) =>
        new(state.Ids, state.Positions, state.Velocities, state.Species, state.Time, state.Step);

    private static void Same(ParticleSnapshot expected, ParticleStateView actual)
    {
        Assert.Equal(expected.Time, actual.Time);
        Assert.Equal(expected.Step, actual.Step);
        Assert.Equal(expected.Ids.ToArray(), actual.Ids.ToArray());
        Assert.Equal(expected.Positions.ToArray(), actual.Positions.ToArray());
        Assert.Equal(expected.Velocities.ToArray(), actual.Velocities.ToArray());
        Assert.Equal(expected.Species.ToArray(), actual.Species.ToArray());
    }
}
