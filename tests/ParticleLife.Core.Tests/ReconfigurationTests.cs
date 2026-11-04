using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Core.Tests;

public sealed class ReconfigurationTests
{
    [Theory]
    [InlineData(ForceBackend.Direct, 1)]
    [InlineData(ForceBackend.UniformGrid, 1)]
    [InlineData(ForceBackend.UniformGrid, 4)]
    public void EveryLiveRuleChangePreservesTheBoundaryAndMatchesIndependentDirectEvaluation(ForceBackend backend, int workers)
    {
        ExperimentDefinition experiment = PresetGenerator.Create(PresetKind.Random, 64, 3, 930,
            timeStep: 0.001, backend: backend, workerCount: workers);
        using Simulation simulation = experiment.CreateSimulation();
        simulation.Step();
        SimulationConfiguration[] changes =
        [
            new(experiment.Parameters with { Cutoff = 0.125 }, experiment.Matrix, 0.001, backend, workers),
            new(experiment.Parameters with { Cutoff = double.BitIncrement(0.125) }, experiment.Matrix, 0.001, backend, workers),
            new(experiment.Parameters with { DampingHalfLife = null }, experiment.Matrix, 0.001, backend, workers),
            new(experiment.Parameters with { DampingHalfLife = 0.2, ForceScale = 2, RepulsionFraction = 0.45 }, experiment.Matrix, 0.001, backend, workers),
            new(experiment.Parameters with { WorldWidth = 0.4, WorldHeight = 0.3, WallWidth = 0.02, WallStrength = 0.5 }, experiment.Matrix, 0.001, backend, workers),
            new(experiment.Parameters with { WallStrength = 0 }, experiment.Matrix, 0.003, backend, workers),
            new(experiment.Parameters, new InteractionMatrix(3, [0.1, 0.7, -1, -0.5, 0.4, 0.8, 0, -0.3, 1]), 0.0005, backend, workers),
            new(experiment.Parameters, experiment.Matrix, 0.002, ForceBackend.UniformGrid, 2),
            new(experiment.Parameters, experiment.Matrix, 0.001, ForceBackend.Direct)
        ];
        foreach (SimulationConfiguration change in changes)
        {
            ParticleSnapshot before = simulation.CaptureSnapshot();
            simulation.Reconfigure(change);
            AssertExactState(before, simulation.CaptureSnapshot());
            Assert.Null(simulation.LastExecution);
            using Simulation reference = new(before, change.Parameters, change.Matrix, change.TimeStep);
            reference.Step();
            simulation.Step();
            // A single local step over order-one reduced quantities permits accumulation-order
            // roundoff through 1e-12 absolute plus relative; identities/species/counts stay exact.
            NumericalAssert.SameState(reference.CaptureSnapshot(), simulation.CaptureSnapshot());
            Assert.Equal(reference.LastStatistics.AcceptedPairs, simulation.LastStatistics.AcceptedPairs);
        }
    }

    [Fact]
    public void ScalarEditsReuseWorkersAndIndexCapacityAcrossCutoffCellTransitions()
    {
        using Simulation simulation = PresetGenerator.Create(PresetKind.Random, 128, 3, 31,
            backend: ForceBackend.UniformGrid, workerCount: 4).CreateSimulation();
        simulation.Step();
        UniformGridForceEvaluator evaluator = Assert.IsType<UniformGridForceEvaluator>(simulation.Evaluator);
        GridIndexStatistics capacity = evaluator.IndexStatistics;
        foreach (double cutoff in new[] { 0.125, double.BitIncrement(0.125), 0.0625, 0.1 })
        {
            simulation.Reconfigure(new(simulation.Parameters with { Cutoff = cutoff, ForceScale = 0.5 },
                simulation.Matrix, 0.002, simulation.Backend, simulation.WorkerCount));
            Assert.Same(evaluator, simulation.Evaluator);
            Assert.Equal(capacity.ParticleCapacity, evaluator.IndexStatistics.ParticleCapacity);
            Assert.Equal(capacity.CellCapacity, evaluator.IndexStatistics.CellCapacity);
            simulation.Step();
            Assert.Equal(4, simulation.LastExecution!.Value.ActualWorkerCount);
            Assert.Equal(cutoff == double.BitIncrement(0.125) ? 0.25 : cutoff == 0.0625 ? 0.0625 : 0.125,
                evaluator.IndexStatistics.CellWidth);
        }
    }

    [Fact]
    public void InvalidStructuralEditsLeaveStateClockConfigurationAndWorkerOwnershipUntouched()
    {
        ParticleSnapshot initial = new([1, 2], [new(0.3, 0.4), new(0.4, 0.4)], [default, default], [0, 1]);
        using Simulation simulation = new(initial, new ModelParameters(), new InteractionMatrix(2, [0, 1, -1, 0]),
            0.001, ForceBackend.UniformGrid, 4);
        simulation.Step();
        ParticleSnapshot before = simulation.CaptureSnapshot();
        SimulationConfiguration configuration = simulation.Configuration;
        IForceEvaluator evaluator = simulation.Evaluator;
        ForceExecution? execution = simulation.LastExecution;
        ForceEvaluationStatistics statistics = simulation.LastStatistics;
        SimulationConfiguration incompatible = new(simulation.Parameters, new InteractionMatrix(1, [0]), 0.02,
            ForceBackend.UniformGrid, 8);
        Assert.Throws<ArgumentException>(() => simulation.Reconfigure(incompatible));
        Assert.Throws<ArgumentException>(() => simulation.Reconfigure(configuration, initial));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimulationConfiguration(
            simulation.Parameters with { WorldWidth = 0.01 }, simulation.Matrix, 0.001));
        AssertExactState(before, simulation.CaptureSnapshot());
        Assert.Same(configuration, simulation.Configuration);
        Assert.Same(evaluator, simulation.Evaluator);
        Assert.Equal(execution, simulation.LastExecution);
        Assert.Equal(statistics, simulation.LastStatistics);
        simulation.Step();
        Assert.Equal(4, simulation.LastExecution!.Value.ActualWorkerCount);
    }

    [Fact]
    public void StructuralGrowthShrinkAndEmptyStateRetainActualIdentitiesAndClock()
    {
        using Simulation simulation = PresetGenerator.Create(PresetKind.Random, 3, 2,
            backend: ForceBackend.UniformGrid, workerCount: 4).CreateSimulation();
        simulation.Step();
        ParticleSnapshot before = simulation.CaptureSnapshot();
        ParticleSnapshot grown = new([before.Ids[0], before.Ids[1], before.Ids[2], 1000],
            [before.Positions[0], before.Positions[1], before.Positions[2], new(-2, 3)],
            [before.Velocities[0], before.Velocities[1], before.Velocities[2], new(0.1, -0.2)],
            [0, 1, 0, 2], before.Time, before.Step);
        SimulationConfiguration expanded = new(simulation.Parameters, new InteractionMatrix(3, new double[9]),
            simulation.TimeStep, simulation.Backend, simulation.WorkerCount);
        simulation.Reconfigure(expanded, grown);
        AssertExactState(grown, simulation.CaptureSnapshot());
        simulation.Step();
        ParticleSnapshot completed = simulation.CaptureSnapshot();
        ParticleSnapshot shrunk = new([completed.Ids[3]], [completed.Positions[3]], [completed.Velocities[3]],
            [0], completed.Time, completed.Step);
        simulation.Reconfigure(new(simulation.Parameters, new InteractionMatrix(1, [0]), simulation.TimeStep,
            simulation.Backend, simulation.WorkerCount), shrunk);
        AssertExactState(shrunk, simulation.CaptureSnapshot());
        simulation.Reconfigure(simulation.Configuration, new ParticleSnapshot([], [], [], [], shrunk.Time, shrunk.Step));
        simulation.Step();
        Assert.Equal(0, simulation.Observe().Count);
        Assert.Equal(shrunk.Step + 1, simulation.Observe().Step);
    }

    [Fact]
    public void TimestepAndBackendChangesKeepAccumulatedTimeAndDisposeReplacedWorkers()
    {
        using Simulation simulation = PresetGenerator.Create(PresetKind.Random, 0, 1, timeStep: 0.01,
            backend: ForceBackend.UniformGrid, workerCount: 4).CreateSimulation();
        simulation.Step();
        simulation.Step();
        UniformGridForceEvaluator previous = Assert.IsType<UniformGridForceEvaluator>(simulation.Evaluator);
        simulation.Reconfigure(new(simulation.Parameters, simulation.Matrix, 0.025, ForceBackend.Direct));
        Assert.Equal(0.02, simulation.Observe().Time);
        Assert.Throws<ObjectDisposedException>(() => previous.Evaluate(new ParticleSnapshot([], [], [], []).Observe(), []));
        simulation.Step();
        Assert.Equal(0.02 + 0.025, simulation.Observe().Time);
        Assert.NotEqual(3 * 0.025, simulation.Observe().Time);
        simulation.Dispose();
        Assert.Throws<ObjectDisposedException>(() => simulation.Reconfigure(simulation.Configuration));
    }

    [Fact]
    public void EqualValueEditPreservesMeasurementsAndFallbackHistorySurvivesRealEdits()
    {
        ParticleSnapshot initial = new([1], [new(1e100, 0)], [default], [0]);
        using Simulation simulation = new(initial, new ModelParameters { ForceScale = 0, WallStrength = 0 },
            new InteractionMatrix(1, [0]), 0.01, ForceBackend.UniformGrid, 4);
        simulation.Step();
        ForceExecution? previous = simulation.LastExecution;
        simulation.Reconfigure(new(simulation.Parameters with { }, new InteractionMatrix(1, [0]),
            simulation.TimeStep, simulation.Backend, simulation.WorkerCount), simulation.CaptureSnapshot());
        Assert.Equal(previous, simulation.LastExecution);
        Assert.Equal(1, simulation.DirectFallbackSteps);
        simulation.Reconfigure(new(simulation.Parameters, simulation.Matrix, 0.02, ForceBackend.Direct));
        Assert.Null(simulation.LastExecution);
        Assert.Equal(1, simulation.DirectFallbackSteps);
        simulation.Step();
        Assert.Equal(1, simulation.DirectFallbackSteps);
    }

    [Theory]
    [InlineData(ForceBackend.Direct, 1)]
    [InlineData(ForceBackend.UniformGrid, 1)]
    [InlineData(ForceBackend.UniformGrid, 4)]
    public void WarmedSteppingAfterReconfigurationAllocatesNothing(ForceBackend backend, int workers)
    {
        using Simulation simulation = PresetGenerator.Create(PresetKind.Random, 64, 3, 37,
            backend: backend, workerCount: workers).CreateSimulation();
        simulation.Reconfigure(new(simulation.Parameters with { Cutoff = double.BitIncrement(0.125) },
            simulation.Matrix, 0.002, backend, workers));
        for (int i = 0; i < 128; i++)
        {
            simulation.Step();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        long backgroundBytes = 0;
        for (int i = 0; i < 128; i++)
        {
            simulation.Step();
            if (simulation.Evaluator is UniformGridForceEvaluator grid)
            {
                backgroundBytes += grid.LastWorkerAllocatedBytes;
            }
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(0, backgroundBytes);
    }

    [Fact]
    public void StructuralGrowthThenShrinkAndWorkerSwitchRetainZeroAllocationAfterWarmup()
    {
        ExperimentDefinition initial = PresetGenerator.Create(PresetKind.Random, 16, 2, 19);
        using Simulation simulation = initial.CreateSimulation();
        simulation.Step();
        ExperimentDefinition generated = PresetGenerator.Create(PresetKind.Random, 257, 3, 20);
        ParticleSnapshot growth = new(generated.InitialState.Ids, generated.InitialState.Positions,
            generated.InitialState.Velocities, generated.InitialState.Species, simulation.Observe().Time, simulation.Observe().Step);
        simulation.Reconfigure(new(initial.Parameters, generated.Matrix, 0.001, ForceBackend.UniformGrid, 4), growth);
        simulation.Step();
        ParticleSnapshot completed = simulation.CaptureSnapshot();
        ParticleSnapshot shrink = new(completed.Ids[..65], completed.Positions[..65], completed.Velocities[..65],
            completed.Species[..65], completed.Time, completed.Step);
        simulation.Reconfigure(new(simulation.Parameters with { Cutoff = 0.2 }, simulation.Matrix,
            0.002, simulation.Backend, simulation.WorkerCount), shrink);
        for (int i = 0; i < 128; i++)
        {
            simulation.Step();
        }

        UniformGridForceEvaluator evaluator = Assert.IsType<UniformGridForceEvaluator>(simulation.Evaluator);
        Assert.Equal(257, evaluator.IndexStatistics.ParticleCapacity);
        long before = GC.GetAllocatedBytesForCurrentThread();
        long workers = 0;
        for (int i = 0; i < 128; i++)
        {
            simulation.Step();
            workers += evaluator.LastWorkerAllocatedBytes;
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(0, workers);
    }

    [Fact]
    public void NumericalFailureAfterAcceptedEditRetainsTheLastCompletedParticleState()
    {
        ParticleSnapshot initial = new([1, 2], [new(0.1, 0.5), new(0.75, 0.5)], [default, default], [0, 0]);
        ModelParameters parameters = new() { Cutoff = 1, ForceScale = 0, WallStrength = 0, DampingHalfLife = null };
        using Simulation simulation = new(initial, parameters, new InteractionMatrix(1, [1]), 0.001,
            ForceBackend.UniformGrid, 4);
        simulation.Step();
        ParticleSnapshot completed = simulation.CaptureSnapshot();
        SimulationConfiguration accepted = new(parameters with { ForceScale = 1 }, simulation.Matrix,
            1e308, simulation.Backend, simulation.WorkerCount);
        simulation.Reconfigure(accepted);
        Assert.Throws<ArithmeticException>(simulation.Step);
        ReconfigurationTests.AssertExactState(completed, simulation.CaptureSnapshot());
        Assert.Same(accepted, simulation.Configuration);
        Assert.Null(simulation.LastExecution);
        Assert.Equal(0, simulation.DirectFallbackSteps);
        simulation.Reconfigure(new(parameters, simulation.Matrix, 0.001, simulation.Backend, simulation.WorkerCount));
        simulation.Step();
        Assert.Equal(completed.Step + 1, simulation.Observe().Step);
    }

    internal static void AssertExactState(ParticleSnapshot expected, ParticleSnapshot actual)
    {
        Assert.Equal(expected.Ids.ToArray(), actual.Ids.ToArray());
        Assert.Equal(expected.Positions.ToArray(), actual.Positions.ToArray());
        Assert.Equal(expected.Velocities.ToArray(), actual.Velocities.ToArray());
        Assert.Equal(expected.Species.ToArray(), actual.Species.ToArray());
        Assert.Equal(expected.Time, actual.Time);
        Assert.Equal(expected.Step, actual.Step);
    }
}
