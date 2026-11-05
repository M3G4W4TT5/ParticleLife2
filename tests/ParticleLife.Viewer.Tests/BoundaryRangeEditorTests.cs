using System.Text.Json;
using System.Text.Json.Nodes;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using Xunit;

namespace ParticleLife.Viewer.Tests;

public sealed class BoundaryRangeEditorTests
{
    [Fact]
    public void LivePeriodicWorldChangeRecordsActualRemappingAndUndoRetainsClock()
    {
        ExperimentDefinition source = new("off-world", new ModelParameters { Boundary = BoundaryKind.Open, ForceScale = 0 },
            new InteractionMatrix(1, [0]), new ParticleSnapshot([71], [new(-0.1, 1.1)], [new(0.2, 0.3)], [0]),
            1.0 / 144, new GeneratorMetadata("test", "1", null, "test"));
        using PlaybackController controller = new(source, diagnosticEverySteps: 0);
        ExperimentEditor editor = new(controller);
        editor.ApplyConfiguration(new(source.Parameters with { Boundary = BoundaryKind.Periodic }, source.Matrix, source.TimeStep), "Wrap world");
        ParticleStateView state = controller.Observe();
        Assert.InRange(Math.Abs(state.Positions[0].X - 0.9), 0, 1e-14);
        Assert.InRange(Math.Abs(state.Positions[0].Y - 0.1), 0, 1e-14);
        Assert.Equal(new Vector2d(0.2, 0.3), state.Velocities[0]);
        SessionDefinition session = editor.CaptureSession("mapped");
        Assert.Single(session.Events);
        Assert.NotNull(session.Events[0].ReplacementState);
        using Simulation replay = SessionReplay.Replay(SessionFile.Deserialize(SessionFile.Serialize(session)));
        Assert.Equal(state.Positions.ToArray(), replay.Observe().Positions.ToArray());
        controller.SingleStep();
        double time = controller.Observe().Time;
        editor.Undo();
        Assert.Equal(BoundaryKind.Open, controller.Configuration.Parameters.Boundary);
        Assert.Equal(new Vector2d(-0.1, 1.1), controller.Observe().Positions[0]);
        Assert.Equal(time, controller.Observe().Time);
        Assert.Equal(1, controller.Observe().Step);
        editor.Redo();
        Assert.Equal(BoundaryKind.Periodic, controller.Configuration.Parameters.Boundary);
        Assert.Equal(time, controller.Observe().Time);
        Assert.Equal(71, controller.Observe().Ids[0]);
    }

    [Fact]
    public void CoefficientEditsAndPopulationResizeRetainExistingPairRanges()
    {
        ExperimentDefinition source = PresetGenerator.Create(PresetKind.Random, 10, 2, 1);
        using PlaybackController controller = new(source, diagnosticEverySteps: 0);
        ExperimentEditor editor = new(controller);
        PairInteractionRanges ranges = new(2, [0.01, 0.02, 0.03, 0], [0.11, 0.12, 0.13, 0.14]);
        editor.ApplyConfiguration(new(source.Parameters, source.Matrix.WithRanges(ranges), source.TimeStep));
        editor.SetMatrixCoefficient(0, 1, 0.7);
        editor.ApplyMatrixTemplate(PresetKind.Chains, 2);
        editor.ResetMatrix();
        Assert.True(ranges.EquivalentTo(editor.Configuration.Matrix.Ranges));
        editor.ApplyPopulation(3, [5, 5, 0]);
        PairInteractionRanges expanded = editor.Configuration.Matrix.Ranges!;
        Assert.Equal(0.12, expanded.CutoffRadii[1]);
        Assert.Equal(0.03, expanded.CoreRadii[3]);
        Assert.Equal(source.Parameters.Cutoff, expanded.CutoffRadii[8]);
        editor.Undo();
        Assert.True(ranges.EquivalentTo(editor.Configuration.Matrix.Ranges));
    }

    [Fact]
    public void PresetRoundTripPreservesPairRadiiAndRejectsDisguisedLegacyPayloads()
    {
        using PlaybackController controller = new(PresetGenerator.Create(PresetKind.Random, 10, 2, 1));
        ExperimentEditor editor = new(controller);
        SimulationConfiguration original = editor.Configuration;
        editor.ApplyConfiguration(new(original.Parameters with { Boundary = BoundaryKind.Reflecting },
            original.Matrix.WithRanges(PairInteractionRanges.FromGlobal(2, original.Parameters)), original.TimeStep));
        string json = PresetFile.Serialize(editor.CapturePreset("radii"));
        PresetDefinition loaded = PresetFile.Deserialize(json);
        Assert.True(editor.Configuration.EquivalentTo(loaded.Configuration));
        JsonObject legacy = JsonNode.Parse(json)!.AsObject();
        legacy["schemaVersion"] = 1;
        Assert.Throws<JsonException>(() => PresetFile.Deserialize(legacy.ToJsonString()));
        legacy["configuration"]!["parameters"]!.AsObject().Remove("boundary");
        legacy["configuration"]!["matrix"]!.AsObject().Remove("pairRanges");
        PresetDefinition old = PresetFile.Deserialize(legacy.ToJsonString());
        Assert.Equal(BoundaryKind.SoftWall, old.Configuration.Parameters.Boundary);
        Assert.Null(old.Configuration.Matrix.Ranges);
    }
}
