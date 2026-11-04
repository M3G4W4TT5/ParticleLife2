using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using Xunit;

namespace ParticleLife.Core.Tests;

public sealed class BackendSimulationTests
{
    [Theory]
    [InlineData(17)]
    [InlineData(1729)]
    [InlineData(8675309)]
    public void ShortTrajectoriesAndReorderedStorageAgreeByIdentity(int seed)
    {
        ExperimentDefinition experiment = PresetGenerator.Create(PresetKind.Random, 96, 4, seed, timeStep: 0.001);
        ParticleSnapshot initial = experiment.InitialState;
        int[] order = Enumerable.Range(0, initial.Count).Reverse().ToArray();
        ParticleSnapshot reversed = new(order.Select(i => initial.Ids[i]).ToArray(),
            order.Select(i => initial.Positions[i]).ToArray(), order.Select(i => initial.Velocities[i]).ToArray(),
            order.Select(i => initial.Species[i]).ToArray());
        Simulation direct = new(initial, experiment.Parameters, experiment.Matrix, experiment.TimeStep);
        Simulation grid = new(initial, experiment.Parameters, experiment.Matrix, experiment.TimeStep, ForceBackend.UniformGrid);
        Simulation reordered = new(reversed, experiment.Parameters, experiment.Matrix, experiment.TimeStep, ForceBackend.UniformGrid);

        for (int step = 0; step < 120; step++)
        {
            direct.Step();
            grid.Step();
            reordered.Step();
            Assert.Equal(direct.LastStatistics.AcceptedPairs, grid.LastStatistics.AcceptedPairs);
            Assert.Equal(direct.LastStatistics.OverlapPairs, grid.LastStatistics.OverlapPairs);
            Assert.Equal(grid.LastStatistics.AcceptedPairs, reordered.LastStatistics.AcceptedPairs);
            // These 0.12-time-unit trajectories use order-one reduced quantities. The
            // shared assertion's 1e-12 absolute + 1e-12 relative admits accumulation
            // order roundoff; exact pair counts independently detect missing neighbours.
            NumericalAssert.SameState(direct.CaptureSnapshot(), grid.CaptureSnapshot());
            NumericalAssert.SameState(direct.CaptureSnapshot(), reordered.CaptureSnapshot());
        }

        Assert.Equal(ForceBackend.UniformGrid, grid.LastExecution!.Value.ActualBackend);
        Assert.Equal(0, grid.DirectFallbackSteps);
    }

    [Theory]
    [InlineData(ForceBackend.Direct)]
    [InlineData(ForceBackend.UniformGrid)]
    public void IntegrationFailurePreservesPreviousCompletedStateStatisticsAndExecution(ForceBackend backend)
    {
        ParticleSnapshot initial = new([11, 12], [new(0, 0), new(0.05, 0)],
            [new(1e308, 0), new(1e308, 0)], [0, 0]);
        Simulation simulation = new(initial, Ballistic(), new InteractionMatrix(1, [0]), 1, backend);
        simulation.Step();
        ParticleSnapshot completed = simulation.CaptureSnapshot();
        ForceEvaluationStatistics statistics = simulation.LastStatistics;
        ForceExecution? execution = simulation.LastExecution;
        Assert.Equal(2, statistics.AcceptedPairs);

        // The next input exceeds grid coordinates and the rejected evaluation would
        // fall back. Neither that metadata nor its new overlap count may be published.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Assert.Throws<ArithmeticException>(simulation.Step);
            Assert.Equal(completed.Positions.ToArray(), simulation.Observe().Positions.ToArray());
            Assert.Equal(completed.Velocities.ToArray(), simulation.Observe().Velocities.ToArray());
            Assert.Equal(completed.Time, simulation.Observe().Time);
            Assert.Equal(completed.Step, simulation.Observe().Step);
            Assert.Equal(statistics, simulation.LastStatistics);
            Assert.Equal(execution, simulation.LastExecution);
            Assert.Equal(0, simulation.DirectFallbackSteps);
        }
    }

    [Theory]
    [InlineData(ForceBackend.Direct)]
    [InlineData(ForceBackend.UniformGrid)]
    public void ForceFailurePreservesPreviousCompletedStateAndStatistics(ForceBackend backend)
    {
        ParticleSnapshot initial = new([11, 12], [new(0.5, 0.5), new(0.55, 0.5)],
            [new(-1, 0), new(-1, 0)], [0, 0]);
        Simulation simulation = new(initial, Ballistic() with { WallStrength = double.MaxValue },
            new InteractionMatrix(1, [0]), 1, backend);
        simulation.Step();
        ParticleSnapshot completed = simulation.CaptureSnapshot();
        ForceEvaluationStatistics statistics = simulation.LastStatistics;
        ForceExecution? execution = simulation.LastExecution;
        Assert.Equal(2, statistics.AcceptedPairs);

        Assert.Throws<ArithmeticException>(simulation.Step);
        NumericalAssert.SameState(completed, simulation.CaptureSnapshot());
        Assert.Equal(statistics, simulation.LastStatistics);
        Assert.Equal(execution, simulation.LastExecution);
    }

    [Fact]
    public void FallbackCountsOnlyCompletedStepsAndResetClearsProvenance()
    {
        ParticleSnapshot initial = new([1], [new(1e308, 0)], [new(5e307, 0)], [0]);
        Simulation simulation = new(initial, Ballistic(), new InteractionMatrix(1, [0]), 1, ForceBackend.UniformGrid);
        Assert.Null(simulation.LastExecution);
        simulation.Step();
        ForceExecution? completedExecution = simulation.LastExecution;
        Assert.Equal(ForceBackend.UniformGrid, simulation.Backend);
        Assert.Equal(ForceBackend.Direct, completedExecution!.Value.ActualBackend);
        Assert.Equal(GridFallbackReason.CoordinateRange, completedExecution.Value.FallbackReason);
        Assert.Equal(1, simulation.DirectFallbackSteps);

        Assert.Throws<ArithmeticException>(simulation.Step);
        Assert.Equal(completedExecution, simulation.LastExecution);
        Assert.Equal(1, simulation.DirectFallbackSteps);

        simulation.Reset(new ParticleSnapshot([1], [default], [default], [0]));
        Assert.Null(simulation.LastExecution);
        Assert.Equal(default, simulation.LastStatistics);
        Assert.Equal(0, simulation.DirectFallbackSteps);
        simulation.Step();
        Assert.Equal(ForceBackend.UniformGrid, simulation.LastExecution!.Value.ActualBackend);
        Assert.Equal(GridFallbackReason.None, simulation.LastExecution.Value.FallbackReason);
    }

    [Fact]
    public void AccumulationFallbackPublishesOnlyWithACompletedStepAndResetsItsCount()
    {
        long[] ids = [1, 2, 3, 4, 5];
        Vector2d[] positions = [default, new(0.65, 0), new(-0.65, 0), new(0.65, 0), new(-0.65, 0)];
        int[] species = [0, 1, 1, 1, 1];
        ModelParameters parameters = new() { Cutoff = 1, ForceScale = 1e308, WallStrength = 0, DampingHalfLife = null };
        InteractionMatrix matrix = new(2, [0, 1, 0, 0]);
        ParticleSnapshot failing = new(ids, positions, [default, new(1e308, 0), default, default, default], species);
        Simulation grid = new(failing, parameters, matrix, 2, ForceBackend.UniformGrid);
        // Direct storage order cancels finite terms; cell order overflows and retries
        // direct. The later integration failure must still publish no fallback metadata.
        Assert.Throws<ArithmeticException>(grid.Step);
        Assert.Null(grid.LastExecution);
        Assert.Equal(0, grid.DirectFallbackSteps);
        Assert.Equal(0, grid.AccumulationFallbackSteps);
        Assert.Equal(failing.Positions.ToArray(), grid.Observe().Positions.ToArray());

        ParticleSnapshot stationary = new(ids, positions, new Vector2d[5], species);
        grid.Reset(stationary);
        Simulation direct = new(stationary, parameters, matrix, 2);
        direct.Step();
        grid.Step();
        NumericalAssert.SameState(direct.CaptureSnapshot(), grid.CaptureSnapshot());
        Assert.Equal(GridFallbackReason.AccumulationRange, grid.LastExecution!.Value.FallbackReason);
        Assert.Equal(1, grid.DirectFallbackSteps);
        Assert.Equal(1, grid.AccumulationFallbackSteps);
        grid.Reset(stationary);
        Assert.Equal(0, grid.AccumulationFallbackSteps);
        Assert.Equal(0, grid.DirectFallbackSteps);
        Assert.Null(grid.LastExecution);
    }

    [Fact]
    public void ResetGrowthShrinkAndEmptyReplacementNeverRetainOldNeighbours()
    {
        ExperimentDefinition experiment = PresetGenerator.Create(PresetKind.Random, 257, 3, 618);
        Simulation direct = new(new ParticleSnapshot([], [], [], []), experiment.Parameters, experiment.Matrix, 0.001);
        Simulation grid = new(new ParticleSnapshot([], [], [], []), experiment.Parameters, experiment.Matrix, 0.001, ForceBackend.UniformGrid);
        ParticleSnapshot[] replacements =
        [
            experiment.InitialState,
            new([5, 7], [new(-0.001, 1.2), new(0.001, 1.2)], [default, default], [2, 1]),
            new([], [], [], []),
            new([90], [new(-2, 4)], [new(1, -1)], [0]),
            experiment.InitialState
        ];
        foreach (ParticleSnapshot replacement in replacements)
        {
            direct.Reset(replacement);
            grid.Reset(replacement);
            Assert.Null(grid.LastExecution);
            Assert.Equal(default, grid.LastStatistics);
            for (int i = 0; i < 5; i++)
            {
                direct.Step();
                grid.Step();
                NumericalAssert.SameState(direct.CaptureSnapshot(), grid.CaptureSnapshot());
                Assert.Equal(direct.LastStatistics.AcceptedPairs, grid.LastStatistics.AcceptedPairs);
                Assert.Equal(direct.LastStatistics.OverlapPairs, grid.LastStatistics.OverlapPairs);
            }
        }
    }

    [Theory]
    [InlineData(ForceBackend.Direct)]
    [InlineData(ForceBackend.UniformGrid)]
    public void WarmedStepsAllocateNothingWhileParticlesSpreadAcrossChangingCells(ForceBackend backend)
    {
        const int count = 128;
        long[] ids = Enumerable.Range(0, count).Select(i => (long)i).ToArray();
        Vector2d[] positions = new Vector2d[count];
        Vector2d[] velocities = Enumerable.Range(0, count)
            .Select(i => new Vector2d((i % 16 - 7.5) * 0.02, (i / 16 - 3.5) * 0.03)).ToArray();
        ParticleSnapshot initial = new(ids, positions, velocities, new int[count]);
        Simulation simulation = new(initial, Ballistic(), new InteractionMatrix(1, [0]), 0.02, backend);
        for (int i = 0; i < 256; i++)
        {
            simulation.Step();
        }

        simulation.Reset(initial);
        int minimumCells = int.MaxValue;
        int maximumCells = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 256; i++)
        {
            simulation.Step();
            int occupied = simulation.LastExecution!.Value.Index.OccupiedCells;
            minimumCells = Math.Min(minimumCells, occupied);
            maximumCells = Math.Max(maximumCells, occupied);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.NotEqual(initial.Positions[0], simulation.Observe().Positions[0]);
        if (backend == ForceBackend.UniformGrid)
        {
            Assert.Equal(1, minimumCells);
            Assert.True(maximumCells > 32, $"Expected substantial cell migration; saw {maximumCells} occupied cells.");
        }
    }

    [Fact]
    public void UnknownBackendIsRejectedAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Simulation(new ParticleSnapshot([], [], [], []),
            new ModelParameters(), new InteractionMatrix(1, [0]), 0.01, (ForceBackend)99));
    }

    private static ModelParameters Ballistic() => new() { ForceScale = 0, WallStrength = 0, DampingHalfLife = null };
}
