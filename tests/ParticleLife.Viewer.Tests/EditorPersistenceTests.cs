using System.Text.Json;
using System.Text.Json.Nodes;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using ParticleLife.Viewer;

namespace ParticleLife.Viewer.Tests;

public sealed class EditorPersistenceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "particle-life-editor-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void PresetRoundTripsEveryPhysicalRecipeAndPresentationField()
    {
        using PlaybackController playback = CreatePlayback();
        ExperimentEditor editor = new(playback);
        editor.SetGeneration(new GenerationSettings
        {
            Seed = -789,
            Placement = PlacementKind.Disk,
            Velocity = VelocityKind.RandomDirection,
            Speed = 0.12345678912345678,
            DirectionRadians = 1.2345678912345678
        });
        editor.SetAppearance(new AppearanceSettings([0x001122, 0xABCDEF])
        {
            ParticleRadius = 5.25,
            ShowDiagnostics = false,
            ShowHelp = false,
            ShowOverlay = false,
            ShowEditor = false,
            UiScale = 1.25
        });
        editor.SetPlayback(new PlaybackSettings { Speed = 2.5, MaxStepsPerFrame = 11, DiagnosticEverySteps = 97, DiagnosticRadius = 0.251 });
        editor.SetPopulationRecipe([9, 2, 0]);
        SimulationConfiguration old = editor.Configuration;
        editor.ApplyConfiguration(new SimulationConfiguration(old.Parameters with
        {
            WorldWidth = 2,
            WorldHeight = 3,
            Cutoff = 0.12500000001,
            ForceScale = 0.789,
            RepulsionFraction = 0.125,
            DampingHalfLife = null,
            WallStrength = 0,
            WallWidth = 0.4
        }, old.Matrix, 0.00251, ForceBackend.UniformGrid, 4));
        PresetDefinition original = editor.CapturePreset("precise preset");
        string json = PresetFile.Serialize(original);
        PresetDefinition parsed = PresetFile.Deserialize(json);
        Assert.Equal(json, PresetFile.Serialize(parsed));
        Assert.True(original.Configuration.EquivalentTo(parsed.Configuration));
        Assert.Equal(original.Generation, parsed.Generation);
        Assert.Equal(original.Populations.ToArray(), parsed.Populations.ToArray());
        Assert.Equal(original.Appearance.SpeciesColors.ToArray(), parsed.Appearance.SpeciesColors.ToArray());
        Assert.Equal(original.Playback, parsed.Playback);
        editor.SavePreset(PathFor("settings.preset.json"), "precise preset");
        Assert.Equal(json, File.ReadAllText(PathFor("settings.preset.json")));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("version")]
    [InlineData("populations")]
    [InlineData("null")]
    [InlineData("enum")]
    [InlineData("backend")]
    [InlineData("nonfinite")]
    public void PresetStrictlyRejectsMalformedAndUnsupportedContracts(string mutation)
    {
        using PlaybackController playback = CreatePlayback();
        ExperimentEditor editor = new(playback);
        string json = PresetFile.Serialize(editor.CapturePreset("strict"));
        JsonObject root = JsonNode.Parse(json)!.AsObject();
        switch (mutation)
        {
            case "unknown":
                root["configuration"]!["parameters"]!["extra"] = 1;
                break;
            case "missing":
                root["generation"]!.AsObject().Remove("speed");
                break;
            case "version":
                root["schemaVersion"] = 999;
                break;
            case "populations":
                root["populations"] = new JsonArray(1, 2);
                break;
            case "null":
                root["appearance"] = null;
                break;
            case "enum":
                root["generation"]!["placement"] = "999";
                break;
            case "backend":
                root["configuration"]!["neighbourSearchVersion"] = "future/42";
                break;
        }

        string malformed = mutation switch
        {
            "duplicate" => json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"schemaVersion\": 1", StringComparison.Ordinal),
            "nonfinite" => json.Replace("\"speed\": 0", "\"speed\": 1e999", StringComparison.Ordinal),
            _ => root.ToJsonString()
        };
        Assert.ThrowsAny<Exception>(() => PresetFile.Deserialize(malformed));
    }

    [Fact]
    public void ApplyingPresetLiveRetainsPopulationClockAndBaselineButNewGenerationEstablishesBaseline()
    {
        using PlaybackController playback = CreatePlayback();
        ExperimentEditor editor = new(playback);
        editor.SetPopulationRecipe([7, 0, 4]);
        editor.SetGeneration(editor.Generation with { Seed = 937, Placement = PlacementKind.Grid });
        PresetDefinition preset = editor.CapturePreset("recipe");
        playback.SingleStep();
        ParticleSnapshot before = Copy(playback.Observe());
        ExperimentDefinition baseline = playback.Experiment;
        editor.ApplyPreset(preset, generatePopulation: false);
        Same(before, playback.Observe());
        Assert.Same(baseline, playback.Experiment);
        editor.ApplyPreset(preset, generatePopulation: true);
        Assert.Equal(11, playback.Observe().Count);
        Assert.Equal(0, playback.Observe().Time);
        Assert.Equal(0, playback.Observe().Step);
        Assert.True(playback.IsPaused);
        Assert.Equal(new int[] { 7, 0, 4 }, editor.GetCurrentPopulations());
        Assert.False(editor.CanUndo);
        Assert.Equal(0, editor.SessionEventCount);
        ParticleSnapshot generated = Copy(playback.Observe());
        playback.SingleStep();
        editor.Reset();
        Same(generated, playback.Observe());
    }

    [Fact]
    public void LivePresetSpeciesMismatchFailsWithoutChangingHistoryOrState()
    {
        using PlaybackController playback = CreatePlayback();
        ExperimentEditor editor = new(playback);
        using PlaybackController different = new(PresetGenerator.Create(PresetKind.Random, 2, 2));
        PresetDefinition preset = new ExperimentEditor(different).CapturePreset("different species");
        editor.SetMatrixCoefficient(0, 1, 0.8);
        playback.SingleStep();
        ParticleSnapshot before = Copy(playback.Observe());
        long revision = editor.Revision;
        Assert.Throws<ArgumentException>(() => editor.ApplyPreset(preset, false));
        Same(before, playback.Observe());
        Assert.Equal(revision, editor.Revision);
        Assert.Equal(1, editor.SessionEventCount);
        Assert.True(editor.CanUndo);
    }

    [Fact]
    public void CurrentStateExportRebasesHonestlyAndSavingDoesNotAffectTrajectoryOrHistory()
    {
        using PlaybackController playback = CreatePlayback();
        ExperimentEditor editor = new(playback);
        editor.SetMatrixCoefficient(0, 1, 0.8);
        playback.SingleStep();
        editor.ApplyConfiguration(new SimulationConfiguration(editor.Configuration.Parameters, editor.Configuration.Matrix, 0.003,
            ForceBackend.UniformGrid, 4));
        playback.SingleStep();
        ParticleSnapshot before = Copy(playback.Observe());
        int eventCount = editor.SessionEventCount;
        long revision = editor.Revision;
        double pending = playback.PendingPlaybackSeconds;
        editor.SaveCurrent(PathFor("checkpoint.json"), "from here");
        editor.SaveInitial(PathFor("initial.json"), "original values");
        editor.SaveSession(PathFor("run.session.json"), "edited run");
        Same(before, playback.Observe());
        Assert.Equal(eventCount, editor.SessionEventCount);
        Assert.Equal(revision, editor.Revision);
        Assert.Equal(pending, playback.PendingPlaybackSeconds);
        ExperimentDefinition saved = ExperimentFile.Load(PathFor("checkpoint.json"));
        Assert.Equal(0, saved.InitialState.Time);
        Assert.Equal(0, saved.InitialState.Step);
        Assert.Equal(before.Time, saved.Provenance!.SourceTime);
        Assert.Equal(before.Step, saved.Provenance.SourceStep);
        Assert.Equal(playback.Experiment.Generator, saved.Provenance.SourceGenerator);
        Assert.Equal(playback.Experiment.Name, saved.Provenance.SourceName);
        Assert.Null(saved.Generator.Seed);
        Assert.Equal(before.Positions.ToArray(), saved.InitialState.Positions.ToArray());
        Assert.Equal(before.Velocities.ToArray(), saved.InitialState.Velocities.ToArray());
        Assert.Equal(before.Ids.ToArray(), saved.InitialState.Ids.ToArray());
        Assert.Equal(ForceBackend.UniformGrid, saved.Backend);
        Assert.Equal(4, saved.WorkerCount);
        ExperimentDefinition initial = ExperimentFile.Load(PathFor("initial.json"));
        Assert.Equal(playback.Experiment.InitialState.Positions.ToArray(), initial.InitialState.Positions.ToArray());
        Assert.Equal(playback.Experiment.Matrix.Values.ToArray(), initial.Matrix.Values.ToArray());
        Assert.Equal("edited run", SessionFile.Load(PathFor("run.session.json")).InitialExperiment.Name);
    }

    [Fact]
    public void MainAndCompanionExistingFilesRemainProtectedAndNoOrphanIsCreated()
    {
        using PlaybackController playback = CreatePlayback();
        ExperimentEditor editor = new(playback);
        string main = PathFor("existing.json");
        Directory.CreateDirectory(directory);
        File.WriteAllText(main, "keep main bytes");
        Assert.Throws<IOException>(() => editor.SaveCurrent(main, "collision"));
        Assert.Equal("keep main bytes", File.ReadAllText(main));
        Assert.False(File.Exists(main + ".viewer.json"));
        string companionOnly = PathFor("companion.json");
        File.WriteAllText(companionOnly + ".viewer.json", "keep companion bytes");
        Assert.Throws<IOException>(() => editor.SaveSession(companionOnly, "collision"));
        Assert.False(File.Exists(companionOnly));
        Assert.Equal("keep companion bytes", File.ReadAllText(companionOnly + ".viewer.json"));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp*"));
    }

    [Fact]
    public async Task ConcurrentPairedSavesPublishExactlyOneMatchingCompletePair()
    {
        string path = PathFor("race.json");
        Task<bool>[] saves = Enumerable.Range(0, 6).Select(index => Task.Run(() =>
        {
            using PlaybackController playback = CreatePlayback();
            ExperimentEditor editor = new(playback);
            editor.SetGeneration(editor.Generation with { Seed = index });
            try
            {
                editor.SaveCurrent(path, "writer " + index);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        })).ToArray();
        bool[] results = await Task.WhenAll(saves);
        Assert.Single(results, success => success);
        using PlaybackController loaded = CreatePlayback();
        ExperimentEditor loadedEditor = new(loaded);
        loadedEditor.LoadExperiment(path);
        Assert.Equal("writer " + loadedEditor.Generation.Seed, loaded.Experiment.Name);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp*"));
        Assert.Empty(Directory.GetFiles(directory, "*.save.lock"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BusyOrInterruptedSaveRetainsItsReservationAndRetryWorksAfterRelease(bool companion)
    {
        using PlaybackController playback = CreatePlayback();
        ExperimentEditor editor = new(playback);
        Directory.CreateDirectory(directory);
        string path = PathFor("reserved.json");
        string reservedDestination = companion ? path + ".viewer.json" : path;
        string reservation = reservedDestination + ".save.lock";
        File.WriteAllText(reservation, "another writer");
        Assert.Throws<IOException>(() => editor.SaveCurrent(path, "blocked"));
        Assert.Throws<IOException>(() => ExperimentStorage.SaveNew(reservedDestination, playback.Experiment));
        Assert.Equal("another writer", File.ReadAllText(reservation));
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".viewer.json"));
        Assert.Single(Directory.GetFiles(directory));
        File.Delete(reservation);
        editor.SaveCurrent(path, "retry");
        Assert.Equal("retry", ExperimentFile.Load(path).Name);
        Assert.True(File.Exists(path + ".viewer.json"));
        Assert.Empty(Directory.GetFiles(directory, "*.save.lock"));
    }

    [Fact]
    public void CurrentExperimentLoadRoundTripsViewerChoicesAndEstablishesPausedBaseline()
    {
        using PlaybackController playback = CreatePlayback();
        ExperimentEditor editor = new(playback);
        editor.SetAppearance(editor.Appearance.WithSpeciesColor(2, 0x123456) with { ParticleRadius = 4.75 });
        editor.SetPlayback(new PlaybackSettings { Speed = 2, MaxStepsPerFrame = 7, DiagnosticEverySteps = 19, DiagnosticRadius = 0.15 });
        editor.SetGeneration(editor.Generation with { Seed = 993, Placement = PlacementKind.Disk });
        playback.SingleStep();
        ParticleSnapshot expected = Copy(playback.Observe());
        string path = PathFor("current.json");
        editor.SaveCurrent(path, "saved pattern");
        editor.ApplyPopulation(4, [1, 2, 3, 4]);
        playback.SingleStep();
        editor.LoadExperiment(path);
        Assert.True(playback.IsPaused);
        Assert.Equal(0, playback.Observe().Step);
        Assert.Equal(expected.Positions.ToArray(), playback.Observe().Positions.ToArray());
        Assert.Equal(expected.Velocities.ToArray(), playback.Observe().Velocities.ToArray());
        Assert.Equal(0x123456u, editor.Appearance.ColorForSpecies(2));
        Assert.Equal(4.75, editor.Appearance.ParticleRadius);
        Assert.Equal(2, playback.PlaybackSpeed);
        Assert.Equal(993, editor.Generation.Seed);
        Assert.False(editor.CanUndo);
        Assert.Equal(0, editor.SessionEventCount);
        playback.SingleStep();
        editor.Reset();
        Assert.Equal(expected.Positions.ToArray(), playback.Observe().Positions.ToArray());
    }

    [Fact]
    public void ExplicitReloadExecutionOverrideBecomesBaselineAndInvalidCombinationIsAtomic()
    {
        using PlaybackController playback = CreatePlayback();
        ExperimentEditor editor = new(playback);
        string path = PathFor("execution.json");
        editor.SaveCurrent(path, "saved direct");
        editor.LoadExperiment(path, ForceBackend.UniformGrid, 4);
        Assert.Equal(ForceBackend.UniformGrid, playback.Experiment.Backend);
        Assert.Equal(4, playback.Experiment.WorkerCount);
        Assert.Equal(0, editor.SessionEventCount);
        playback.SingleStep();
        ParticleSnapshot before = Copy(playback.Observe());
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.LoadExperiment(path, ForceBackend.Direct, 4));
        Same(before, playback.Observe());
        Assert.Equal(4, editor.Configuration.WorkerCount);
        editor.Reset();
        Assert.Equal(ForceBackend.UniformGrid, editor.Configuration.Backend);
        Assert.Equal(4, editor.Configuration.WorkerCount);
        Assert.True(playback.IsPaused);
        editor.LoadExperiment(path);
        Assert.Equal(ForceBackend.Direct, editor.Configuration.Backend);
        Assert.Equal(1, editor.Configuration.WorkerCount);
    }

    [Theory]
    [InlineData("invalid main")]
    [InlineData("mismatched companion")]
    [InlineData("malformed companion")]
    public void FailedLoadPreservesValidStateAndHistories(string failure)
    {
        using PlaybackController playback = CreatePlayback();
        ExperimentEditor editor = new(playback);
        string path = PathFor("load.json");
        editor.SaveCurrent(path, "file");
        editor.SetMatrixCoefficient(1, 0, -0.7);
        playback.SingleStep();
        ParticleSnapshot before = Copy(playback.Observe());
        ExperimentDefinition baseline = playback.Experiment;
        if (failure == "invalid main")
        {
            File.WriteAllText(path, "{broken");
        }
        else if (failure == "mismatched companion")
        {
            File.AppendAllText(path, " ");
        }
        else
        {
            File.WriteAllText(path + ".viewer.json", "{}");
        }

        Assert.ThrowsAny<Exception>(() => editor.LoadExperiment(path));
        Same(before, playback.Observe());
        Assert.Same(baseline, playback.Experiment);
        Assert.Equal(-0.7, editor.Configuration.Matrix[1, 0]);
        Assert.Equal(1, editor.SessionEventCount);
        Assert.True(editor.CanUndo);
    }

    [Fact]
    public void SessionReplayUsesSharedEventsAndContinuesTimelineIncludingTerminalEdits()
    {
        using PlaybackController source = CreatePlayback();
        ExperimentEditor editor = new(source);
        editor.SetMatrixCoefficient(0, 1, -0.6);
        source.SingleStep();
        editor.ApplyPopulation(2, [5, 3]);
        editor.ApplyConfiguration(new SimulationConfiguration(editor.Configuration.Parameters, editor.Configuration.Matrix, 0.003,
            ForceBackend.UniformGrid, 4));
        source.SingleStep();
        editor.Undo();
        editor.Redo();
        editor.ApplyMatrixTemplate(PresetKind.Snakes, 11);
        editor.SaveSession(PathFor("session.json"), "session");
        using PlaybackController target = CreatePlayback();
        ExperimentEditor loaded = new(target);
        loaded.LoadSession(PathFor("session.json"));
        Same(Copy(source.Observe()), target.Observe());
        Assert.True(editor.Configuration.EquivalentTo(loaded.Configuration));
        Assert.True(target.IsPaused);
        Assert.False(loaded.CanUndo);
        Assert.Equal(editor.SessionEventCount, loaded.SessionEventCount);
        loaded.SetMatrixCoefficient(1, 0, 0.9);
        target.SingleStep();
        using Simulation continuedReplay = SessionReplay.Replay(loaded.CaptureSession("continued"));
        Same(Copy(target.Observe()), continuedReplay.Observe());
        loaded.Reset();
        Same(target.Experiment.InitialState, target.Observe());
        Assert.Equal(0, loaded.SessionEventCount);
    }

    [Fact]
    public void IncrementalReplayRetainsActiveStateUntilCompletionAndSupportsCancellation()
    {
        using PlaybackController source = CreatePlayback();
        ExperimentEditor sourceEditor = new(source);
        for (int i = 0; i < 12; i++)
        {
            source.SingleStep();
        }

        sourceEditor.SaveSession(PathFor("longer.json"), "replay");
        using PlaybackController target = CreatePlayback();
        ExperimentEditor editor = new(target);
        editor.SetMatrixCoefficient(0, 1, 0.99);
        target.SingleStep();
        ParticleSnapshot original = Copy(target.Observe());
        editor.BeginSessionReplay(PathFor("longer.json"));
        Assert.True(editor.IsReplaying);
        Assert.Equal(12, editor.ReplayTerminalStep);
        Assert.False(editor.AdvanceSessionReplay(3));
        Assert.Equal(3, editor.ReplayCompletedStep);
        Same(original, target.Observe());
        Assert.Throws<InvalidOperationException>(() => editor.SetMatrixCoefficient(0, 1, 0.1));
        editor.CancelSessionReplay();
        Same(original, target.Observe());
        Assert.Equal(1, editor.SessionEventCount);
        Assert.True(editor.CanUndo);
        editor.BeginSessionReplay(PathFor("longer.json"));
        while (!editor.AdvanceSessionReplay(4))
        {
            Same(original, target.Observe());
        }

        Same(Copy(source.Observe()), target.Observe());
        Assert.False(editor.IsReplaying);
        Assert.True(target.IsPaused);
    }

    [Fact]
    public void ZeroStepSessionAppliesEveryTerminalEventWithoutHiddenAdvance()
    {
        using PlaybackController source = CreatePlayback();
        ExperimentEditor editor = new(source);
        editor.SetMatrixCoefficient(0, 1, 0.2);
        editor.ApplyPopulation(2, [4, 1]);
        editor.SetMatrixCoefficient(1, 0, -0.9);
        editor.SaveSession(PathFor("zero.json"), "zero-step");
        using PlaybackController target = CreatePlayback();
        ExperimentEditor loaded = new(target);
        loaded.BeginSessionReplay(PathFor("zero.json"));
        Assert.True(loaded.AdvanceSessionReplay(1));
        Same(Copy(source.Observe()), target.Observe());
        Assert.Equal(0, target.Observe().Step);
        Assert.Equal(-0.9, loaded.Configuration.Matrix[1, 0]);
        Assert.Equal(3, loaded.SessionEventCount);
    }

    [Fact]
    public void DisposalDuringTemporaryReplayRejectsTransferAndReleasesTemporaryOwnership()
    {
        using PlaybackController source = new(PresetGenerator.Create(PresetKind.Chains, 12, 3, backend: ForceBackend.UniformGrid,
            workerCount: 4), diagnosticEverySteps: 0);
        ExperimentEditor sourceEditor = new(source);
        source.SingleStep();
        sourceEditor.SaveSession(PathFor("dispose.json"), "disposed host");
        using PlaybackController target = CreatePlayback();
        ExperimentEditor editor = new(target);
        ParticleSnapshot before = Copy(target.Observe());
        editor.BeginSessionReplay(PathFor("dispose.json"));
        target.Dispose();
        Assert.Throws<ObjectDisposedException>(() => editor.AdvanceSessionReplay(8));
        Assert.False(editor.IsReplaying);
        Same(before, target.Observe());
        Assert.Throws<ObjectDisposedException>(() => editor.SetGeneration(editor.Generation with { Seed = 88 }));
        editor.CancelSessionReplay();
    }

    [Fact]
    public void PureHeadlessExperimentAndSessionLoadWithoutViewerCompanion()
    {
        using PlaybackController playback = CreatePlayback();
        ExperimentEditor editor = new(playback);
        Directory.CreateDirectory(directory);
        ExperimentFile.Save(PathFor("pure-experiment.json"), playback.Experiment);
        editor.LoadExperiment(PathFor("pure-experiment.json"));
        editor.SetMatrixCoefficient(1, 0, -0.8);
        playback.SingleStep();
        SessionDefinition session = editor.CaptureSession("pure session");
        SessionFile.Save(PathFor("pure-session.json"), session);
        ParticleSnapshot expected = Copy(playback.Observe());
        editor.Reset();
        editor.LoadSession(PathFor("pure-session.json"));
        Same(expected, playback.Observe());
        Assert.False(File.Exists(PathFor("pure-session.json.viewer.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private string PathFor(string name) => Path.Combine(directory, name);

    private static PlaybackController CreatePlayback() => new(PresetGenerator.Create(PresetKind.Chains, 12, 3, 41), diagnosticEverySteps: 0);

    private static ParticleSnapshot Copy(ParticleStateView state) => PlaybackControllerTests.Copy(state);

    private static void Same(ParticleSnapshot expected, ParticleStateView actual) => PlaybackControllerTests.SameState(expected, actual);
}
