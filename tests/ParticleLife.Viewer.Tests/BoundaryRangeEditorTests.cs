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
    public void SelectedRangeEditAndUndoKeepDirectionAndOtherPairs()
    {
        using PlaybackController controller = new(PresetGenerator.Create(PresetKind.Random, 10, 3, 1));
        ExperimentEditor editor = new(controller);
        editor.SetSelectedPairRanges([1, 5], 0, 0.2);
        PairInteractionRanges ranges = editor.Configuration.Matrix.Ranges!;
        Assert.Equal(0.2, ranges.CutoffRadii[1]);
        Assert.Equal(0.2, ranges.CutoffRadii[5]);
        Assert.Equal(0.1, ranges.CutoffRadii[3]);
        Assert.Equal(0, ranges.CoreRadii[1]);
        Assert.Single(editor.CaptureSession("selection").Events);
        editor.Undo();
        Assert.Null(editor.Configuration.Matrix.Ranges);
        editor.Redo();
        Assert.True(ranges.EquivalentTo(editor.Configuration.Matrix.Ranges));
        editor.SetPairRange(0, 2, 0.04, 0.3, linkReverse: true);
        Assert.Equal(0.3, editor.Configuration.Matrix.Ranges!.CutoffRadii[6]);
        Assert.Equal(0.04, editor.Configuration.Matrix.Ranges.CoreRadii[2]);
    }

    [Fact]
    public void SelectiveRandomizationRetainsUnselectedQuantitiesAndReplaysActualValues()
    {
        using PlaybackController controller = new(PresetGenerator.Create(PresetKind.Random, 10, 2, 1));
        ExperimentEditor editor = new(controller);
        editor.EnablePairRanges(true);
        PairInteractionRanges original = editor.Configuration.Matrix.Ranges!;
        editor.RandomizeInteractionSelection([1], InteractionRandomization.Rules, 7, double.NaN, double.NaN);
        Assert.True(original.EquivalentTo(editor.Configuration.Matrix.Ranges));
        double[] rules = editor.Configuration.Matrix.Values.ToArray();
        editor.RandomizeInteractionSelection([1], InteractionRandomization.Radii, 11, 0.15, 0.2, 0, linkReverse: true);
        Assert.Equal(rules, editor.Configuration.Matrix.Values.ToArray());
        PairInteractionRanges ranges = editor.Configuration.Matrix.Ranges!;
        Assert.Equal(ranges.CutoffRadii[1], ranges.CutoffRadii[2]);
        Assert.InRange(ranges.CutoffRadii[1], 0.15, 0.2);
        Assert.Equal(0, ranges.CoreRadii[1]);
        Assert.Equal(original.CutoffRadii[0], ranges.CutoffRadii[0]);
        SessionDefinition recorded = SessionFile.Deserialize(SessionFile.Serialize(editor.CaptureSession("actual radii")));
        using Simulation replay = SessionReplay.Replay(recorded);
        Assert.True(editor.Configuration.EquivalentTo(replay.Configuration));
        editor.EnablePairRanges(false);
        Assert.Null(editor.Configuration.Matrix.Ranges);
        editor.Undo();
        Assert.True(ranges.EquivalentTo(editor.Configuration.Matrix.Ranges));
    }

    [Fact]
    public void InvalidSelectedRadiiRejectAtomicallyIncludingPeriodicLimit()
    {
        ExperimentDefinition source = PresetGenerator.Create(PresetKind.Random, 10, 2, 1);
        using PlaybackController controller = new(source);
        ExperimentEditor editor = new(controller);
        editor.ApplyConfiguration(new(source.Parameters with { Boundary = BoundaryKind.Periodic }, source.Matrix, source.TimeStep));
        SimulationConfiguration valid = editor.Configuration;
        long revision = editor.Revision;
        int events = editor.SessionEventCount;
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.SetSelectedPairRanges([1, 4], 0.01, 0.2));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.SetPairRange(0, 1, 0.1, 0.6));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.RandomizeInteractionSelection([1],
            InteractionRandomization.Radii, 1, 0.1, 0.6));
        Assert.Same(valid, editor.Configuration);
        Assert.Equal(revision, editor.Revision);
        Assert.Equal(events, editor.SessionEventCount);
        Assert.Equal(source.InitialState.Positions.ToArray(), controller.Observe().Positions.ToArray());
    }

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
        ExperimentDefinition captured = editor.CaptureCurrentExperiment("ranges");
        Assert.True(ranges.EquivalentTo(ExperimentEditing.RandomizeMatrix(captured, 19).Matrix.Ranges));
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
