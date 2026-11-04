using System.Text.Json;
using System.Text.Json.Nodes;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Core.Tests;

public sealed class SessionTests
{
    [Theory]
    [InlineData(ForceBackend.Direct, 1)]
    [InlineData(ForceBackend.UniformGrid, 1)]
    [InlineData(ForceBackend.UniformGrid, 4)]
    public void OrderedReplayRoundTripsActualStructuralPayloadVariableTimestepsAndTerminalEdits(ForceBackend backend, int workers)
    {
        ExperimentDefinition initial = PresetGenerator.Create(PresetKind.Random, 24, 2, 745,
            timeStep: 0.001, backend: backend, workerCount: workers);
        using Simulation edited = initial.CreateSimulation();
        List<SessionEvent> events = [];
        Apply(new(initial.Parameters with { ForceScale = 0.7 }, initial.Matrix, 0.001, backend, workers));
        edited.Step();
        Apply(new(initial.Parameters with { DampingHalfLife = null }, new InteractionMatrix(2, [0.1, 1, -0.5, 0]),
            0.002, backend, workers));
        Apply(new(edited.Parameters with { Cutoff = double.BitIncrement(0.125) }, edited.Matrix, 0.002, backend, workers));
        edited.Step();
        edited.Step();
        ParticleSnapshot boundary = edited.CaptureSnapshot();
        long[] ids = [.. boundary.Ids, 991];
        Vector2d[] positions = [.. boundary.Positions, new(-0.1, 1.1)];
        Vector2d[] velocities = [.. boundary.Velocities, new(0.1, -0.1)];
        int[] species = [.. boundary.Species, 2];
        ParticleSnapshot grown = new(ids, positions, velocities, species, boundary.Time, boundary.Step);
        Apply(new(edited.Parameters, new InteractionMatrix(3, [0.1, 1, 0, -0.5, 0, 0, 0.2, 0, 0]),
            0.0005, backend, workers), grown);
        edited.Step();
        edited.Step();
        Apply(new(edited.Parameters with { WorldWidth = 2, WallStrength = 0 }, edited.Matrix, 0.003, backend, workers));
        SessionDefinition session = new(initial, events, edited.Observe().Step);
        string json = SessionFile.Serialize(session);
        SessionDefinition saved = SessionFile.Deserialize(json);
        Assert.Equal(json, SessionFile.Serialize(saved));
        Assert.Equal(events.Count, saved.Events.Count);
        Assert.Equal(grown.Ids.ToArray(), saved.Events[3].ReplacementState!.Ids.ToArray());
        using SessionReplayRunner replay = new(saved);
        Assert.Equal(1, replay.AppliedEventCount);
        replay.RunToEnd();
        Assert.True(replay.IsComplete);
        Assert.Equal(events.Count, replay.AppliedEventCount);
        Assert.NotNull(replay.LastCompletedExecution);
        Assert.Equal(0.0005, replay.LastCompletedConfiguration!.TimeStep);
        Assert.Null(replay.Simulation.LastExecution);
        Assert.True(edited.Configuration.EquivalentTo(replay.Simulation.Configuration));
        // Same runtime, backend and source accumulation order reproduce these saved doubles exactly.
        ReconfigurationTests.AssertExactState(edited.CaptureSnapshot(), replay.Simulation.CaptureSnapshot());
        Assert.Equal(0.001 + 0.002 + 0.002 + 0.0005 + 0.0005, replay.Simulation.Observe().Time);

        void Apply(SimulationConfiguration configuration, ParticleSnapshot? state = null)
        {
            SessionEvent change = new(edited.Observe().Step, configuration, state, $"Edit {events.Count}");
            edited.Reconfigure(configuration, state);
            events.Add(change);
        }
    }

    [Fact]
    public void ZeroStepSessionAppliesEverySameBoundaryEventAndTransfersOwnershipOnce()
    {
        ExperimentDefinition initial = PresetGenerator.Create(PresetKind.Random, 0, 1,
            backend: ForceBackend.UniformGrid, workerCount: 4);
        SimulationConfiguration first = new(initial.Parameters with { ForceScale = 3 }, initial.Matrix, 0.01,
            ForceBackend.UniformGrid, 4);
        SimulationConfiguration second = new(initial.Parameters with { ForceScale = 0.2 }, initial.Matrix, 0.02,
            ForceBackend.UniformGrid, 2);
        SessionDefinition session = new(initial, [new(0, first), new(0, second)], 0);
        using SessionReplayRunner runner = new(SessionFile.Deserialize(SessionFile.Serialize(session)));
        Assert.True(runner.IsComplete);
        Assert.Equal(2, runner.AppliedEventCount);
        Assert.Null(runner.LastCompletedExecution);
        Assert.Equal(0, runner.Simulation.Observe().Step);
        Assert.True(second.EquivalentTo(runner.Simulation.Configuration));
        Assert.Throws<InvalidOperationException>(runner.AdvanceOneStep);
        using Simulation detached = runner.DetachSimulation();
        Assert.Throws<InvalidOperationException>(runner.DetachSimulation);
        runner.Dispose();
        detached.Step();
        Assert.Equal(1, detached.Observe().Step);
        Assert.Throws<ObjectDisposedException>(runner.DetachSimulation);
    }

    [Fact]
    public void OverridesAffectEveryEventWithoutMutatingRecordedChoices()
    {
        ExperimentDefinition initial = PresetGenerator.Create(PresetKind.Random, 12, 2, 18,
            timeStep: 0.001, backend: ForceBackend.UniformGrid, workerCount: 4);
        SimulationConfiguration direct = new(initial.Parameters, initial.Matrix, 0.002);
        SimulationConfiguration grid = new(initial.Parameters, initial.Matrix, 0.003, ForceBackend.UniformGrid, 2);
        SessionDefinition session = new(initial, [new(1, direct), new(2, grid)], 3);
        ReplayOverrides overrides = new(ForceBackend.UniformGrid, 8);
        using SessionReplayRunner runner = new(session, overrides);
        while (!runner.IsComplete)
        {
            runner.AdvanceOneStep();
            Assert.Equal(8, runner.Simulation.WorkerCount);
            Assert.Equal(ForceBackend.UniformGrid, runner.LastCompletedExecution!.Value.RequestedBackend);
            Assert.Equal(8, runner.LastCompletedExecution.Value.ConfiguredWorkerCount);
        }

        Assert.Same(overrides, runner.Overrides);
        Assert.Equal(4, session.InitialExperiment.WorkerCount);
        Assert.Equal(ForceBackend.Direct, session.Events[0].Configuration.Backend);
        Assert.Equal(2, session.Events[1].Configuration.WorkerCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionReplayRunner(session, new(workerCount: 4)));
    }

    [Fact]
    public void RecordedCoordinateFallbackHistorySurvivesTerminalConfigurationChanges()
    {
        ModelParameters parameters = new() { ForceScale = 0, WallStrength = 0, DampingHalfLife = null };
        ExperimentDefinition initial = new("far", parameters, new InteractionMatrix(1, [0]),
            new ParticleSnapshot([1], [new(1e100, 0)], [default], [0]), 0.01,
            new GeneratorMetadata("exact", "1", 0, "test"), ForceBackend.UniformGrid, 4);
        SessionDefinition session = new(initial, [new(2, new(parameters, initial.Matrix, 0.02))], 2);
        using SessionReplayRunner runner = new(session);
        runner.RunToEnd();
        Assert.Equal(2, runner.Simulation.DirectFallbackSteps);
        Assert.Equal(0, runner.Simulation.AccumulationFallbackSteps);
        Assert.Equal(GridFallbackReason.CoordinateRange, runner.LastCompletedExecution!.Value.FallbackReason);
        Assert.Equal(ForceBackend.Direct, runner.Simulation.Backend);
        Assert.Null(runner.Simulation.LastExecution);
    }

    [Fact]
    public void MismatchedStructuralClockIsRejectedBeforePublishingTheEditAndCannotBeSkipped()
    {
        ExperimentDefinition initial = PresetGenerator.Create(PresetKind.Random, 0, 1, timeStep: 0.01);
        SimulationConfiguration candidate = new(initial.Parameters with { ForceScale = 2 }, initial.Matrix, 0.02);
        SessionDefinition session = new(initial, [new(1, candidate, new ParticleSnapshot([], [], [], [], 0.02, 1))], 1);
        using SessionReplayRunner runner = new(session);
        Assert.Throws<ArgumentException>(runner.AdvanceOneStep);
        Assert.Equal(1, runner.Simulation.Observe().Step);
        Assert.Equal(0.01, runner.Simulation.Observe().Time);
        Assert.Equal(0, runner.AppliedEventCount);
        Assert.Equal(initial.Parameters, runner.Simulation.Parameters);
        Assert.Throws<ArgumentException>(runner.AdvanceOneStep);
        Assert.Equal(1, runner.Simulation.Observe().Step);
    }

    [Fact]
    public void SessionRejectsUnorderedBeyondTerminalAndIncompatibleSpeciesChanges()
    {
        ExperimentDefinition initial = PresetGenerator.Create(PresetKind.Random, 0, 1);
        SimulationConfiguration configuration = new(initial.Parameters, initial.Matrix, initial.TimeStep);
        Assert.Throws<ArgumentException>(() => new SessionDefinition(initial, [new(2, configuration), new(1, configuration)], 2));
        Assert.Throws<ArgumentException>(() => new SessionDefinition(initial, [new(2, configuration)], 1));
        ParticleSnapshot speciesTwo = new([1], [default], [default], [1]);
        ExperimentDefinition two = new("two", initial.Parameters, new InteractionMatrix(2, new double[4]), speciesTwo,
            initial.TimeStep, initial.Generator);
        Assert.Throws<ArgumentException>(() => new SessionDefinition(two, [new(0, configuration)], 0));
    }

    [Theory]
    [InlineData("format")]
    [InlineData("schemaVersion")]
    [InlineData("initialExperiment")]
    [InlineData("events")]
    [InlineData("terminalStep")]
    public void SessionMissingRequiredRootFieldsAreRejected(string field)
    {
        JsonObject json = JsonNode.Parse(ValidJson())!.AsObject();
        json.Remove(field);
        Assert.Throws<JsonException>(() => SessionFile.Deserialize(json.ToJsonString()));
    }

    [Fact]
    public void SessionRejectsDuplicateUnknownAndUnsupportedContractFieldsIncludingNestedObjects()
    {
        string json = ValidJson();
        Assert.Throws<JsonException>(() => SessionFile.Deserialize(json.Replace("\"terminalStep\": 1", "\"terminalStep\": 1, \"terminalStep\": 1")));
        Assert.Throws<JsonException>(() => SessionFile.Deserialize(json.Replace("\"label\":", "\"unexpected\": true, \"label\":")));
        Assert.Throws<JsonException>(() => SessionFile.Deserialize(json.Replace("\"forceScale\": 1", "\"forceScale\": 1, \"forceScale\": 1")));
        Assert.Throws<JsonException>(() => SessionFile.Deserialize(json.Replace("\"boundaryStep\": 0", "\"boundaryStep\": 0, \"boundaryStep\": 0")));
        Assert.Throws<JsonException>(() => SessionFile.Deserialize("null"));
        JsonNode unsupported = JsonNode.Parse(json)!;
        unsupported["schemaVersion"] = 999;
        Assert.Throws<NotSupportedException>(() => SessionFile.Deserialize(unsupported.ToJsonString()));
        unsupported = JsonNode.Parse(json)!;
        unsupported["format"] = "particle-life-run-report";
        Assert.Throws<NotSupportedException>(() => SessionFile.Deserialize(unsupported.ToJsonString()));
        Assert.Throws<JsonException>(() => ExperimentFile.Deserialize(json));
    }

    [Fact]
    public void SessionRejectsIncompleteNonfiniteNullAndMismatchedStructuralValues()
    {
        JsonNode json = JsonNode.Parse(ValidJson())!;
        json["events"]![0]!["configuration"]!["parameters"]!.AsObject().Remove("dampingHalfLife");
        Assert.Throws<JsonException>(() => SessionFile.Deserialize(json.ToJsonString()));
        json = JsonNode.Parse(ValidJson())!;
        json["events"]![0]!["configuration"]!["timeStep"] = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionFile.Deserialize(json.ToJsonString()));
        json = JsonNode.Parse(ValidJson())!;
        json["events"]![0] = null;
        Assert.Throws<JsonException>(() => SessionFile.Deserialize(json.ToJsonString()));
        json = JsonNode.Parse(ValidJson())!;
        json["events"]![0]!["replacementState"]!["positions"]![0] = null;
        Assert.Throws<JsonException>(() => SessionFile.Deserialize(json.ToJsonString()));
        json = JsonNode.Parse(ValidJson())!;
        json["events"]![0]!["replacementState"]!["species"]![0] = 9;
        Assert.Throws<ArgumentException>(() => SessionFile.Deserialize(json.ToJsonString()));
        Assert.ThrowsAny<ArgumentException>(() => SessionFile.Deserialize(ValidJson().Replace("\"timeStep\": 0.01", "\"timeStep\": 1e999")));
    }

    [Fact]
    public void SafeSessionSaveNeverOverwritesAnExistingFileOrChangesTheSimulation()
    {
        ExperimentDefinition initial = PresetGenerator.Create(PresetKind.Random, 8, 2);
        using Simulation simulation = initial.CreateSimulation();
        simulation.Step();
        ParticleSnapshot before = simulation.CaptureSnapshot();
        SessionDefinition session = new(initial, [], before.Step);
        string path = Path.Combine(Path.GetTempPath(), $"particle-life-session-{Guid.NewGuid():N}.json");
        try
        {
            SessionFile.Save(path, session);
            string original = File.ReadAllText(path);
            Assert.Throws<IOException>(() => SessionFile.Save(path, session));
            Assert.Equal(original, File.ReadAllText(path));
            using Simulation replay = SessionReplay.Replay(SessionFile.Load(path));
            ReconfigurationTests.AssertExactState(before, replay.CaptureSnapshot());
            ReconfigurationTests.AssertExactState(before, simulation.CaptureSnapshot());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CurrentStateExperimentProvenanceRoundTripsWithoutChangingInitialExperimentMeaning()
    {
        ExperimentDefinition initial = PresetGenerator.Create(PresetKind.Random, 8, 2);
        using Simulation simulation = initial.CreateSimulation();
        simulation.Step();
        ParticleSnapshot source = simulation.CaptureSnapshot();
        ParticleSnapshot rebased = new(source.Ids, source.Positions, source.Velocities, source.Species);
        ExperimentDefinition fromHere = new("from here", simulation.Parameters, simulation.Matrix, rebased,
            simulation.TimeStep, initial.Generator, simulation.Backend, simulation.WorkerCount,
            new ExperimentProvenance(source.Time, source.Step, "current-state capture", initial.Generator, initial.Name));
        ExperimentDefinition saved = ExperimentFile.Deserialize(ExperimentFile.Serialize(fromHere));
        Assert.Equal(0, saved.InitialState.Time);
        Assert.Equal(0, saved.InitialState.Step);
        Assert.Equal(source.Time, saved.Provenance!.SourceTime);
        Assert.Equal(source.Step, saved.Provenance.SourceStep);
        Assert.Equal(initial.Generator, saved.Provenance.SourceGenerator);
        Assert.Equal(initial.Name, saved.Provenance.SourceName);
        Assert.Equal(source.Positions.ToArray(), saved.InitialState.Positions.ToArray());
        Assert.Same(saved.Provenance, saved.WithBackend(ForceBackend.UniformGrid).Provenance);
        JsonNode json = JsonNode.Parse(ExperimentFile.Serialize(saved))!;
        json["provenance"]!.AsObject().Remove("sourceStep");
        Assert.Throws<JsonException>(() => ExperimentFile.Deserialize(json.ToJsonString()));
        json = JsonNode.Parse(ExperimentFile.Serialize(saved))!;
        json["provenance"]!["sourceGenerator"]!.AsObject().Remove("runtime");
        Assert.Throws<JsonException>(() => ExperimentFile.Deserialize(json.ToJsonString()));
        json = JsonNode.Parse(ExperimentFile.Serialize(saved))!;
        json["provenance"]!["sourceName"] = " ";
        Assert.Throws<ArgumentException>(() => ExperimentFile.Deserialize(json.ToJsonString()));
        json = JsonNode.Parse(ExperimentFile.Serialize(saved))!;
        json["provenance"]!.AsObject().Remove("sourceGenerator");
        json["provenance"]!.AsObject().Remove("sourceName");
        Assert.Null(ExperimentFile.Deserialize(json.ToJsonString()).Provenance!.SourceGenerator);
        Assert.Throws<ArgumentException>(() => new ExperimentProvenance(0, 0, "capture", new(" ", "1", 0, "runtime")));
    }

    [Fact]
    public void StructuralGenerationProvenanceRoundTripsButNeverRegeneratesTheSavedPayload()
    {
        ExperimentDefinition initial = PresetGenerator.Create(PresetKind.Random, 0, 1, timeStep: 0.01);
        SimulationConfiguration configuration = new(initial.Parameters, initial.Matrix, initial.TimeStep);
        ParticleSnapshot actual = new([991], [new(-0.2, 1.4)], [new(0.25, -0.125)], [0]);
        GeneratorMetadata metadata = new("uniform-rectangle/constant-velocity", "1", 81723, "test-runtime");
        SessionDefinition session = new(initial, [new(0, configuration, actual, "Add actual generated particle", metadata)], 0);
        SessionDefinition saved = SessionFile.Deserialize(SessionFile.Serialize(session));
        Assert.Equal(metadata, saved.Events[0].Generator);
        using Simulation replay = SessionReplay.Replay(saved);
        ReconfigurationTests.AssertExactState(actual, replay.CaptureSnapshot());
        SessionDefinition matrixOnly = new(initial, [new(0, configuration, generator: metadata)], 0);
        Assert.Equal(metadata, SessionFile.Deserialize(SessionFile.Serialize(matrixOnly)).Events[0].Generator);
        JsonNode json = JsonNode.Parse(SessionFile.Serialize(session))!;
        json["events"]![0]!["generator"]!.AsObject().Remove("seed");
        Assert.Throws<JsonException>(() => SessionFile.Deserialize(json.ToJsonString()));
        json = JsonNode.Parse(SessionFile.Serialize(session))!;
        json["events"]![0]!["generator"]!["runtime"] = " ";
        Assert.Throws<ArgumentException>(() => SessionFile.Deserialize(json.ToJsonString()));
    }

    [Fact]
    public void OwnedEventOrderCannotBeChangedThroughTheCallersOriginalCollection()
    {
        ExperimentDefinition initial = PresetGenerator.Create(PresetKind.Random, 0, 1);
        SimulationConfiguration configuration = new(initial.Parameters, initial.Matrix, initial.TimeStep);
        List<SessionEvent> original = [new(0, configuration)];
        SessionDefinition session = new(initial, original, 1);
        original.Clear();
        Assert.Single(session.Events);
        Assert.Throws<NotSupportedException>(() => ((IList<SessionEvent>)session.Events).Clear());
    }

    private static string ValidJson()
    {
        ExperimentDefinition initial = PresetGenerator.Create(PresetKind.Random, 1, 1, timeStep: 0.01);
        SimulationConfiguration configuration = new(initial.Parameters, initial.Matrix, initial.TimeStep);
        return SessionFile.Serialize(new(initial, [new(0, configuration, initial.InitialState)], 1));
    }
}
