using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using Xunit;

namespace ParticleLife.Core.Tests;

public sealed class SimulationTests
{
    private static readonly ModelParameters Isolated = new()
    {
        Cutoff = 1,
        DampingHalfLife = null,
        WallStrength = 0
    };

    [Theory]
    [InlineData(0.15, 0.8, -0.5)]
    [InlineData(0.65, 0.8, 0.8)]
    [InlineData(0.65, -0.8, -0.8)]
    [InlineData(1, 0.8, 0)]
    [InlineData(1.1, 0.8, 0)]
    public void PairStepHasExpectedForceSignAndCutoff(double separation, double attraction, double force)
    {
        const double dt = 0.01;
        Simulation simulation = new(Pair(separation), Isolated, new InteractionMatrix(1, [attraction]), dt);
        simulation.Step();
        ParticleSnapshot result = simulation.CaptureSnapshot();

        NumericalAssert.Close(new Vector2d(dt * force, 0), result.Velocities[0]);
        NumericalAssert.Close(new Vector2d(-dt * force, 0), result.Velocities[1]);
        NumericalAssert.Close(new Vector2d(dt * dt * force, 0), result.Positions[0]);
        NumericalAssert.Close(new Vector2d(separation - dt * dt * force, 0), result.Positions[1]);
        Assert.Equal(2, simulation.LastStatistics.CandidatePairs);
        Assert.Equal(separation < 1 ? 2 : 0, simulation.LastStatistics.AcceptedPairs);
        Assert.Equal(0, simulation.LastStatistics.OverlapPairs);
        Assert.Equal(1, result.Step);
        NumericalAssert.Close(dt, result.Time);
    }

    [Fact]
    public void PairDirectionIsNormalizedInTwoDimensions()
    {
        ParticleSnapshot initial = new([10, 11], [new(0, 0), new(0.39, 0.52)], [default, default], [0, 0]);
        Simulation simulation = new(initial, Isolated, new InteractionMatrix(1, [0.8]), 0.01);
        simulation.Step();

        // Separation is 0.65; the unit direction is (0.6, 0.8).
        NumericalAssert.Close(new Vector2d(0.0048, 0.0064), simulation.Observe().Velocities[0]);
        NumericalAssert.Close(new Vector2d(-0.0048, -0.0064), simulation.Observe().Velocities[1]);
    }

    [Fact]
    public void DirectedMatrixDoesNotUseActionReactionShortcut()
    {
        ParticleSnapshot initial = new([10, 11], [new(0, 0), new(0.65, 0)], [default, default], [0, 1]);
        Simulation simulation = new(initial, Isolated, new InteractionMatrix(2, [0, 1, -0.5, 0]), 0.01);
        simulation.Step();

        NumericalAssert.Close(new Vector2d(0.01, 0), simulation.Observe().Velocities[0]);
        NumericalAssert.Close(new Vector2d(0.005, 0), simulation.Observe().Velocities[1]);
        Assert.True(simulation.Observe().Velocities[0].X + simulation.Observe().Velocities[1].X > 0);
    }

    [Fact]
    public void ForcesComeFromOneCompleteInputStateRegardlessOfParticleOrder()
    {
        long[] ids = [101, 42, 901, 3, 77];
        Vector2d[] positions = [new(0.2, 0.3), new(0.5, 0.1), new(0.75, 0.6), new(0.4, 0.7), new(0.6, 0.45)];
        Vector2d[] velocities = [new(0.3, -0.2), new(-0.1, 0.4), new(0.2, 0.1), new(-0.4, -0.2), default];
        int[] species = [0, 1, 2, 0, 1];
        int[] permutation = [2, 4, 0, 3, 1];
        ParticleSnapshot initial = new(ids, positions, velocities, species);
        ParticleSnapshot reordered = new(
            permutation.Select(i => ids[i]).ToArray(),
            permutation.Select(i => positions[i]).ToArray(),
            permutation.Select(i => velocities[i]).ToArray(),
            permutation.Select(i => species[i]).ToArray());
        InteractionMatrix matrix = new(3, [0.2, -0.5, 0.8, 0.4, -0.1, 0.7, -0.9, 0.6, 0.3]);
        Simulation first = new(initial, Isolated, matrix, 0.03);
        Simulation second = new(reordered, Isolated, matrix, 0.03);

        first.Step();
        second.Step();

        NumericalAssert.SameState(first.CaptureSnapshot(), second.CaptureSnapshot());
    }

    [Fact]
    public void PairVelocityIncrementScalesLinearlyWithTimestep()
    {
        InteractionMatrix matrix = new(1, [0.8]);
        Simulation full = new(Pair(0.65), Isolated, matrix, 0.01);
        Simulation half = new(Pair(0.65), Isolated, matrix, 0.005);
        full.Step();
        half.Step();

        NumericalAssert.Close(full.Observe().Velocities[0].X / 2, half.Observe().Velocities[0].X);
        NumericalAssert.Close(full.Observe().Positions[0].X / 4, half.Observe().Positions[0].X);
    }

    [Fact]
    public void WallVelocityIncrementScalesWithTimestepAndMatchesMigrationReference()
    {
        ParticleSnapshot initial = new([1], [new(0.025, 0.5)], [default], [0]);
        ModelParameters parameters = new() { DampingHalfLife = null };
        InteractionMatrix matrix = new(1, [0]);
        Simulation full = new(initial, parameters, matrix, 1d / 144);
        Simulation half = new(initial, parameters, matrix, 1d / 288);
        full.Step();
        half.Step();

        // The legacy wall kick at this position was 0.005 for both timesteps.
        // The corrected force is 0.72 and produces dt-proportional increments.
        NumericalAssert.Close(0.005, full.Observe().Velocities[0].X);
        NumericalAssert.Close(0.0025, half.Observe().Velocities[0].X);
        NumericalAssert.Close(0, half.Observe().Velocities[0].Y);
    }

    [Fact]
    public void DampingMatchesAnalyticVelocityAndDocumentedDiscretePosition()
    {
        const double dt = 0.01;
        const double halfLife = 0.08;
        const int steps = 16;
        Vector2d velocity = new(1.25, -0.75);
        ParticleSnapshot initial = new([1], [new(0.5, 0.5)], [velocity], [0]);
        ModelParameters parameters = Isolated with { ForceScale = 0, DampingHalfLife = halfLife };
        Simulation simulation = new(initial, parameters, new InteractionMatrix(1, [0]), dt);
        Advance(simulation, steps);

        double analyticFactor = Math.Pow(2, -steps * dt / halfLife);
        double damping = Math.Pow(2, -dt / halfLife);
        double positionFactor = dt * damping * (1 - Math.Pow(damping, steps)) / (1 - damping);
        NumericalAssert.Close(new Vector2d(velocity.X * analyticFactor, velocity.Y * analyticFactor), simulation.Observe().Velocities[0]);
        NumericalAssert.Close(new Vector2d(0.5 + velocity.X * positionFactor, 0.5 + velocity.Y * positionFactor), simulation.Observe().Positions[0]);
    }

    [Fact]
    public void DampingVelocityIsIndependentOfTimestepPartitionForFixedDuration()
    {
        ParticleSnapshot initial = new([1], [new(0.5, 0.5)], [new(0.6, -0.2)], [0]);
        ModelParameters parameters = Isolated with { ForceScale = 0, DampingHalfLife = 0.05 };
        InteractionMatrix matrix = new(1, [0]);
        Simulation full = new(initial, parameters, matrix, 0.01);
        Simulation half = new(initial, parameters, matrix, 0.005);
        Advance(full, 20);
        Advance(half, 40);

        NumericalAssert.Close(full.Observe().Velocities[0], half.Observe().Velocities[0]);
        NumericalAssert.Close(0.6 / 16, full.Observe().Velocities[0].X);
    }

    [Fact]
    public void DampedPositionConvergesAtFirstOrderForFixedDuration()
    {
        const double duration = 0.4;
        const double halfLife = 0.5;
        double lambda = Math.Log(2) / halfLife;
        double exact = 0.5 + 0.6 * (1 - Math.Exp(-lambda * duration)) / lambda;
        ParticleSnapshot initial = new([1], [new(0.5, 0.5)], [new(0.6, 0)], [0]);
        ModelParameters parameters = Isolated with { ForceScale = 0, DampingHalfLife = halfLife };
        double[] errors = new double[3];
        int[] stepCounts = [10, 20, 40];
        for (int i = 0; i < stepCounts.Length; i++)
        {
            Simulation simulation = new(initial, parameters, new InteractionMatrix(1, [0]), duration / stepCounts[i]);
            Advance(simulation, stepCounts[i]);
            errors[i] = Math.Abs(simulation.Observe().Positions[0].X - exact);
        }

        // First-order global error halves with dt; this deliberately tests the
        // convergence rate rather than requiring a discrete path to be exact.
        Assert.InRange(errors[0] / errors[1], 1.9, 2.1);
        Assert.InRange(errors[1] / errors[2], 1.9, 2.1);
    }

    [Fact]
    public void PairMotionConvergesToAnalyticSolutionWithinOneLinearCurveSegment()
    {
        const double duration = 0.25;
        const double initialSeparation = 0.5;
        const double beta = 0.3;
        const double attraction = 0.7;
        double omega = Math.Sqrt(4 * attraction / (1 - beta));
        double exactSeparation = beta + (initialSeparation - beta) * Math.Cos(omega * duration);
        double exactPosition = (initialSeparation - exactSeparation) / 2;
        double[] errors = new double[3];
        int[] stepCounts = [10, 20, 40];
        for (int i = 0; i < stepCounts.Length; i++)
        {
            Simulation simulation = new(Pair(initialSeparation), Isolated, new InteractionMatrix(1, [attraction]), duration / stepCounts[i]);
            Advance(simulation, stepCounts[i]);
            errors[i] = Math.Abs(simulation.Observe().Positions[0].X - exactPosition);
            double separation = simulation.Observe().Positions[1].X - simulation.Observe().Positions[0].X;
            Assert.InRange(separation, beta, (1 + beta) / 2);
        }

        // On beta < r < (1+beta)/2 with R=mass=F0=1, the relative
        // coordinate satisfies (r-beta)'' = -4*a/(1-beta)*(r-beta).
        Assert.InRange(errors[0] / errors[1], 1.8, 2.2);
        Assert.InRange(errors[1] / errors[2], 1.8, 2.2);
    }

    [Fact]
    public void ReciprocalClosedSystemPreservesTotalMomentumWithinRoundoff()
    {
        ParticleSnapshot initial = new([1, 2, 3],
            [new(-0.1, 0.2), new(0.3, 0.5), new(0.6, -0.1)],
            [new(0.2, -0.15), new(-0.05, 0.3), new(0.1, -0.1)], [0, 1, 0]);
        InteractionMatrix reciprocal = new(2, [0.4, -0.2, -0.2, 0.7]);
        Simulation simulation = new(initial, Isolated, reciprocal, 0.001);
        Advance(simulation, 300);
        ParticleStateView result = simulation.Observe();

        NumericalAssert.Close(0.25, result.Velocities[0].X + result.Velocities[1].X + result.Velocities[2].X);
        NumericalAssert.Close(0.05, result.Velocities[0].Y + result.Velocities[1].Y + result.Velocities[2].Y);
    }

    [Fact]
    public void CoincidentParticlesAreRetainedAndCountedWithoutAnInventedDirection()
    {
        ParticleSnapshot initial = new([1, 2, 3], [new(0.5, 0.5), new(0.5, 0.5), new(0.5, 0.5)],
            [default, default, default], [0, 0, 0]);
        Simulation simulation = new(initial, Isolated, new InteractionMatrix(1, [1]), 0.01);
        simulation.Step();

        Assert.Equal(3, simulation.Observe().Count);
        Assert.Equal(6, simulation.LastStatistics.CandidatePairs);
        Assert.Equal(0, simulation.LastStatistics.AcceptedPairs);
        Assert.Equal(3, simulation.LastStatistics.OverlapPairs);
        for (int i = 0; i < initial.Count; i++)
        {
            Assert.Equal(initial.Positions[i], simulation.Observe().Positions[i]);
            Assert.Equal(default, simulation.Observe().Velocities[i]);
        }
    }

    [Fact]
    public void FiniteParticlesOutsideWorldAreKeptAndPushedInward()
    {
        ParticleSnapshot initial = new([1, 2], [new(-0.1, 0.5), new(1.1, 0.5)], [default, default], [0, 0]);
        Simulation simulation = new(initial, new ModelParameters(), new InteractionMatrix(1, [0]), 0.001);
        simulation.Step();

        Assert.Equal(2, simulation.Observe().Count);
        Assert.True(simulation.Observe().Velocities[0].X > 0);
        Assert.True(simulation.Observe().Velocities[1].X < 0);
        Assert.True(simulation.Observe().Positions[0].X < 0);
        Assert.True(simulation.Observe().Positions[1].X > 1);
        Assert.True(simulation.Observe().Positions[0].IsFinite);
        Assert.True(simulation.Observe().Positions[1].IsFinite);
    }

    [Fact]
    public void ResetReplaysStoredInitialStateOnSameRuntime()
    {
        ParticleSnapshot initial = Pair(0.65);
        Simulation simulation = new(initial, Isolated with { DampingHalfLife = 0.05 }, new InteractionMatrix(1, [0.8]), 0.01);
        Advance(simulation, 25);
        ParticleSnapshot first = simulation.CaptureSnapshot();
        simulation.Reset(initial);
        Advance(simulation, 25);
        ParticleSnapshot repeated = simulation.CaptureSnapshot();

        Assert.Equal(first.Positions.ToArray(), repeated.Positions.ToArray());
        Assert.Equal(first.Velocities.ToArray(), repeated.Velocities.ToArray());
        Assert.Equal(first.Time, repeated.Time);
        Assert.Equal(first.Step, repeated.Step);
    }

    [Fact]
    public void WarmedStableCapacityStepsAllocateNoManagedBytes()
    {
        const int count = 32;
        long[] ids = new long[count];
        Vector2d[] positions = new Vector2d[count];
        Vector2d[] velocities = new Vector2d[count];
        int[] species = new int[count];
        for (int i = 0; i < count; i++)
        {
            ids[i] = i + 1;
            positions[i] = new Vector2d(0.2 + 0.07 * (i % 8), 0.2 + 0.07 * (i / 8));
        }

        Simulation simulation = new(new ParticleSnapshot(ids, positions, velocities, species),
            new ModelParameters(), new InteractionMatrix(1, [0.5]), 0.001);
        Advance(simulation, 200);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Advance(simulation, 200);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    private static ParticleSnapshot Pair(double separation)
    {
        return new ParticleSnapshot([10, 11], [new(0, 0), new(separation, 0)], [default, default], [0, 0]);
    }

    private static void Advance(Simulation simulation, int steps)
    {
        for (int i = 0; i < steps; i++)
        {
            simulation.Step();
        }
    }
}
