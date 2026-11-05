using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Core.Tests;

public sealed class PackedGridTests
{
    private static readonly InteractionMatrix Directed = new(3, [0.2, -0.9, 0.7, 0.8, -0.3, 0, -0.4, 0.6, 1]);

    [Theory]
    [InlineData("uniform")]
    [InlineData("dense")]
    [InlineData("mixed")]
    [InlineData("boundary")]
    public void WorkerCountsPreserveEveryReceiverSumAndReferenceCounts(string distribution)
    {
        ParticleSnapshot input = MakeState(257, distribution);
        ModelParameters parameters = new();
        Vector2d[] reference = new Vector2d[input.Count];
        ForceEvaluationStatistics direct = new DirectForceEvaluator(parameters, Directed).Evaluate(input.Observe(), reference);
        using UniformGridForceEvaluator serial = new(parameters, Directed);
        Vector2d[] expected = new Vector2d[input.Count];
        ForceEvaluationStatistics expectedStatistics = serial.Evaluate(input.Observe(), expected);
        Assert.Equal(direct.AcceptedPairs, expectedStatistics.AcceptedPairs);
        Assert.Equal(direct.OverlapPairs, expectedStatistics.OverlapPairs);
        for (int i = 0; i < input.Count; i++)
        {
            // Same independent enumeration contract as M4: order-one force sums over 257
            // particles allow 1e-12 absolute + relative. Worker/serial equality below is exact.
            NumericalAssert.Close(reference[i], expected[i]);
        }

        foreach (int workerCount in new[] { 1, 2, 4, 8, 12, 24 })
        {
            using UniformGridForceEvaluator evaluator = new(parameters, Directed, workerCount);
            Vector2d[] actual = new Vector2d[input.Count];
            for (int repeat = 0; repeat < 5; repeat++)
            {
                Assert.Equal(expectedStatistics, evaluator.Evaluate(input.Observe(), actual));
                Assert.Equal(expected, actual);
                Assert.Equal(workerCount, evaluator.LastExecution.ConfiguredWorkerCount);
                Assert.Equal(workerCount, evaluator.LastExecution.ActualWorkerCount);
            }
        }
    }

    [Fact]
    public void PackingPreservesOriginalM4CellAndWithinCellOrdering()
    {
        ParticleSnapshot input = MakeState(127, "boundary");
        UniformGridIndex index = new(0.125);
        Assert.True(index.Build(input.Positions));
        for (int receiver = 0; receiver < input.Count; receiver++)
        {
            Assert.True(index.TryMap(input.Positions[receiver].X, out long centerX));
            Assert.True(index.TryMap(input.Positions[receiver].Y, out long centerY));
            List<int> expected = [];
            for (int y = -1; y <= 1; y++)
            {
                for (int x = -1; x <= 1; x++)
                {
                    for (int source = 0; source < input.Count; source++)
                    {
                        index.TryMap(input.Positions[source].X, out long sourceX);
                        index.TryMap(input.Positions[source].Y, out long sourceY);
                        if (sourceX == centerX + x && sourceY == centerY + y)
                        {
                            expected.Add(source);
                        }
                    }
                }
            }

            List<int> actual = [];
            UniformGridIndex.CandidateEnumerator query = index.Candidates(receiver);
            while (query.MoveNext())
            {
                actual.Add(query.Current);
            }

            Assert.Equal(expected, actual);
            Assert.Equal(actual.Count, actual.Distinct().Count());
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void WorkerStateAndBuffersRebuildAfterReplacementGrowthShrinkAndEmptyState(int workerCount)
    {
        ModelParameters parameters = new();
        using UniformGridForceEvaluator serial = new(parameters, Directed);
        using UniformGridForceEvaluator parallel = new(parameters, Directed, workerCount);
        foreach (ParticleSnapshot input in new[]
        {
            MakeState(3, "dense"), MakeState(513, "mixed"), MakeState(127, "boundary"),
            MakeState(0, "uniform"), MakeState(513, "dense"), MakeState(513, "uniform")
        })
        {
            Vector2d[] expected = new Vector2d[input.Count];
            Vector2d[] actual = new Vector2d[input.Count];
            Assert.Equal(serial.Evaluate(input.Observe(), expected), parallel.Evaluate(input.Observe(), actual));
            Assert.Equal(expected, actual);
        }

        Assert.Equal(513, parallel.IndexStatistics.ParticleCapacity);
    }

    [Theory]
    [InlineData(1, -1074)]
    [InlineData(-512, -561)]
    public void ParallelRetainsAnalyticSubnormalDiagonalForces(int cutoffExponent, int betaExponent)
    {
        ParticleSnapshot input = new([1, 2], [default, new(double.Epsilon, double.Epsilon)], [default, default], [0, 0]);
        ModelParameters parameters = new()
        {
            Cutoff = Math.ScaleB(1, cutoffExponent),
            RepulsionFraction = Math.ScaleB(1, betaExponent),
            WallStrength = 0
        };
        double component = (Math.Sqrt(2) / 2 - 1) / Math.Sqrt(2);
        foreach (int workers in new[] { 2, 4, 8 })
        {
            using UniformGridForceEvaluator evaluator = new(parameters, new InteractionMatrix(1, [1]), workers);
            Vector2d[] forces = new Vector2d[2];
            Assert.Equal(2, evaluator.Evaluate(input.Observe(), forces).AcceptedPairs);
            NumericalAssert.Close(new Vector2d(component, component), forces[0]);
            NumericalAssert.Close(new Vector2d(-component, -component), forces[1]);
            Assert.Equal(GridFallbackReason.None, evaluator.LastExecution.FallbackReason);
        }
    }

    [Fact]
    public void ParallelRetainsTinyAttractionAndSubnormalSquaredCutoff()
    {
        foreach (int workers in new[] { 2, 4, 8 })
        {
            ParticleSnapshot input = new([1, 2], [default, new(4 * double.Epsilon, 0)], [default, default], [0, 0]);
            ModelParameters parameters = new()
            {
                Cutoff = 2,
                RepulsionFraction = double.Epsilon,
                ForceScale = Math.ScaleB(1, 1023),
                WallStrength = 0
            };
            using UniformGridForceEvaluator evaluator = new(parameters, new InteractionMatrix(1, [1]), workers);
            Vector2d[] forces = new Vector2d[2];
            evaluator.Evaluate(input.Observe(), forces);
            Assert.Equal(new Vector2d(Math.ScaleB(1, -50), 0), forces[0]);
            Assert.Equal(new Vector2d(-Math.ScaleB(1, -50), 0), forces[1]);

            ParticleSnapshot tiny = new([1, 2], [default, new(2.9e-162, 0)], [default, default], [0, 0]);
            parameters = new() { Cutoff = 3e-162, WallStrength = 0 };
            using UniformGridForceEvaluator tinyEvaluator = new(parameters, new InteractionMatrix(1, [1]), workers);
            Vector2d[] expected = new Vector2d[2];
            Assert.Equal(new DirectForceEvaluator(parameters, new InteractionMatrix(1, [1])).Evaluate(tiny.Observe(), expected),
                tinyEvaluator.Evaluate(tiny.Observe(), forces));
            Assert.Equal(expected, forces);
        }
    }

    [Theory]
    [InlineData(-537)]
    [InlineData(-4)]
    [InlineData(511)]
    public void ParallelStrictCutoffMatchesReferenceAcrossCellWidthTransitions(int exponent)
    {
        double power = Math.ScaleB(1, exponent);
        foreach (double cutoff in new[] { Math.BitDecrement(power), power, Math.BitIncrement(power) })
        {
            ParticleSnapshot input = new([1, 2, 3, 4, 5],
                [default, new(Math.BitDecrement(cutoff), 0), new(cutoff, 0),
                    new(Math.BitIncrement(cutoff), 0), new(-0.5 * cutoff, 0)], new Vector2d[5], [0, 1, 2, 1, 0]);
            ModelParameters parameters = new() { Cutoff = cutoff, WallStrength = 0 };
            Vector2d[] reference = new Vector2d[input.Count];
            ForceEvaluationStatistics expected = new DirectForceEvaluator(parameters, Directed).Evaluate(input.Observe(), reference);
            using UniformGridForceEvaluator evaluator = new(parameters, Directed, 4);
            Vector2d[] actual = new Vector2d[input.Count];
            ForceEvaluationStatistics observed = evaluator.Evaluate(input.Observe(), actual);
            Assert.Equal(expected.AcceptedPairs, observed.AcceptedPairs);
            Assert.Equal(expected.OverlapPairs, observed.OverlapPairs);
            for (int i = 0; i < input.Count; i++)
            {
                NumericalAssert.Close(reference[i], actual[i]);
            }
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void ParallelFallbackCountsAndNumericalFailureRemainAtomic(int workerCount)
    {
        ModelParameters parameters = new() { Cutoff = 1, ForceScale = 1e308, WallStrength = 0, DampingHalfLife = null };
        InteractionMatrix matrix = new(2, [0, 1, 0, 0]);
        ParticleSnapshot input = new([1, 2, 3, 4, 5],
            [default, new(0.65, 0), new(-0.65, 0), new(0.65, 0), new(-0.65, 0)], new Vector2d[5], [0, 1, 1, 1, 1]);
        using UniformGridForceEvaluator evaluator = new(parameters, matrix, workerCount);
        Vector2d[] actual = new Vector2d[5];
        Vector2d[] expected = new Vector2d[5];
        ForceEvaluationStatistics direct = new DirectForceEvaluator(parameters, matrix).Evaluate(input.Observe(), expected);
        Assert.Equal(direct, evaluator.Evaluate(input.Observe(), actual));
        Assert.Equal(expected, actual);
        Assert.Equal(GridFallbackReason.AccumulationRange, evaluator.LastExecution.FallbackReason);
        Assert.Equal(1, evaluator.LastExecution.ActualWorkerCount);
        Assert.Equal(workerCount, evaluator.LastExecution.ConfiguredWorkerCount);

        // No cancelling negative source: both packed and direct orders overflow. The prior
        // successful execution record must survive failure, and all workers have joined.
        ParticleSnapshot overflowing = new([1, 2, 3, 4, 5],
            [default, new(0.65, 0), new(0.65, 0), new(0.65, 0), new(0.65, 0)], new Vector2d[5], [0, 1, 1, 1, 1]);
        ForceExecution successful = evaluator.LastExecution;
        Assert.Throws<ArithmeticException>(() => evaluator.Evaluate(overflowing.Observe(), actual));
        Assert.Equal(successful, evaluator.LastExecution);
        Assert.Equal(direct, evaluator.Evaluate(input.Observe(), actual));
        Assert.Equal(expected, actual);

        ParticleSnapshot far = new([1, 2], [new(double.MaxValue, 0), new(-double.MaxValue, 0)], [default, default], [0, 1]);
        evaluator.Evaluate(far.Observe(), new Vector2d[2]);
        Assert.Equal(GridFallbackReason.CoordinateRange, evaluator.LastExecution.FallbackReason);
        Assert.Equal(1, evaluator.LastExecution.ActualWorkerCount);
    }

    [Fact]
    public async Task WorkerBarrierPropagatesExceptionsOnlyAfterEveryWorkerCompletesAndCanBeReused()
    {
        InvalidOperationException failure = new("deliberate worker callback failure");
        int finished = 0;
        bool fail = true;
        using ManualResetEventSlim blockedWorkerStarted = new(false);
        using ManualResetEventSlim mayFinish = new(false);
        using ManualResetEventSlim failureReached = new(false);
        using ForceWorkerPool pool = new(4, worker =>
        {
            if (worker == 1 && fail)
            {
                blockedWorkerStarted.Wait();
                failureReached.Set();
                throw failure;
            }

            if (worker == 2 && fail)
            {
                blockedWorkerStarted.Set();
                mayFinish.Wait();
            }

            Interlocked.Increment(ref finished);
        });
        Task<Exception?> run = Task.Run(() => Record.Exception(pool.Run), TestContext.Current.CancellationToken);
        try
        {
            Assert.True(failureReached.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.False(run.IsCompleted);
        }
        finally
        {
            mayFinish.Set();
        }

        Assert.Same(failure, await run);
        Assert.Equal(3, finished);
        fail = false;
        pool.Run();
        Assert.Equal(7, finished);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void WarmedChangingOccupancyAllocatesNothingOnCallerOrWorkers(int workerCount)
    {
        ParticleSnapshot dense = MakeState(257, "dense");
        ParticleSnapshot spread = MakeState(257, "uniform");
        using UniformGridForceEvaluator evaluator = new(new ModelParameters(), Directed, workerCount);
        Vector2d[] forces = new Vector2d[257];
        for (int iteration = 0; iteration < 64; iteration++)
        {
            evaluator.Evaluate((iteration % 2 == 0 ? dense : spread).Observe(), forces);
        }

        long workerAllocated = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 64; iteration++)
        {
            evaluator.Evaluate((iteration % 2 == 0 ? dense : spread).Observe(), forces);
            workerAllocated += evaluator.LastWorkerAllocatedBytes;
        }

        long callerAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, callerAllocated);
        Assert.Equal(0, workerAllocated);
    }

    [Fact]
    public void ParallelSimulationMatchesSerialAcrossEvolutionAndFailedPublication()
    {
        ExperimentDefinition experiment = PresetGenerator.Create(PresetKind.Random, 96, 4, 1729, timeStep: 0.001);
        using Simulation serial = experiment.CreateSimulation(ForceBackend.UniformGrid);
        using Simulation parallel = new(experiment.InitialState, experiment.Parameters, experiment.Matrix,
            experiment.TimeStep, ForceBackend.UniformGrid, 4);
        for (int step = 0; step < 120; step++)
        {
            serial.Step();
            parallel.Step();
            Assert.Equal(serial.Observe().Positions.ToArray(), parallel.Observe().Positions.ToArray());
            Assert.Equal(serial.Observe().Velocities.ToArray(), parallel.Observe().Velocities.ToArray());
            Assert.Equal(serial.LastStatistics, parallel.LastStatistics);
        }

        ParticleSnapshot initial = new([11, 12], [new(0, 0), new(0.05, 0)],
            [new(1e308, 0), new(1e308, 0)], [0, 0]);
        ModelParameters ballistic = new() { ForceScale = 0, WallStrength = 0, DampingHalfLife = null };
        using Simulation failing = new(initial, ballistic, new InteractionMatrix(1, [0]), 1, ForceBackend.UniformGrid, 4);
        failing.Step();
        ParticleSnapshot completed = failing.CaptureSnapshot();
        ForceExecution? execution = failing.LastExecution;
        ForceEvaluationStatistics statistics = failing.LastStatistics;
        Assert.Throws<ArithmeticException>(failing.Step);
        NumericalAssert.SameState(completed, failing.CaptureSnapshot());
        Assert.Equal(execution, failing.LastExecution);
        Assert.Equal(statistics, failing.LastStatistics);
        Assert.Equal(0, failing.DirectFallbackSteps);
    }

    [Fact]
    public void WorkerConfigurationAndDisposedLifetimeAreExplicit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UniformGridForceEvaluator(new(), Directed, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UniformGridForceEvaluator(new(), Directed, 65));
        UniformGridForceEvaluator evaluator = new(new(), Directed, 4);
        evaluator.Dispose();
        evaluator.Dispose();
        ParticleSnapshot input = MakeState(0, "uniform");
        Assert.Throws<ObjectDisposedException>(() => evaluator.Evaluate(input.Observe(), []));
    }

    [Fact]
    public void ParallelForceFailureDoesNotPublishStateOrFallbackMetadata()
    {
        ParticleSnapshot initial = new([11, 12], [new(0.5, 0.5), new(0.55, 0.5)],
            [new(-1, 0), new(-1, 0)], [0, 0]);
        ModelParameters parameters = new() { ForceScale = 0, WallStrength = double.MaxValue, DampingHalfLife = null };
        using Simulation simulation = new(initial, parameters, new InteractionMatrix(1, [0]), 1, ForceBackend.UniformGrid, 4);
        simulation.Step();
        ParticleSnapshot completed = simulation.CaptureSnapshot();
        ForceExecution? execution = simulation.LastExecution;
        ForceEvaluationStatistics statistics = simulation.LastStatistics;
        Assert.Throws<ArithmeticException>(simulation.Step);
        NumericalAssert.SameState(completed, simulation.CaptureSnapshot());
        Assert.Equal(execution, simulation.LastExecution);
        Assert.Equal(statistics, simulation.LastStatistics);
        Assert.Equal(0, simulation.DirectFallbackSteps);
        Assert.Equal(0, simulation.AccumulationFallbackSteps);
    }

    private static ParticleSnapshot MakeState(int count, string distribution)
    {
        Random random = new(1729);
        Vector2d[] positions = new Vector2d[count];
        for (int i = 0; i < count; i++)
        {
            double x = random.NextDouble();
            double y = random.NextDouble();
            positions[i] = distribution switch
            {
                "dense" => new(0.48 + 0.04 * x, 0.48 + 0.04 * y),
                "mixed" when i < count / 2 => new(0.48 + 0.04 * x, 0.48 + 0.04 * y),
                "boundary" => new(Math.Round(16 * x - 8) * 0.125 + (i % 2 == 0 ? 1e-15 : -1e-15),
                    Math.Round(16 * y - 8) * 0.125 + (i % 3 == 0 ? 1e-15 : -1e-15)),
                _ => new(-0.2 + 1.4 * x, -0.2 + 1.4 * y)
            };
        }

        if (count > 4)
        {
            positions[3] = positions[2];
        }

        return new(Enumerable.Range(0, count).Select(i => 19L + 17L * i).ToArray(), positions,
            new Vector2d[count], Enumerable.Range(0, count).Select(i => i % 3).ToArray());
    }
}
