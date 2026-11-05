using System.Text.Json;
using System.Text.Json.Nodes;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Core.Tests;

public sealed class ParticleDeltaTests
{
    [Fact]
    public void ChunkedHistoryCheckpointRestoresExactBitsAcrossChunkBoundaries()
    {
        const int count = 4101;
        long[] ids = new long[count];
        Vector2d[] positions = new Vector2d[count], velocities = new Vector2d[count];
        int[] species = new int[count];
        for (int i = 0; i < count; i++)
        {
            ids[i] = long.MaxValue - i;
            positions[i] = new(i * 0.03125, i % 2 == 0 ? -0.0 : double.Epsilon);
            velocities[i] = new(-1e150, 1e-150);
            species[i] = i % 3;
        }
        ParticleSnapshot source = new(ids, positions, velocities, species, 2.75, 321);
        CompressedParticleSnapshot stored = CompressedParticleSnapshot.Pack(source);
        Assert.InRange(stored.StorageBytes, 1, 44L * count);
        ParticleSnapshot restored = stored.Restore();
        Assert.Equal(ids, restored.Ids.ToArray());
        Assert.Equal(species, restored.Species.ToArray());
        Assert.Equal(2.75, restored.Time);
        Assert.Equal(321, restored.Step);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(positions[i].X), BitConverter.DoubleToInt64Bits(restored.Positions[i].X));
            Assert.Equal(BitConverter.DoubleToInt64Bits(positions[i].Y), BitConverter.DoubleToInt64Bits(restored.Positions[i].Y));
            Assert.Equal(velocities[i], restored.Velocities[i]);
        }
        Assert.Equal(0, CompressedParticleSnapshot.Pack(new([], [], [], [])).Restore().Count);
    }

    [Fact]
    public void CompactActualEditsPreserveSurvivorOrderUntouchedBitsAndAppendedIdentities()
    {
        ParticleSnapshot before = new([9, 3, long.MaxValue], [new(1, -0.0), new(2, 2), new(3, 3)],
            [new(0.1, 0.2), new(0.3, 0.4), new(0.5, 0.6)], [0, 1, 0], 0.25, 5);
        ParticleSnapshot after = new([9, long.MaxValue, 27], [new(1, -0.0), new(4, 4), new(5, 5)],
            [new(0.1, 0.2), new(0.7, 0.8), new(0.9, 1)], [0, 1, 0], 0.25, 5);
        ParticleStateDelta delta = ParticleStateDelta.TryCreate(before, after)!;
        Assert.Equal([3L], delta.RemovedIds.ToArray());
        Assert.Equal([long.MaxValue], delta.Updates.Ids.ToArray());
        Assert.Equal([27L], delta.Additions.Ids.ToArray());
        ParticleSnapshot applied = delta.Apply(before.Observe());
        Assert.Equal(after.Ids.ToArray(), applied.Ids.ToArray());
        Assert.Equal(after.Positions.ToArray(), applied.Positions.ToArray());
        Assert.Equal(after.Velocities.ToArray(), applied.Velocities.ToArray());
        Assert.Equal(after.Species.ToArray(), applied.Species.ToArray());
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits(applied.Positions[0].Y));
        Assert.Equal(new Vector2d(3, 3), before.Positions[2]);
    }

    [Fact]
    public void DeltaRecordsSignedZeroChangesAndFallsBackForReordering()
    {
        ParticleSnapshot before = new([1, 2], [new(0, 0), new(1, 1)], [default, default], [0, 0]);
        ParticleSnapshot after = new([1, 2], [new(-0.0, 0), new(1, 1)], [default, default], [0, 0]);
        ParticleStateDelta delta = ParticleStateDelta.TryCreate(before, after)!;
        Assert.Equal(1, delta.Updates.Count);
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits(delta.Apply(before.Observe()).Positions[0].X));
        ParticleSnapshot reordered = new([2, 1], [new(1, 1), new(0, 0)], [default, default], [0, 0]);
        Assert.Null(ParticleStateDelta.TryCreate(before, reordered));
    }

    [Fact]
    public void DeltaRejectsAbsentOrExistingIdentitiesAndWrongClockWithoutMutatingSource()
    {
        ParticleSnapshot state = new([1], [new(0.2, 0.3)], [default], [0]);
        ParticleSnapshot empty = new([], [], [], []);
        ParticleStateDelta missing = new([2], empty, empty);
        Assert.Throws<ArgumentException>(() => missing.Apply(state.Observe()));
        ParticleStateDelta duplicate = new([], empty, state);
        Assert.Throws<ArgumentException>(() => duplicate.Apply(state.Observe()));
        ParticleSnapshot future = new([], [], [], [], 0.1, 1);
        ParticleStateDelta wrongTime = new([], future, future);
        Assert.Throws<ArgumentException>(() => wrongTime.Apply(state.Observe()));
        Assert.Equal(new Vector2d(0.2, 0.3), state.Positions[0]);
    }

    [Fact]
    public void SessionRoundTripReplaysCompactTerminalEditsAndRejectsInvalidMembership()
    {
        ExperimentDefinition initial = PresetGenerator.Create(PresetKind.Random, 12, 2, 7);
        using Simulation simulation = initial.CreateSimulation();
        simulation.Step();
        ParticleSnapshot before = simulation.CaptureSnapshot();
        Vector2d[] positions = before.Positions.ToArray();
        positions[2] = new(0.4, 0.6);
        ParticleSnapshot after = new(before.Ids, positions, before.Velocities, before.Species, before.Time, before.Step);
        ParticleStateDelta delta = ParticleStateDelta.TryCreate(before, after)!;
        SessionEvent change = new(1, simulation.Configuration, label: "Move one", stateDelta: delta);
        SessionDefinition session = new(initial, [change], 1);
        string json = SessionFile.Serialize(session);
        Assert.Contains("\"stateDelta\"", json, StringComparison.Ordinal);
        SessionDefinition loaded = SessionFile.Deserialize(json);
        Assert.Null(loaded.Events[0].ReplacementState);
        Assert.Equal(1, loaded.Events[0].StateDelta!.Updates.Count);
        using Simulation replay = SessionReplay.Replay(loaded);
        Assert.Equal(after.Positions.ToArray(), replay.Observe().Positions.ToArray());
        Assert.Equal(after.Velocities.ToArray(), replay.Observe().Velocities.ToArray());
        ParticleSnapshot empty = new([], [], [], [], before.Time, 1);
        SessionEvent absent = new(1, simulation.Configuration, stateDelta: new ParticleStateDelta([999], empty, empty));
        Assert.Throws<ArgumentException>(() => new SessionDefinition(initial, [absent], 1));
        Assert.Throws<ArgumentException>(() => new SessionEvent(1, simulation.Configuration, after, stateDelta: delta));
    }

    [Fact]
    public void LegacySessionTwoKeepsFullStateMeaningAndRejectsDisguisedDeltaField()
    {
        ExperimentDefinition initial = PresetGenerator.Create(PresetKind.Random, 2, 1, 7);
        SimulationConfiguration configuration = new(initial.Parameters, initial.Matrix, initial.TimeStep);
        SessionDefinition session = new(initial, [new SessionEvent(0, configuration, initial.InitialState)], 0);
        JsonObject legacy = JsonNode.Parse(SessionFile.Serialize(session))!.AsObject();
        legacy["schemaVersion"] = 2;
        Assert.Throws<JsonException>(() => SessionFile.Deserialize(legacy.ToJsonString()));
        legacy["events"]![0]!.AsObject().Remove("stateDelta");
        SessionDefinition loaded = SessionFile.Deserialize(legacy.ToJsonString());
        Assert.NotNull(loaded.Events[0].ReplacementState);
        Assert.Null(loaded.Events[0].StateDelta);
        legacy["schemaVersion"] = 3;
        Assert.Throws<JsonException>(() => SessionFile.Deserialize(legacy.ToJsonString()));
    }
}
