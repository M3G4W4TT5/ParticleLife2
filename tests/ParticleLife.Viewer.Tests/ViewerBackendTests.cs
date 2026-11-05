using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using ParticleLife.MonoGame;
using ParticleLife.Viewer;

namespace ParticleLife.Viewer.Tests;

public sealed class ViewerBackendTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "particle-life-backend-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void WorkerSelectionSurvivesEditingSavingReplacementAndReset()
    {
        ViewerOptions options = ViewerOptions.Parse(["--backend", "grid", "--workers", "4", "--particles", "20"]);
        ExperimentDefinition initial = options.CreateDefaultExperiment();
        Assert.Equal(4, initial.WorkerCount);
        using PlaybackController controller = new(initial, diagnosticEverySteps: 1);
        foreach (ExperimentDefinition edited in new[] { initial, ExperimentEditing.ResampleState(initial, 7),
            ExperimentEditing.RandomizeMatrix(initial, 8), ExperimentFile.Deserialize(ExperimentFile.Serialize(initial)) })
        {
            Assert.Equal(4, edited.WorkerCount);
            controller.ReplaceExperiment(edited);
            controller.SingleStep();
            Assert.Equal(4, controller.LastExecution!.Value.ActualWorkerCount);
            Assert.NotNull(controller.LatestDiagnostic);
            controller.Reset();
            Assert.Null(controller.LatestDiagnostic);
            Assert.Equal(4, controller.Experiment.WorkerCount);
        }

        ViewerOptions explicitDirect = ViewerOptions.Parse(["--backend", "direct"]);
        ExperimentEditor loaded = ExperimentEditor.CreateFromFile(SaveFixture(initial), explicitDirect.BackendOverride,
            explicitDirect.WorkerOverride);
        using PlaybackController loadedController = loaded.Playback;
        Assert.Equal(ForceBackend.Direct, loaded.Configuration.Backend);
        Assert.Equal(1, loaded.Configuration.WorkerCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => ViewerOptions.Parse(["--backend", "direct", "--workers", "4"]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ViewerOptions.Parse(["--workers", "65"]));
    }

    [Fact]
    public void GeneratedViewerDefaultsUseGridAndLegacyInputRetainsDirect()
    {
        ViewerOptions options = ViewerOptions.Parse(["--particles", "8", "--species", "2"]);
        Assert.Null(options.BackendOverride);
        Assert.Equal(ForceBackend.UniformGrid, options.CreateDefaultExperiment().Backend);
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "two-species.json");
        ExperimentDefinition saved = ExperimentFile.Load(path);
        Assert.Equal(ForceBackend.Direct, saved.Backend);
        ExperimentEditor loaded = ExperimentEditor.CreateFromFile(path, options.BackendOverride, options.WorkerOverride);
        using PlaybackController controller = loaded.Playback;
        Assert.Equal(ForceBackend.Direct, controller.Configuration.Backend);
        PlaybackControllerTests.SameState(saved.InitialState, controller.Observe());
    }

    [Theory]
    [InlineData("direct", ForceBackend.Direct)]
    [InlineData("grid", ForceBackend.UniformGrid)]
    public void StartupOverrideAppliesToSavedInputReloadAndNewGeneration(string option, ForceBackend backend)
    {
        ViewerOptions options = ViewerOptions.Parse(["--backend", option, "--particles", "8", "--species", "2"]);
        ExperimentDefinition saved = PresetGenerator.Create(PresetKind.Random, 7, 3, backend: ForceBackend.UniformGrid);
        string path = SaveFixture(saved);
        ExperimentEditor loaded = ExperimentEditor.CreateFromFile(path, options.BackendOverride, options.WorkerOverride);
        using PlaybackController controller = loaded.Playback;
        ExperimentDefinition effective = controller.Experiment;
        Assert.Equal(backend, effective.Backend);
        PlaybackControllerTests.SameState(saved.InitialState, controller.Observe());
        Assert.Equal(backend, options.CreateDefaultExperiment().Backend);
        Assert.Equal(backend, ExperimentFile.Deserialize(ExperimentFile.Serialize(effective)).Backend);
        controller.SingleStep();
        Assert.Equal(backend, controller.LastExecution!.Value.ActualBackend);
        loaded.LoadExperiment(path, options.BackendOverride, options.WorkerOverride);
        Assert.Equal(backend, controller.Configuration.Backend);
        Assert.True(controller.IsPaused);
        PlaybackControllerTests.SameState(saved.InitialState, controller.Observe());
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("1")]
    [InlineData("UniformGrid")]
    public void ViewerRejectsInvalidBackendOption(string value)
    {
        Assert.Throws<ArgumentException>(() => ViewerOptions.Parse(["--backend", value]));
    }

    [Theory]
    [InlineData(ForceBackend.Direct)]
    [InlineData(ForceBackend.UniformGrid)]
    public void ResetEditingReplacementAndDiagnosticsKeepBackendAndClearExecution(ForceBackend backend)
    {
        ExperimentDefinition initial = PresetGenerator.Create(PresetKind.Random, 24, 3, 61, backend: backend);
        PlaybackController controller = new(initial, diagnosticEverySteps: 1);
        controller.SingleStep();
        Assert.Equal(backend, controller.LastExecution!.Value.ActualBackend);
        Assert.NotNull(controller.LatestDiagnostic);
        controller.Reset();
        Assert.Null(controller.LastExecution);
        Assert.Null(controller.LatestDiagnostic);
        Assert.Equal(0, controller.DirectFallbackSteps);
        Assert.Equal(backend, controller.Experiment.Backend);
        PlaybackControllerTests.SameState(initial.InitialState, controller.Observe());

        ExperimentDefinition[] variations =
        [
            ExperimentEditing.ResampleState(initial, 19),
            ExperimentEditing.RandomizeMatrix(initial, 20),
            PresetGenerator.Create(PresetKind.Snakes, 43, 4, 21, backend: backend)
        ];
        foreach (ExperimentDefinition variation in variations)
        {
            controller.ReplaceExperiment(variation);
            Assert.Null(controller.LastExecution);
            Assert.Null(controller.LatestDiagnostic);
            Assert.True(controller.IsPaused);
            controller.DiagnosticEverySteps = 0;
            controller.SingleStep();
            Assert.Null(controller.LatestDiagnostic);
            Assert.Equal(backend, controller.LastExecution!.Value.ActualBackend);
            controller.Reset();
            PlaybackControllerTests.SameState(variation.InitialState, controller.Observe());
        }
    }

    [Fact]
    public void FallbackProvenanceClearsOnResetAndReplacement()
    {
        ExperimentDefinition extreme = new("fallback", new ModelParameters { WallStrength = 0, ForceScale = 0 },
            new InteractionMatrix(1, [0]), new ParticleSnapshot([1], [new(1e200, 0)], [default], [0]),
            0.001, new GeneratorMetadata("manual", "1", null, "test"), ForceBackend.UniformGrid);
        PlaybackController controller = new(extreme, diagnosticEverySteps: 0);
        controller.SingleStep();
        Assert.Equal(ForceBackend.UniformGrid, controller.Experiment.Backend);
        Assert.Equal(ForceBackend.Direct, controller.LastExecution!.Value.ActualBackend);
        Assert.Equal(GridFallbackReason.CoordinateRange, controller.LastExecution!.Value.FallbackReason);
        Assert.Equal(1, controller.DirectFallbackSteps);
        Assert.Equal(0, controller.AccumulationFallbackSteps);
        controller.Reset();
        Assert.Null(controller.LastExecution);
        Assert.Equal(0, controller.DirectFallbackSteps);
        Assert.Equal(0, controller.AccumulationFallbackSteps);
        controller.SingleStep();
        controller.ReplaceExperiment(PresetGenerator.Create(PresetKind.Random, 4, 1, backend: ForceBackend.UniformGrid));
        Assert.Null(controller.LastExecution);
        Assert.Equal(0, controller.DirectFallbackSteps);
        controller.SingleStep();
        Assert.Equal(ForceBackend.UniformGrid, controller.LastExecution!.Value.ActualBackend);
    }

    [Fact]
    public void MovingBetweenFallbackAndGridRetainsCumulativeSuccessfulFallbackCount()
    {
        ExperimentDefinition experiment = new("cross-index-range",
            new ModelParameters { ForceScale = 0, WallStrength = 0, DampingHalfLife = null },
            new InteractionMatrix(1, [0]), new ParticleSnapshot([91], [new(1e16, 0)], [new(-1e16, 0)], [0]),
            1, new GeneratorMetadata("manual", "1", null, "exact ballistic values"), ForceBackend.UniformGrid);
        PlaybackController controller = new(experiment, diagnosticEverySteps: 0);
        controller.SingleStep();
        Assert.Equal(default, controller.Observe().Positions[0]);
        Assert.Equal(ForceBackend.Direct, controller.LastExecution!.Value.ActualBackend);
        Assert.Equal(GridFallbackReason.CoordinateRange, controller.LastExecution.Value.FallbackReason);
        Assert.Equal(1, controller.DirectFallbackSteps);

        controller.SingleStep();
        Assert.Equal(new Vector2d(-1e16, 0), controller.Observe().Positions[0]);
        Assert.Equal(ForceBackend.UniformGrid, controller.LastExecution!.Value.ActualBackend);
        Assert.Equal(GridFallbackReason.None, controller.LastExecution.Value.FallbackReason);
        Assert.Equal(1, controller.DirectFallbackSteps);

        controller.SingleStep();
        Assert.Equal(new Vector2d(-2e16, 0), controller.Observe().Positions[0]);
        Assert.Equal(ForceBackend.Direct, controller.LastExecution!.Value.ActualBackend);
        Assert.Equal(GridFallbackReason.CoordinateRange, controller.LastExecution.Value.FallbackReason);
        Assert.Equal(2, controller.DirectFallbackSteps);
        Assert.Equal(0, controller.AccumulationFallbackSteps);
    }

    [Fact]
    public void AccumulationFallbackIsReportedAndClearedByResetAndReplacement()
    {
        ExperimentDefinition experiment = new("extreme-force-cancellation",
            new ModelParameters { Cutoff = 1, ForceScale = 1e308, WallStrength = 0, DampingHalfLife = null },
            new InteractionMatrix(2, [0, 1, 0, 0]),
            new ParticleSnapshot([10, 11, 12, 13, 14],
                [default, new(0.65, 0), new(-0.65, 0), new(0.65, 0), new(-0.65, 0)],
                [default, default, default, default, default], [0, 1, 1, 1, 1]),
            0.001, new GeneratorMetadata("manual", "1", null, "alternating directed cancellation"), ForceBackend.UniformGrid);
        PlaybackController controller = new(experiment, diagnosticEverySteps: 0);
        controller.SingleStep();
        Assert.Equal(ForceBackend.Direct, controller.LastExecution!.Value.ActualBackend);
        Assert.Equal(GridFallbackReason.AccumulationRange, controller.LastExecution.Value.FallbackReason);
        Assert.Equal(1, controller.DirectFallbackSteps);
        Assert.Equal(1, controller.AccumulationFallbackSteps);
        controller.Reset();
        Assert.Null(controller.LastExecution);
        Assert.Equal(0, controller.DirectFallbackSteps);
        Assert.Equal(0, controller.AccumulationFallbackSteps);
        controller.SingleStep();
        Assert.Equal(1, controller.AccumulationFallbackSteps);
        controller.ReplaceExperiment(PresetGenerator.Create(PresetKind.Random, 4, 1, backend: ForceBackend.UniformGrid));
        Assert.Null(controller.LastExecution);
        Assert.Equal(0, controller.DirectFallbackSteps);
        Assert.Equal(0, controller.AccumulationFallbackSteps);
        controller.SingleStep();
        Assert.Equal(ForceBackend.UniformGrid, controller.LastExecution!.Value.ActualBackend);
        Assert.Equal(0, controller.AccumulationFallbackSteps);
    }

    private string SaveFixture(ExperimentDefinition experiment)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        ExperimentFile.Save(path, experiment);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
