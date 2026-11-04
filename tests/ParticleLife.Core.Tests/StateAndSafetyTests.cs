using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using Xunit;

namespace ParticleLife.Core.Tests;

public sealed class StateAndSafetyTests
{
    [Fact]
    public void SnapshotOwnsCopiesAndCapturedStateSurvivesLaterSteps()
    {
        long[] ids = [1];
        Vector2d[] positions = [new(0.5, 0.5)];
        Vector2d[] velocities = [new(0.1, -0.1)];
        int[] species = [0];
        ParticleSnapshot initial = new(ids, positions, velocities, species);
        Simulation simulation = new(initial,
            new ModelParameters { ForceScale = 0, WallStrength = 0, DampingHalfLife = null },
            new InteractionMatrix(1, [0]), 0.01);
        ids[0] = 50;
        positions[0] = new Vector2d(10, 10);
        velocities[0] = new Vector2d(20, 20);
        species[0] = 10;
        ParticleSnapshot captured = simulation.CaptureSnapshot();
        simulation.Step();
        simulation.Step();

        Assert.Equal(1, initial.Ids[0]);
        Assert.Equal(new Vector2d(0.5, 0.5), initial.Positions[0]);
        Assert.Equal(new Vector2d(0.1, -0.1), initial.Velocities[0]);
        Assert.Equal(0, initial.Species[0]);
        NumericalAssert.SameState(initial, captured);
        Assert.NotEqual(captured.Positions[0], simulation.Observe().Positions[0]);
    }

    [Fact]
    public void SnapshotRejectsMismatchedArraysDuplicateIdsAndNegativeSpecies()
    {
        Assert.ThrowsAny<ArgumentException>(() => new ParticleSnapshot([1, 2], [default], [default], [0]));
        Assert.ThrowsAny<ArgumentException>(() => new ParticleSnapshot([1], [default], [], [0]));
        Assert.ThrowsAny<ArgumentException>(() => new ParticleSnapshot([1], [default], [default], []));
        Assert.ThrowsAny<ArgumentException>(() => new ParticleSnapshot([1, 1], [default, default], [default, default], [0, 0]));
        Assert.ThrowsAny<ArgumentException>(() => new ParticleSnapshot([1], [default], [default], [-1]));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void SnapshotRejectsNonFiniteCoordinatesAndVelocities(double invalid)
    {
        Assert.ThrowsAny<ArgumentException>(() => new ParticleSnapshot([1], [new(invalid, 0)], [default], [0]));
        Assert.ThrowsAny<ArgumentException>(() => new ParticleSnapshot([1], [new(0, invalid)], [default], [0]));
        Assert.ThrowsAny<ArgumentException>(() => new ParticleSnapshot([1], [default], [new(invalid, 0)], [0]));
        Assert.ThrowsAny<ArgumentException>(() => new ParticleSnapshot([1], [default], [new(0, invalid)], [0]));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void SnapshotRejectsInvalidTime(double invalidTime)
    {
        Assert.ThrowsAny<ArgumentException>(() => new ParticleSnapshot([1], [default], [default], [0], invalidTime));
    }

    [Fact]
    public void SnapshotRejectsNegativeStepNumber()
    {
        Assert.ThrowsAny<ArgumentException>(() => new ParticleSnapshot([1], [default], [default], [0], step: -1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void SimulationRejectsInvalidTimestep(double timestep)
    {
        Assert.ThrowsAny<ArgumentException>(() => new Simulation(Single(), new ModelParameters(), new InteractionMatrix(1, [0]), timestep));
    }

    [Fact]
    public void SimulationRejectsSpeciesNotPresentInMatrix()
    {
        ParticleSnapshot initial = new([1], [default], [default], [1]);
        Assert.ThrowsAny<ArgumentException>(() => new Simulation(initial, new ModelParameters(), new InteractionMatrix(1, [0]), 0.01));
    }

    [Fact]
    public void FailedIntegrationDoesNotPublishPartialStateOrAdvanceTime()
    {
        ParticleSnapshot initial = new([11, 12], [new(0.5, 0.5), new(0.7, 0.5)],
            [new(0.1, 0), new(double.MaxValue, 0)], [0, 0]);
        Simulation simulation = new(initial,
            new ModelParameters { ForceScale = 0, WallStrength = 0, DampingHalfLife = null },
            new InteractionMatrix(1, [0]), 2);

        ArithmeticException error = Assert.Throws<ArithmeticException>(simulation.Step);

        Assert.Contains("12", error.Message);
        NumericalAssert.SameState(initial, simulation.CaptureSnapshot());
        Assert.Throws<ArithmeticException>(simulation.Step);
        NumericalAssert.SameState(initial, simulation.CaptureSnapshot());
    }

    [Fact]
    public void FailedForceEvaluationDoesNotPublishPartialState()
    {
        ParticleSnapshot initial = new([11, 12], [new(0.5, 0.5), new(-1, 0.5)], [default, default], [0, 0]);
        Simulation simulation = new(initial,
            new ModelParameters { ForceScale = 0, WallStrength = double.MaxValue },
            new InteractionMatrix(1, [0]), 0.01);

        Assert.Throws<ArithmeticException>(simulation.Step);

        NumericalAssert.SameState(initial, simulation.CaptureSnapshot());
    }

    [Fact]
    public void InvalidResetLeavesCompletedStateUnchanged()
    {
        Simulation simulation = new(Single(), new ModelParameters(), new InteractionMatrix(1, [0]), 0.01);
        simulation.Step();
        ParticleSnapshot completed = simulation.CaptureSnapshot();
        ParticleSnapshot invalidForMatrix = new([1], [new(0.5, 0.5)], [default], [1]);

        Assert.ThrowsAny<ArgumentException>(() => simulation.Reset(invalidForMatrix));

        NumericalAssert.SameState(completed, simulation.CaptureSnapshot());
    }

    [Fact]
    public void NonzeroSeparationIsNotMistakenForCoincidenceWhenItsSquareUnderflows()
    {
        ParticleSnapshot initial = new([1, 2], [default, new(1e-200, 0)], [default, default], [0, 0]);
        Simulation simulation = new(initial,
            new ModelParameters { Cutoff = 1, WallStrength = 0, DampingHalfLife = null },
            new InteractionMatrix(1, [0]), 0.001);
        simulation.Step();

        NumericalAssert.Close(-0.001, simulation.Observe().Velocities[0].X);
        NumericalAssert.Close(0.001, simulation.Observe().Velocities[1].X);
        Assert.Equal(0, simulation.LastStatistics.OverlapPairs);
        Assert.Equal(2, simulation.LastStatistics.AcceptedPairs);
    }

    [Fact]
    public void SubnormalSquaredCutoffDoesNotRejectAnActuallyCloserPair()
    {
        const double cutoff = 3e-162;
        const double separation = 2.9e-162;
        Assert.Equal(cutoff * cutoff, separation * separation);
        ParticleSnapshot initial = new([1, 2], [default, new(separation, 0)], [default, default], [0, 0]);
        Simulation simulation = new(initial,
            new ModelParameters { Cutoff = cutoff, WallStrength = 0, DampingHalfLife = null },
            new InteractionMatrix(1, [1]), 0.001);
        simulation.Step();

        double expectedForce = 2 * (1 - separation / cutoff) / 0.7;
        NumericalAssert.Close(0.001 * expectedForce, simulation.Observe().Velocities[0].X);
        Assert.Equal(2, simulation.LastStatistics.AcceptedPairs);
        Assert.Equal(0, simulation.LastStatistics.OverlapPairs);
    }

    [Fact]
    public void NoncoincidentPairKeepsRepulsiveLimitWhenNormalizedDistanceUnderflows()
    {
        ParticleSnapshot initial = new([1, 2], [default, new(double.Epsilon, 0)], [default, default], [0, 0]);
        Simulation simulation = new(initial,
            new ModelParameters { Cutoff = 1e100, WallStrength = 0, DampingHalfLife = null },
            new InteractionMatrix(1, [1]), 0.001);
        simulation.Step();

        NumericalAssert.Close(-0.001, simulation.Observe().Velocities[0].X);
        NumericalAssert.Close(0.001, simulation.Observe().Velocities[1].X);
        Assert.Equal(2, simulation.LastStatistics.AcceptedPairs);
        Assert.Equal(0, simulation.LastStatistics.OverlapPairs);
    }

    [Fact]
    public void DiagonalSubnormalSeparationKeepsUnitDirection()
    {
        ParticleSnapshot initial = new([1, 2], [default, new(double.Epsilon, double.Epsilon)], [default, default], [0, 0]);
        Simulation simulation = new(initial,
            new ModelParameters { Cutoff = 1, WallStrength = 0, DampingHalfLife = null },
            new InteractionMatrix(1, [1]), 0.001);
        simulation.Step();

        double component = 0.001 / Math.Sqrt(2);
        NumericalAssert.Close(new Vector2d(-component, -component), simulation.Observe().Velocities[0]);
        NumericalAssert.Close(new Vector2d(component, component), simulation.Observe().Velocities[1]);
        Assert.Equal(0, simulation.LastStatistics.OverlapPairs);
    }

    [Fact]
    public void EmptyStateCanAdvanceWithoutInventingParticlesOrPairs()
    {
        Simulation simulation = new(new ParticleSnapshot([], [], [], []),
            new ModelParameters(), new InteractionMatrix(1, [0]), 0.01);
        simulation.Step();

        Assert.Equal(0, simulation.Observe().Count);
        Assert.Equal(1, simulation.Observe().Step);
        NumericalAssert.Close(0.01, simulation.Observe().Time);
        Assert.Equal(0, simulation.LastStatistics.CandidatePairs);
        Assert.Equal(0, simulation.LastStatistics.AcceptedPairs);
        Assert.Equal(0, simulation.LastStatistics.OverlapPairs);
    }

    [Fact]
    public void TimeAndStepExhaustionFailWithoutChangingState()
    {
        ParticleSnapshot[] exhausted =
        [
            new([1], [new(0.5, 0.5)], [default], [0], time: double.MaxValue),
            new([1], [new(0.5, 0.5)], [default], [0], step: long.MaxValue)
        ];
        foreach (ParticleSnapshot initial in exhausted)
        {
            Simulation simulation = new(initial, new ModelParameters(), new InteractionMatrix(1, [0]), 0.01);
            Assert.Throws<ArithmeticException>(simulation.Step);
            NumericalAssert.SameState(initial, simulation.CaptureSnapshot());
        }
    }

    [Fact]
    public void ResetCanChangePopulationAndClearsStepStatistics()
    {
        Simulation simulation = new(Single(), new ModelParameters(), new InteractionMatrix(1, [0]), 0.01);
        ParticleSnapshot larger = new([1, 2, 3], [new(0.3, 0.5), new(0.5, 0.5), new(0.7, 0.5)],
            [default, default, default], [0, 0, 0]);
        simulation.Reset(larger);
        simulation.Step();
        Assert.Equal(6, simulation.LastStatistics.CandidatePairs);
        simulation.Reset(Single());

        Assert.Equal(1, simulation.Observe().Count);
        Assert.Equal(0, simulation.Observe().Step);
        Assert.Equal(0, simulation.LastStatistics.CandidatePairs);
        simulation.Step();
        Assert.Equal(0, simulation.LastStatistics.CandidatePairs);
    }

    private static ParticleSnapshot Single()
    {
        return new ParticleSnapshot([1], [new(0.5, 0.5)], [default], [0]);
    }
}
