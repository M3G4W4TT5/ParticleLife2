using System.Text.Json;
using System.Text.Json.Nodes;
using ParticleLife.Core.Execution;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using Xunit;

namespace ParticleLife.Core.Tests;

public sealed class ExecutionCaptureTests
{
    [Fact]
    public void ExactRestoreRejectsLossyFloatConversionWhileExplicitTransferKeepsIdentityAndClock()
    {
        ParticleSnapshot state = new([long.MinValue, long.MaxValue], [new(0.1, -0.0), new(0.7, 0.2)],
            [new(0.03, -0.04), new(0, 0)], [0, 1], 1.25, 123);
        Assert.Throws<InvalidOperationException>(() => CaptureTransfer.Convert(state, StatePrecision.Float32));
        ParticleSnapshot converted = CaptureTransfer.Convert(state, StatePrecision.Float32, allowPrecisionConversion: true);
        Assert.True(CaptureTransfer.IsExactFloat32(converted));
        Assert.Equal(state.Ids.ToArray(), converted.Ids.ToArray());
        Assert.Equal(state.Species.ToArray(), converted.Species.ToArray());
        Assert.Equal(state.Time, converted.Time);
        Assert.Equal(state.Step, converted.Step);
        Assert.Equal((double)(float)0.1, converted.Positions[0].X);
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits(converted.Positions[0].Y));
        Assert.Same(converted, CaptureTransfer.Convert(converted, StatePrecision.Float64));
    }

    [Fact]
    public void FloatCaptureRejectsUnrepresentableClaimsAndOverflow()
    {
        SimulationConfiguration configuration = Configuration();
        ParticleSnapshot imprecise = new([1], [new(0.1, 0)], [default], [0]);
        Assert.Throws<ArgumentException>(() => new SimulationCapture(configuration, imprecise, Gpu(), 0, "terminal"));
        ParticleSnapshot enormous = new([1], [new(double.MaxValue, 0)], [default], [0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => CaptureTransfer.Convert(enormous, StatePrecision.Float32, true));
    }

    [Fact]
    public void CaptureCodecRoundTripsActualFloatBitsHighIdentitiesAndExecutionMetadataWithoutGraphics()
    {
        SimulationConfiguration configuration = Configuration();
        ParticleSnapshot state = new([long.MinValue, long.MaxValue], [new(0.25, -0.0), new(0.75, 0.5)],
            [new(0.125, -0.25), default], [0, 1]);
        SessionDefinition actions = new(Experiment(configuration, state), [], 0);
        ExecutionProvenance gpu = Gpu();
        SimulationCapture capture = new(configuration, state, gpu, 0, "terminal");
        CapturedSessionDefinition original = new(actions, [capture],
            [new(0, 0, 0, ExecutionProvenance.Cpu(configuration), gpu, false)]);
        string json = ExecutionCaptureFile.Serialize(original);
        CapturedSessionDefinition restored = ExecutionCaptureFile.Deserialize(json);
        Assert.Equal(gpu, restored.TerminalCapture.Execution);
        Assert.Equal(state.Ids.ToArray(), restored.TerminalCapture.State.Ids.ToArray());
        Assert.Equal(state.Velocities.ToArray(), restored.TerminalCapture.State.Velocities.ToArray());
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits(restored.TerminalCapture.State.Positions[0].Y));
        Assert.Equal(SessionFile.Serialize(actions), SessionFile.Serialize(restored.Actions));
        Assert.Single(restored.Transitions);
        Assert.False(restored.TerminalCapture.Execution.RepeatableActions);
    }

    [Fact]
    public void TerminalCaptureIncludesFinalSameBoundaryEditsAndSettings()
    {
        SimulationConfiguration before = Configuration();
        SimulationConfiguration after = new(before.Parameters with { ForceScale = 2 }, before.Matrix, before.TimeStep);
        ParticleSnapshot state = new([19], [new(0.25, 0.5)], [default], [0]);
        SessionDefinition actions = new(Experiment(before, state), [new(0, after)], 0);
        SimulationCapture early = new(before, state, Gpu(), 0, "periodic");
        Assert.Throws<ArgumentException>(() => new CapturedSessionDefinition(actions, [early], []));
        SimulationCapture terminal = new(after, state, Gpu(), 1, "terminal");
        CapturedSessionDefinition valid = new(actions, [early, terminal], []);
        Assert.Equal(2, ExecutionCaptureFile.Deserialize(ExecutionCaptureFile.Serialize(valid)).Captures.Count);
        SimulationCapture wrongSettings = new(before, state, Gpu(), 1, "terminal");
        Assert.Throws<ArgumentException>(() => new CapturedSessionDefinition(actions, [wrongSettings], []));
    }

    [Fact]
    public void CaptureCodecRejectsUnknownDuplicateFieldsUnsupportedVersionsAndUnbackedTransfers()
    {
        SimulationConfiguration configuration = Configuration();
        ParticleSnapshot state = new([], [], [], []);
        SessionDefinition actions = new(Experiment(configuration, state), [], 0);
        CapturedSessionDefinition session = new(actions, [new(configuration, state, Gpu(), 0, "terminal")], []);
        string json = ExecutionCaptureFile.Serialize(session);
        JsonObject changed = JsonNode.Parse(json)!.AsObject();
        changed["unrecognized"] = true;
        Assert.Throws<JsonException>(() => ExecutionCaptureFile.Deserialize(changed.ToJsonString()));
        changed.Remove("unrecognized");
        changed["schemaVersion"] = 99;
        Assert.Throws<NotSupportedException>(() => ExecutionCaptureFile.Deserialize(changed.ToJsonString()));
        Assert.Throws<JsonException>(() => ExecutionCaptureFile.Deserialize(json.Replace("\"schemaVersion\": 1,",
            "\"schemaVersion\": 1, \"schemaVersion\": 1,", StringComparison.Ordinal)));
        Assert.Throws<ArgumentException>(() => new CapturedSessionDefinition(actions, session.Captures,
            [new(0, 0, 0, Gpu(), ExecutionProvenance.Cpu(configuration), false)]));
    }

    [Fact]
    public void CapturesRequireRecordedIdentityAndSpeciesChangesIncludingCompactDeltas()
    {
        SimulationConfiguration configuration = Configuration();
        ParticleSnapshot initial = new([7, 9], [new(0.25, 0.5), new(0.5, 0.5)], [default, default], [0, 0]);
        ParticleSnapshot final = new([9, 11], [new(0.75, 0.5), new(0.5, 0.25)], [default, default], [1, 0]);
        SimulationCapture capture = new(configuration, final, Gpu(), 0, "terminal");
        SessionDefinition withoutEdits = new(Experiment(configuration, initial), [], 0);
        Assert.Throws<ArgumentException>(() => new CapturedSessionDefinition(withoutEdits, [capture], []));
        ParticleStateDelta delta = ParticleStateDelta.TryCreate(initial, final)!;
        SessionDefinition withEdits = new(Experiment(configuration, initial),
            [new(0, configuration, stateDelta: delta)], 0);
        SimulationCapture recorded = new(configuration, final, Gpu(), 1, "terminal");
        CapturedSessionDefinition valid = new(withEdits, [recorded], []);
        Assert.Equal(final.Ids.ToArray(), ExecutionCaptureFile.Deserialize(ExecutionCaptureFile.Serialize(valid)).TerminalCapture.State.Ids.ToArray());
        ParticleSnapshot missing = new([9], [new(0.75, 0.5)], [default], [1]);
        Assert.Throws<ArgumentException>(() => new CapturedSessionDefinition(withEdits,
            [new(configuration, missing, Gpu(), 1, "terminal")], []));
    }

    private static SimulationConfiguration Configuration() => new(new ModelParameters(), new(2, [0, 0.5, -0.25, 0]), 1.0 / 64);
    private static ExecutionProvenance Gpu() => new(ExecutionHardware.Gpu, StatePrecision.Float32,
        "test-resident/1", "test-runtime", "test-device", "test-api", "test-driver", false);
    private static ExperimentDefinition Experiment(SimulationConfiguration configuration, ParticleSnapshot state) => new("capture fixture",
        configuration.Parameters, configuration.Matrix, state, configuration.TimeStep, new("actual-state", "1", null, "test"));
}
