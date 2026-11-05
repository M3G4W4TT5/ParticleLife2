using System.Text.Json;
using System.Text.Json.Nodes;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using Xunit;

namespace ParticleLife.Core.Tests;

public sealed class BoundaryAndRangeTests
{
    [Theory]
    [InlineData(0, 0.03, 0)]
    [InlineData(0.015, 0.03, -0.5)]
    [InlineData(0.03, 0.03, 0)]
    [InlineData(0.065, 0.03, 0.8)]
    [InlineData(0.1, 0.03, 0)]
    [InlineData(0.05, 0, 0.8)]
    public void DirectedDistanceCurveHasSpecifiedCorePeakAndZeroCoreValues(double distance, double core, double expected)
    {
        NumericalAssert.Close(expected, ParticleLifeForce.RadialCoefficientAtDistance(distance, 0.8, core, 0.1));
    }

    [Theory]
    [InlineData(1.0, 1.0, 0.1)]
    [InlineData(1.17, 0.83, 0.1)]
    [InlineData(0.21, 0.26, 0.1)]
    public void PeriodicGridMatchesDirectAtSeamsCornersAndSmallWrappedGrids(double width, double height, double cutoff)
    {
        ModelParameters parameters = new() { Boundary = BoundaryKind.Periodic, WorldWidth = width, WorldHeight = height, Cutoff = cutoff };
        Random random = new(492);
        const int count = 71;
        Vector2d[] positions = new Vector2d[count];
        int[] species = new int[count];
        for (int i = 0; i < count; i++)
        {
            positions[i] = new(random.NextDouble() * width, random.NextDouble() * height);
            species[i] = i % 2;
        }
        positions[0] = new(0.002, 0.003);
        positions[1] = new(width - 0.004, height - 0.005);
        positions[2] = new(-0.004, -0.005);
        positions[3] = new(3 * width + 0.002, -2 * height + 0.003);
        ParticleSnapshot state = new(Enumerable.Range(0, count).Select(i => (long)i).ToArray(), positions, new Vector2d[count], species);
        InteractionMatrix matrix = new(2, [0.2, 0.7, -0.8, -0.1],
            new(2, [0.02, 0, 0.01, 0.03], [cutoff, cutoff * 0.8, cutoff * 0.6, cutoff]));
        Vector2d[] expected = new Vector2d[count];
        Vector2d[] actual = new Vector2d[count];
        ForceEvaluationStatistics reference = new DirectForceEvaluator(parameters, matrix).Evaluate(state.Observe(), expected);
        using UniformGridForceEvaluator grid = new(parameters, matrix, 4);
        ForceEvaluationStatistics indexed = grid.Evaluate(state.Observe(), actual);
        Assert.Equal(reference.AcceptedPairs, indexed.AcceptedPairs);
        Assert.Equal(reference.OverlapPairs, indexed.OverlapPairs);
        Assert.InRange(indexed.CandidatePairs, 0, (long)count * (count - 1));
        for (int i = 0; i < count; i++)
        {
            NumericalAssert.Close(expected[i], actual[i]);
        }
    }

    [Theory]
    [InlineData(BoundaryKind.Periodic, 0.4, 0.8, 3.2, -2.4)]
    [InlineData(BoundaryKind.Reflecting, 0.6, 0.2, -3.2, 2.4)]
    [InlineData(BoundaryKind.Open, 3.4, -2.2, 3.2, -2.4)]
    public void BoundaryIntegrationHandlesMultipleCrossingsWithoutLosingIdentity(BoundaryKind boundary,
        double x, double y, double vx, double vy)
    {
        ModelParameters parameters = new() { Boundary = boundary, ForceScale = 0, DampingHalfLife = null };
        ParticleSnapshot initial = new([42], [new(0.2, 0.2)], [new(3.2, -2.4)], [0]);
        using Simulation simulation = new(initial, parameters, new(1, [0]), 1, ForceBackend.UniformGrid);
        simulation.Step();
        ParticleSnapshot result = simulation.CaptureSnapshot();
        Assert.Equal(42, result.Ids[0]);
        NumericalAssert.Close(new Vector2d(x, y), result.Positions[0]);
        NumericalAssert.Close(new Vector2d(vx, vy), result.Velocities[0]);
        Assert.Equal(1, result.Step);
        Assert.Equal(1, result.Time);
    }

    [Fact]
    public void ReflectingExactEdgesPointVelocityInwardAndIgnoreDisplaySizes()
    {
        ModelParameters parameters = new() { Boundary = BoundaryKind.Reflecting };
        Vector2d position = new(0, 1);
        Vector2d velocity = new(-2, 3);
        WorldBoundary.Map(ref position, ref velocity, parameters);
        Assert.Equal(new Vector2d(0, 1), position);
        Assert.Equal(new Vector2d(2, -3), velocity);
    }

    [Fact]
    public void DirectedRangesAndZeroCoreGiveDifferentReverseForces()
    {
        ModelParameters parameters = new() { Boundary = BoundaryKind.Open };
        InteractionMatrix matrix = new(2, [0, 1, 1, 0], new(2, [0, 0, 0.02, 0], [0.1, 0.4, 0.05, 0.1]));
        ParticleSnapshot initial = new([10, 20], [new(0.4, 0.5), new(0.5, 0.5)], new Vector2d[2], [0, 1]);
        Vector2d[] forces = new Vector2d[2];
        ForceEvaluationStatistics counts = new DirectForceEvaluator(parameters, matrix).Evaluate(initial.Observe(), forces);
        NumericalAssert.Close(new Vector2d(0.5, 0), forces[0]);
        Assert.Equal(default, forces[1]);
        Assert.Equal(1, counts.AcceptedPairs);
    }

    [Fact]
    public void ZeroCoefficientDoesNotDisableItsNonzeroCore()
    {
        ModelParameters parameters = new() { Boundary = BoundaryKind.Open };
        InteractionMatrix matrix = new(1, [0], new(1, [0.04], [0.1]));
        ParticleSnapshot initial = new([1, 2], [new(0.4, 0.5), new(0.42, 0.5)], new Vector2d[2], [0, 0]);
        Vector2d[] forces = new Vector2d[2];
        using UniformGridForceEvaluator grid = new(parameters, matrix);
        grid.Evaluate(initial.Observe(), forces);
        NumericalAssert.Close(-0.5, forces[0].X);
        NumericalAssert.Close(0.5, forces[1].X);
    }

    [Fact]
    public void PeriodicRangeAndInvalidTablesRejectBeforeChangingActiveState()
    {
        Assert.ThrowsAny<ArgumentException>(() => new PairInteractionRanges(1, [0.1], [0.1]));
        Assert.ThrowsAny<ArgumentException>(() => new PairInteractionRanges(1, [-0.01], [0.1]));
        Assert.ThrowsAny<ArgumentException>(() => new PairInteractionRanges(1, [0], [double.NaN]));
        ExperimentDefinition initial = PresetGenerator.Create(PresetKind.Random, 12, 2, 4);
        using Simulation simulation = initial.CreateSimulation();
        ParticleSnapshot before = simulation.CaptureSnapshot();
        InteractionMatrix tooWide = initial.Matrix.WithRanges(new(2, [0, 0, 0, 0], [0.1, 0.6, 0.1, 0.1]));
        Assert.ThrowsAny<ArgumentException>(() => new SimulationConfiguration(initial.Parameters with { Boundary = BoundaryKind.Periodic },
            tooWide, initial.TimeStep));
        NumericalAssert.SameState(before, simulation.CaptureSnapshot());
    }

    [Fact]
    public void NewExperimentAndSessionFormatsRoundTripActualBoundaryAndPairTables()
    {
        ExperimentDefinition baseline = PresetGenerator.Create(PresetKind.Random, 12, 2, 4,
            new ModelParameters { Boundary = BoundaryKind.Periodic });
        InteractionMatrix matrix = baseline.Matrix.WithRanges(new(2, [0.02, 0, 0.03, 0.04], [0.1, 0.2, 0.3, 0.4]));
        ExperimentDefinition source = new("paired", baseline.Parameters, matrix, baseline.InitialState,
            baseline.TimeStep, baseline.Generator, ForceBackend.UniformGrid);
        ExperimentDefinition decoded = ExperimentFile.Deserialize(ExperimentFile.Serialize(source));
        Assert.Equal(source.Parameters, decoded.Parameters);
        Assert.True(source.Matrix.EquivalentTo(decoded.Matrix));
        NumericalAssert.SameState(source.InitialState, decoded.InitialState);
        SimulationConfiguration changed = new(source.Parameters with { Boundary = BoundaryKind.Reflecting }, matrix, source.TimeStep, source.Backend);
        SessionDefinition session = new(source, [new SessionEvent(0, changed, label: "Reflect")], 4);
        SessionDefinition loaded = SessionFile.Deserialize(SessionFile.Serialize(session));
        Assert.True(changed.EquivalentTo(loaded.Events[0].Configuration));
        using Simulation expected = SessionReplay.Replay(session);
        using Simulation actual = SessionReplay.Replay(loaded);
        NumericalAssert.SameState(expected.CaptureSnapshot(), actual.CaptureSnapshot());
    }

    [Fact]
    public void LegacySchemaTwoKeepsGlobalSoftWallMeaningAndCurrentFieldsAreRequired()
    {
        ExperimentDefinition source = PresetGenerator.Create(PresetKind.Random, 12, 2, 4);
        JsonObject old = JsonNode.Parse(ExperimentFile.Serialize(source))!.AsObject();
        old["schemaVersion"] = 2;
        old["modelVersion"] = ExperimentFile.LegacyModelVersion;
        old["matrix"]!.AsObject().Remove("pairRanges");
        ExperimentDefinition loaded = ExperimentFile.Deserialize(old.ToJsonString());
        Assert.Equal(BoundaryKind.SoftWall, loaded.Parameters.Boundary);
        Assert.Null(loaded.Matrix.Ranges);
        using Simulation expected = source.CreateSimulation();
        using Simulation actual = loaded.CreateSimulation();
        expected.Step();
        actual.Step();
        NumericalAssert.SameState(expected.CaptureSnapshot(), actual.CaptureSnapshot());
        old["schemaVersion"] = 3;
        old["modelVersion"] = ExperimentFile.ModelVersion;
        Assert.Throws<JsonException>(() => ExperimentFile.Deserialize(old.ToJsonString()));
    }
}
