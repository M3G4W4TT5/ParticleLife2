using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Core.Tests;

public sealed class UniformGridTests
{
    private static readonly InteractionMatrix Directed = new(3, [0.2, -0.9, 0.7, 0.8, -0.3, 0, -0.4, 0.6, 1]);

    [Theory]
    [InlineData("uniform", 0, 1729)]
    [InlineData("uniform", 1, 1729)]
    [InlineData("uniform", 257, 1729)]
    [InlineData("uniform", 257, 441)]
    [InlineData("cluster", 257, 1729)]
    [InlineData("band", 257, 1729)]
    [InlineData("adversarial", 257, 1729)]
    [InlineData("adversarial", 257, 9321)]
    public void IndependentAcceptedSetsMultiplicityAndForcesAgree(string distribution, int count, int seed)
    {
        ParticleSnapshot input = MakeState(count, distribution, seed);
        AssertParity(input, new ModelParameters());
    }

    [Fact]
    public void FacesCornersNegativeCoordinatesAndCutoffEdgesHaveExactNeighbourSets()
    {
        const double cutoff = 0.125;
        List<Vector2d> positions = [];
        foreach (int x in new[] { -9, -2, -1, 0, 1, 8 })
        {
            foreach (int y in new[] { -5, -1, 0, 1, 10 })
            {
                double faceX = x * cutoff;
                double faceY = y * cutoff;
                positions.Add(new(faceX, faceY));
                positions.Add(new(Math.BitDecrement(faceX), faceY));
                positions.Add(new(Math.BitIncrement(faceX), Math.BitDecrement(faceY)));
            }
        }

        positions.AddRange([default, new(Math.BitDecrement(cutoff), 0), new(cutoff, 0), new(Math.BitIncrement(cutoff), 0)]);
        AssertParity(Snapshot(positions.ToArray()), new ModelParameters { Cutoff = cutoff, WallStrength = 0 });
    }

    [Fact]
    public void CutoffBoundaryIsStrictEvenAcrossGridFaces()
    {
        const double cutoff = 0.125;
        foreach (double separation in new[] { Math.BitDecrement(cutoff), cutoff, Math.BitIncrement(cutoff) })
        {
            ParticleSnapshot input = Snapshot([default, new(separation, 0)]);
            UniformGridForceEvaluator evaluator = new(new ModelParameters { Cutoff = cutoff, WallStrength = 0 }, Directed);
            ForceEvaluationStatistics statistics = evaluator.Evaluate(input.Observe(), new Vector2d[2]);
            Assert.Equal(separation < cutoff ? 2 : 0, statistics.AcceptedPairs);
            Assert.Equal(0, statistics.OverlapPairs);
            AssertParity(input, new ModelParameters { Cutoff = cutoff, WallStrength = 0 });
        }
    }

    [Theory]
    [InlineData(-537)]
    [InlineData(-512)]
    [InlineData(-4)]
    [InlineData(0)]
    [InlineData(400)]
    [InlineData(511)]
    public void CellWidthCoverageTracksCutoffAcrossBinaryScales(int exponent)
    {
        double power = Math.ScaleB(1, exponent);
        foreach (double cutoff in new[] { Math.BitDecrement(power), power, Math.BitIncrement(power) })
        {
            // The model requires a positive finite cutoff square; the lowest binary scale
            // still passes that validation, including its adjacent representable cutoffs.
            UniformGridIndex index = new(cutoff);
            Assert.True(index.CellWidth >= cutoff);
            Assert.True(index.CellWidth / 2 < cutoff);
            ParticleSnapshot input = Snapshot([default, new(0.75 * cutoff, 0), new(-0.5 * cutoff, 0.25 * cutoff),
                new(3 * cutoff, -4 * cutoff), new(3.5 * cutoff, -4 * cutoff)]);
            AssertParity(input, new ModelParameters { Cutoff = cutoff, WallStrength = 0 });
        }
    }

    [Fact]
    public void FullCoordinateEqualityKeepsHashCollisionsSeparate()
    {
        UniformGridIndex index = new(0.1, new ConstantHashComparer());
        ParticleSnapshot input = Snapshot([new(-10, -10), new(-10.025, -10), new(10, 10),
            new(10.025, 10), new(0.125, 0.125), new(0.126, 0.126)]);
        Assert.True(index.Build(input.Positions));
        Assert.True(index.Statistics.OccupiedCells >= 3);
        AssertIndependentNeighbours(index, input, 0.1);
    }

    [Fact]
    public void DenseCellsRetainEveryParticleAndEveryUnorderedOverlap()
    {
        const int count = 1025;
        ParticleSnapshot input = Snapshot(Enumerable.Repeat(new Vector2d(-0.01, 1.01), count).ToArray());
        UniformGridForceEvaluator evaluator = new(new ModelParameters { WallStrength = 0 }, Directed);
        Vector2d[] forces = new Vector2d[count];
        ForceEvaluationStatistics statistics = evaluator.Evaluate(input.Observe(), forces);

        Assert.Equal((long)count * (count - 1), statistics.CandidatePairs);
        Assert.Equal((long)count * (count - 1) / 2, statistics.OverlapPairs);
        Assert.Equal(0, statistics.AcceptedPairs);
        Assert.All(forces, force => Assert.Equal(default, force));
        Assert.Equal(1, evaluator.IndexStatistics.OccupiedCells);
    }

    [Fact]
    public void RebuildClearsOldEntriesAfterMovementGrowthShrinkAndEmptyReplacement()
    {
        UniformGridIndex index = new(0.1);
        UniformGridForceEvaluator evaluator = new(new ModelParameters(), Directed);
        foreach (ParticleSnapshot input in new[]
        {
            MakeState(5, "cluster", 1), MakeState(513, "uniform", 2),
            MakeState(513, "adversarial", 3), MakeState(2, "band", 4), Snapshot([]),
            MakeState(513, "cluster", 5), MakeState(513, "uniform", 6)
        })
        {
            Assert.True(index.Build(input.Positions));
            AssertIndependentNeighbours(index, input, 0.1);
            AssertForces(input, new ModelParameters(), evaluator);
        }

        Assert.Equal(513, index.Statistics.ParticleCapacity);
        Assert.True(index.Statistics.CellCapacity >= 513);
    }

    [Fact]
    public void ReorderedStorageKeepsStableIdentityAndDirectedForceMeaning()
    {
        ParticleSnapshot input = MakeState(127, "adversarial", 712);
        int[] permutation = Enumerable.Range(0, input.Count).OrderBy(i => (i * 53) % input.Count).ToArray();
        long[] ids = input.Ids.ToArray();
        Vector2d[] positions = input.Positions.ToArray();
        Vector2d[] velocities = input.Velocities.ToArray();
        int[] species = input.Species.ToArray();
        ParticleSnapshot reordered = new(permutation.Select(i => ids[i]).ToArray(), permutation.Select(i => positions[i]).ToArray(),
            permutation.Select(i => velocities[i]).ToArray(), permutation.Select(i => species[i]).ToArray());
        UniformGridForceEvaluator evaluator = new(new ModelParameters(), Directed);
        Vector2d[] first = new Vector2d[input.Count];
        Vector2d[] second = new Vector2d[input.Count];
        ForceEvaluationStatistics before = evaluator.Evaluate(input.Observe(), first);
        ForceEvaluationStatistics after = evaluator.Evaluate(reordered.Observe(), second);

        Assert.Equal(before, after);
        for (int i = 0; i < input.Count; i++)
        {
            NumericalAssert.Close(first[i], second[reordered.Ids.IndexOf(input.Ids[i])]);
        }
    }

    [Fact]
    public void NegativeScalingUnderflowPreservesItsCellAndTinyNonzeroPairs()
    {
        UniformGridIndex index = new(1e100);
        Assert.True(index.TryMap(-double.Epsilon, out long negative));
        Assert.True(index.TryMap(double.Epsilon, out long positive));
        Assert.Equal(-1, negative);
        Assert.Equal(0, positive);
        ParticleSnapshot input = Snapshot([new(-double.Epsilon, -double.Epsilon), default,
            new(double.Epsilon, double.Epsilon), new(2 * double.Epsilon, 0)]);
        AssertParity(input, new ModelParameters { Cutoff = 1e100, WallStrength = 0 });
    }

    [Theory]
    [InlineData(1, -1074)]
    [InlineData(-512, -561)]
    public void GridRetainsAnalyticSubnormalDiagonalCoreForce(int cutoffExponent, int betaExponent)
    {
        ParticleSnapshot input = new([1, 2], [default, new(double.Epsilon, double.Epsilon)], [default, default], [0, 0]);
        UniformGridForceEvaluator evaluator = new(new ModelParameters
        {
            Cutoff = Math.ScaleB(1, cutoffExponent),
            RepulsionFraction = Math.ScaleB(1, betaExponent),
            WallStrength = 0
        }, new InteractionMatrix(1, [1]));
        Vector2d[] forces = new Vector2d[2];
        ForceEvaluationStatistics statistics = evaluator.Evaluate(input.Observe(), forces);

        // Here R beta = 2 epsilon; g = sqrt(2)/2 - 1 and each unit direction component is 1/sqrt(2).
        double component = (Math.Sqrt(2) / 2 - 1) / Math.Sqrt(2);
        NumericalAssert.Close(new Vector2d(component, component), forces[0]);
        NumericalAssert.Close(new Vector2d(-component, -component), forces[1]);
        Assert.Equal(2, statistics.AcceptedPairs);
        Assert.Equal(0, statistics.OverlapPairs);
        Assert.Equal(ForceBackend.UniformGrid, evaluator.LastExecution.ActualBackend);
    }

    [Fact]
    public void GridRetainsSubnormalSquaredCutoffAndRisingAttractionBranch()
    {
        AssertParity(Snapshot([default, new(2.9e-162, 0)]), new ModelParameters { Cutoff = 3e-162, WallStrength = 0 });
        ParticleSnapshot input = new([1, 2], [default, new(4 * double.Epsilon, 0)], [default, default], [0, 0]);
        UniformGridForceEvaluator evaluator = new(new ModelParameters
        {
            Cutoff = 2,
            RepulsionFraction = double.Epsilon,
            ForceScale = Math.ScaleB(1, 1023),
            WallStrength = 0
        }, new InteractionMatrix(1, [1]));
        Vector2d[] forces = new Vector2d[2];
        evaluator.Evaluate(input.Observe(), forces);
        Assert.Equal(new Vector2d(Math.ScaleB(1, -50), 0), forces[0]);
        Assert.Equal(new Vector2d(-Math.ScaleB(1, -50), 0), forces[1]);
    }

    [Fact]
    public void GuardBoundaryIsExplicitAndFallbackPreservesFiniteExtremeStates()
    {
        const double cutoff = 0.125;
        UniformGridIndex index = new(cutoff);
        double boundary = UniformGridIndex.CoordinateLimit * cutoff;
        Assert.True(index.TryMap(Math.BitDecrement(boundary), out _));
        Assert.True(index.TryMap(Math.BitIncrement(-boundary), out _));
        Assert.False(index.TryMap(boundary, out _));
        Assert.False(index.TryMap(-boundary, out _));
        Assert.False(index.TryMap(double.MaxValue, out _));
        AssertParity(Snapshot([new(Math.BitDecrement(boundary), 0), new(Math.BitDecrement(boundary) - 0.0625, 0),
            new(-boundary + 0.25, 0), new(-boundary + 0.3125, 0)]),
            new ModelParameters { Cutoff = cutoff, WallStrength = 0 });

        UniformGridForceEvaluator evaluator = new(new ModelParameters { Cutoff = cutoff, WallStrength = 0 }, Directed);
        ParticleSnapshot[] inputs =
        [
            Snapshot([new(boundary, 0), new(boundary, 0), new(boundary - 0.0625, 0)]),
            Snapshot([new(double.MaxValue, double.MaxValue), new(-double.MaxValue, -double.MaxValue), default, new(0.0625, 0)]),
            Snapshot([new(1e100, 0), new(1e100, 0), new(-1e100, 0)])
        ];
        foreach (ParticleSnapshot input in inputs)
        {
            AssertForces(input, new ModelParameters { Cutoff = cutoff, WallStrength = 0 }, evaluator);
            Assert.Equal(ForceBackend.Direct, evaluator.LastExecution.ActualBackend);
            Assert.Equal(GridFallbackReason.CoordinateRange, evaluator.LastExecution.FallbackReason);
            Assert.Equal(0, evaluator.IndexStatistics.OccupiedCells);
        }

        AssertForces(Snapshot([default, new(0.0625, 0)]), new ModelParameters { Cutoff = cutoff, WallStrength = 0 }, evaluator);
        Assert.Equal(ForceBackend.UniformGrid, evaluator.LastExecution.ActualBackend);
        Assert.Equal(GridFallbackReason.None, evaluator.LastExecution.FallbackReason);
    }

    [Fact]
    public void EstablishedPopulationAllowsChangingOccupancyWithoutIndexAllocation()
    {
        const int count = 257;
        UniformGridIndex index = new(0.1);
        Vector2d[] dense = new Vector2d[count];
        Vector2d[] spread = Enumerable.Range(0, count).Select(i => new Vector2d(i * 0.25, -i * 0.25)).ToArray();
        Assert.True(index.Build(dense));
        Assert.Equal(1, index.Statistics.OccupiedCells);
        GridIndexStatistics established = index.Statistics;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 100; iteration++)
        {
            index.Build(iteration % 2 == 0 ? spread : dense);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.True(index.Build(spread));
        Assert.Equal(count, index.Statistics.OccupiedCells);
        Assert.Equal(established.ParticleCapacity, index.Statistics.ParticleCapacity);
        Assert.Equal(established.CellCapacity, index.Statistics.CellCapacity);
        Assert.Equal(established.EstimatedRetainedBytes, index.Statistics.EstimatedRetainedBytes);
    }

    [Fact]
    public void FailedEvaluationRetainsPriorExecutionMetadataAndRejectsAliasing()
    {
        ModelParameters parameters = new() { WallStrength = double.MaxValue, ForceScale = 0 };
        UniformGridForceEvaluator evaluator = new(parameters, Directed);
        ParticleSnapshot good = Snapshot([new(0.5, 0.5)]);
        evaluator.Evaluate(good.Observe(), new Vector2d[1]);
        ForceExecution successful = evaluator.LastExecution;
        ParticleSnapshot bad = Snapshot([new(0.5, 0.5), new(-1, 0.5)]);
        Assert.Throws<ArithmeticException>(() => evaluator.Evaluate(bad.Observe(), new Vector2d[2]));
        Assert.Equal(successful, evaluator.LastExecution);
        Assert.Throws<ArgumentException>(() => evaluator.Evaluate(good.Observe(), []));

        Vector2d[] positions = [new(0.5, 0.5)];
        long[] ids = [1];
        int[] species = [0];
        Vector2d[] velocities = [default];
        Assert.Throws<ArgumentException>(() => evaluator.Evaluate(new ParticleStateView(ids, positions, velocities, species, 0, 0), positions));
        Assert.Throws<ArgumentException>(() => evaluator.Evaluate(new ParticleStateView(ids, positions, velocities, species, 0, 0), velocities));
    }

    private static void AssertParity(ParticleSnapshot input, ModelParameters parameters)
    {
        UniformGridIndex index = new(parameters.Cutoff);
        Assert.True(index.Build(input.Positions));
        AssertIndependentNeighbours(index, input, parameters.Cutoff);
        AssertForces(input, parameters, new UniformGridForceEvaluator(parameters, Directed));
    }

    private static void AssertForces(ParticleSnapshot input, ModelParameters parameters, UniformGridForceEvaluator grid)
    {
        Vector2d[] directForces = new Vector2d[input.Count];
        Vector2d[] gridForces = new Vector2d[input.Count];
        DirectForceEvaluator direct = new(parameters, Directed);
        ForceEvaluationStatistics expected = direct.Evaluate(input.Observe(), directForces);
        ForceEvaluationStatistics actual = grid.Evaluate(input.Observe(), gridForces);
        Assert.Equal(expected.AcceptedPairs, actual.AcceptedPairs);
        Assert.Equal(expected.OverlapPairs, actual.OverlapPairs);
        Assert.InRange(actual.CandidatePairs, actual.AcceptedPairs + 2 * actual.OverlapPairs, expected.CandidatePairs);
        for (int i = 0; i < input.Count; i++)
        {
            // Exact neighbour-set checks independently prohibit missing or duplicate sources.
            // Reduced-unit sums over <= 513 particles allow 1e-12 absolute + relative for order roundoff.
            NumericalAssert.Close(directForces[i], gridForces[i]);
        }
    }

    private static void AssertIndependentNeighbours(UniformGridIndex index, ParticleSnapshot input, double cutoff)
    {
        for (int receiver = 0; receiver < input.Count; receiver++)
        {
            HashSet<int> expected = [];
            HashSet<int> actual = [];
            HashSet<int> candidates = [];
            for (int source = 0; source < input.Count; source++)
            {
                if (source != receiver && IndependentlyInside(input.Positions[source] - input.Positions[receiver], cutoff))
                {
                    expected.Add(source);
                }
            }

            UniformGridIndex.CandidateEnumerator query = index.Candidates(receiver);
            while (query.MoveNext())
            {
                int source = query.Current;
                Assert.True(candidates.Add(source), $"Duplicate source {source} for receiver {receiver}.");
                if (source != receiver && IndependentlyInside(input.Positions[source] - input.Positions[receiver], cutoff))
                {
                    actual.Add(source);
                }
            }

            Assert.True(expected.SetEquals(actual), $"Neighbour sets differ for receiver {receiver}.");
        }
    }

    private static bool IndependentlyInside(Vector2d displacement, double cutoff)
    {
        // Independent enumeration and robust norm; coincidences are checked separately by count.
        return (displacement.X != 0 || displacement.Y != 0) && double.Hypot(displacement.X, displacement.Y) < cutoff;
    }

    private static ParticleSnapshot MakeState(int count, string distribution, int seed)
    {
        Random random = new(seed);
        Vector2d[] positions = new Vector2d[count];
        for (int i = 0; i < count; i++)
        {
            double x = random.NextDouble();
            double y = random.NextDouble();
            positions[i] = distribution switch
            {
                "cluster" => new(-0.02 + x * 0.025, 1.01 + y * 0.025),
                "band" => new(-0.4 + 1.8 * x, 0.499 + 0.002 * y),
                "adversarial" => new(Math.Round(32 * x - 16) * 0.125 + (i % 2 == 0 ? 1e-15 : -1e-15),
                    Math.Round(32 * y - 16) * 0.125 + (i % 3 == 0 ? 1e-15 : -1e-15)),
                _ => new(-0.4 + 1.8 * x, -0.4 + 1.8 * y)
            };
        }

        if (count > 4)
        {
            positions[3] = positions[2];
        }

        return Snapshot(positions);
    }

    private static ParticleSnapshot Snapshot(Vector2d[] positions) => new(
        Enumerable.Range(0, positions.Length).Select(i => 19L + 17L * i).ToArray(), positions,
        new Vector2d[positions.Length], Enumerable.Range(0, positions.Length).Select(i => i % 3).ToArray());

    private sealed class ConstantHashComparer : IEqualityComparer<UniformGridIndex.CellCoordinate>
    {
        public bool Equals(UniformGridIndex.CellCoordinate x, UniformGridIndex.CellCoordinate y) => x == y;

        public int GetHashCode(UniformGridIndex.CellCoordinate obj) => 0;
    }
}
