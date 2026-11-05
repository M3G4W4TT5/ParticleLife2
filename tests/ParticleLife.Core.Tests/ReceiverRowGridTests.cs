using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Core.Tests;

public sealed class ReceiverRowGridTests
{
    [Theory]
    [InlineData(-537, 1)]
    [InlineData(-4, 1)]
    [InlineData(-4, 4)]
    [InlineData(400, 1)]
    public void RowStencilsRetainEveryAcceptedSourceOnceAcrossFacesAndCutoffTransitions(int exponent, int workers)
    {
        double power = Math.ScaleB(1, exponent);
        foreach (double maximum in new[] { Math.BitDecrement(power), power, Math.BitIncrement(power) })
        {
            ModelParameters parameters = new() { Boundary = BoundaryKind.Open, Cutoff = maximum, WallStrength = 0 };
            InteractionMatrix matrix = Matrix(maximum, exponent == -537 ? maximum * 0.9 : null);
            List<Vector2d> positions = [];
            double width = Math.ScaleB(1, Math.ILogB(maximum) + (Math.ScaleB(1, Math.ILogB(maximum)) < maximum ? 1 : 0));
            foreach (int x in new[] { -5, -1, 0, 1, 4 })
            {
                foreach (int y in new[] { -2, 0, 3 })
                {
                    double faceX = x * width, faceY = y * width;
                    positions.Add(new(faceX, faceY)); positions.Add(new(Math.BitDecrement(faceX), Math.BitIncrement(faceY)));
                }
            }
            positions.AddRange([default, default, new(Math.BitDecrement(maximum / 8), 0), new(maximum / 8, 0), new(Math.BitIncrement(maximum / 8), 0)]);
            ParticleSnapshot state = Snapshot(positions.ToArray());
            UniformGridIndex index = new(maximum);
            Assert.True(index.Build(state.Positions));
            PairForceKernel kernel = new(parameters, matrix);
            for (int receiver = 0; receiver < state.Count; receiver++)
            {
                int row = state.Species[receiver];
                double radius = matrix.Ranges!.CutoffRadii[row * 3];
                HashSet<int> candidates = [];
                var ranges = index.CandidateRanges(receiver, state.Positions[receiver], radius);
                while (ranges.MoveNext())
                {
                    for (int slot = ranges.Current.Start; slot < ranges.Current.End; slot++) { Assert.True(candidates.Add(index.PackedSources[slot])); }
                }
                for (int source = 0; source < state.Count; source++)
                {
                    if (receiver == source) { continue; }
                    PairKind kind = kernel.Evaluate(state.Positions[source] - state.Positions[receiver], row * 3 + state.Species[source], out _);
                    if (kind != PairKind.Rejected) { Assert.Contains(source, candidates); }
                }
            }
            AssertParity(state, parameters, matrix, workers);
        }
    }

    [Fact]
    public void SubnormalSquaredRowRadiiAndCoordinateFallbackAgreeWithDirect()
    {
        ModelParameters parameters = new() { Boundary = BoundaryKind.Open, Cutoff = 1e-150, WallStrength = 0 };
        InteractionMatrix matrix = Matrix(1e-150, minimum: 1e-160);
        AssertParity(Snapshot([default, new(0.9e-160, 0), new(-0.7e-160, 0.4e-160), new(1e-150, -1e-150)]), parameters, matrix, 1);
        parameters = parameters with { Cutoff = 1e150 };
        matrix = Matrix(1e150, minimum: 1e-160);
        AssertParity(Snapshot([default, new(-double.Epsilon, double.Epsilon), new(0.9e-160, 0), new(-0.7e-160, 0.4e-160)]), parameters, matrix, 1);
        parameters = parameters with { Cutoff = 0.2 };
        matrix = Matrix(0.2);
        ParticleSnapshot far = Snapshot([new(1e20, 0), new(1e20, 0), new(-1e20, 0)]);
        using UniformGridForceEvaluator evaluator = new(parameters, matrix);
        evaluator.Evaluate(far.Observe(), new Vector2d[far.Count]);
        Assert.Equal(ForceBackend.Direct, evaluator.LastExecution.ActualBackend);
        Assert.Equal(GridFallbackReason.CoordinateRange, evaluator.LastExecution.FallbackReason);
        AssertParity(far, parameters, matrix, 1);
    }

    [Fact]
    public void OnlyNonperiodicDifferentReceiverRowsUseRowRectangles()
    {
        ModelParameters parameters = new() { Boundary = BoundaryKind.Open, Cutoff = 0.2 };
        InteractionMatrix different = Matrix(0.2);
        using UniformGridForceEvaluator fine = new(parameters, different);
        using UniformGridForceEvaluator global = new(parameters, different.WithRanges(null));
        using UniformGridForceEvaluator periodic = new(parameters with { Boundary = BoundaryKind.Periodic }, different);
        using UniformGridForceEvaluator equal = new(parameters, different.WithRanges(new(3, new double[9], Enumerable.Repeat(0.2, 9).ToArray())));
        Assert.Equal(0.25, fine.IndexStatistics.CellWidth);
        Assert.True(fine.UsesReceiverRowStencil);
        Assert.False(global.UsesReceiverRowStencil); Assert.False(equal.UsesReceiverRowStencil); Assert.False(periodic.UsesReceiverRowStencil);
        Assert.Equal(0.25, global.IndexStatistics.CellWidth);
        Assert.Equal(0.25, equal.IndexStatistics.CellWidth);
        Assert.Equal(0.2, periodic.IndexStatistics.CellWidth);
        fine.Reconfigure(parameters, different.WithRanges(null));
        Assert.Equal(0.25, fine.IndexStatistics.CellWidth);
        Assert.False(fine.UsesReceiverRowStencil);
        fine.Reconfigure(parameters, different);
        Assert.Equal(0.25, fine.IndexStatistics.CellWidth);
        Assert.True(fine.UsesReceiverRowStencil);
    }

    [Fact]
    public void EvolvingRowsMatchDirectAndWorkerSchedulingKeepsIdenticalReceiverSums()
    {
        ExperimentDefinition source = PresetGenerator.Create(PresetKind.Random, 128, 3, 19);
        ModelParameters parameters = source.Parameters with { Boundary = BoundaryKind.Open, Cutoff = 0.2 };
        InteractionMatrix matrix = Matrix(0.2);
        using Simulation direct = new(source.InitialState, parameters, matrix, source.TimeStep, ForceBackend.Direct);
        using Simulation serial = new(source.InitialState, parameters, matrix, source.TimeStep, ForceBackend.UniformGrid);
        using Simulation parallel = new(source.InitialState, parameters, matrix, source.TimeStep, ForceBackend.UniformGrid, 4);
        for (int step = 0; step < 16; step++)
        {
            direct.Step(); serial.Step(); parallel.Step();
            NumericalAssert.SameState(direct.CaptureSnapshot(), serial.CaptureSnapshot());
            Assert.Equal(serial.Observe().Positions.ToArray(), parallel.Observe().Positions.ToArray());
            Assert.Equal(serial.Observe().Velocities.ToArray(), parallel.Observe().Velocities.ToArray());
            Assert.Equal(serial.LastStatistics, parallel.LastStatistics);
        }
    }

    [Fact]
    public void RowRectanglesPreserveOriginalCoarseAcceptedContributionOrder()
    {
        ExperimentDefinition source = PresetGenerator.Create(PresetKind.Random, 257, 3, 71);
        ModelParameters parameters = source.Parameters with { Boundary = BoundaryKind.Open, Cutoff = 0.2 };
        InteractionMatrix matrix = Matrix(0.2);
        ParticleStateView state = source.InitialState.Observe();
        UniformGridIndex original = new(0.2);
        Assert.True(original.Build(state.Positions));
        PairForceKernel kernel = new(parameters, matrix);
        Vector2d[] expected = new Vector2d[state.Count], actual = new Vector2d[state.Count];
        for (int receiver = 0; receiver < state.Count; receiver++)
        {
            Vector2d total = default;
            UniformGridIndex.CandidateEnumerator candidates = original.Candidates(receiver);
            while (candidates.MoveNext())
            {
                int candidate = candidates.Current;
                if (candidate == receiver) { continue; }
                if (kernel.Evaluate(state.Positions[candidate] - state.Positions[receiver], state.Species[receiver] * 3 + state.Species[candidate], out Vector2d force) == PairKind.Accepted)
                {
                    total += force;
                }
            }
            expected[receiver] = total;
        }
        using UniformGridForceEvaluator evaluator = new(parameters, matrix);
        evaluator.Evaluate(state, actual);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void WarmChangedOccupancyAndMovingStepsAllocateZero(int workers)
    {
        ModelParameters parameters = new() { Boundary = BoundaryKind.Open, Cutoff = 0.2, WallStrength = 0 };
        InteractionMatrix matrix = Matrix(0.2);
        ParticleSnapshot spread = Snapshot(Enumerable.Range(0, 128).Select(i => new Vector2d((i % 16) * 0.13, (i / 16) * 0.14)).ToArray());
        ParticleSnapshot dense = Snapshot(Enumerable.Range(0, 128).Select(i => new Vector2d(i * 1e-5, -i * 1e-5)).ToArray());
        using UniformGridForceEvaluator evaluator = new(parameters, matrix, workers);
        Vector2d[] forces = new Vector2d[spread.Count];
        for (int i = 0; i < 32; i++) { evaluator.Evaluate((i % 2 == 0 ? dense : spread).Observe(), forces); }
        long before = GC.GetAllocatedBytesForCurrentThread(), workerBytes = 0;
        for (int i = 0; i < 32; i++) { evaluator.Evaluate((i % 2 == 0 ? dense : spread).Observe(), forces); workerBytes += evaluator.LastWorkerAllocatedBytes; }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before); Assert.Equal(0, workerBytes);
        using Simulation simulation = new(spread, parameters, matrix, 0.001, ForceBackend.UniformGrid, workers);
        for (int i = 0; i < 64; i++) { simulation.Step(); }
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 32; i++) { simulation.Step(); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static InteractionMatrix Matrix(double maximum, double? minimum = null)
    {
        double[] cutoffs = new double[9], cores = new double[9];
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                int cell = row * 3 + column; cutoffs[cell] = row == 0 ? maximum : minimum ?? maximum / (row == 1 ? 8 : 3);
                cores[cell] = row == 2 ? 0 : cutoffs[cell] * 0.3;
            }
        }
        return new(3, [0.4, -0.7, 0, 0.8, -0.3, 0.5, -0.4, 0.6, 1], new(3, cores, cutoffs));
    }
    private static ParticleSnapshot Snapshot(Vector2d[] positions) => new(Enumerable.Range(0, positions.Length).Select(i => (long)i).ToArray(),
        positions, new Vector2d[positions.Length], Enumerable.Range(0, positions.Length).Select(i => i % 3).ToArray());
    private static void AssertParity(ParticleSnapshot state, ModelParameters parameters, InteractionMatrix matrix, int workers)
    {
        Vector2d[] expected = new Vector2d[state.Count], actual = new Vector2d[state.Count];
        ForceEvaluationStatistics reference = new DirectForceEvaluator(parameters, matrix).Evaluate(state.Observe(), expected);
        using UniformGridForceEvaluator evaluator = new(parameters, matrix, workers);
        ForceEvaluationStatistics result = evaluator.Evaluate(state.Observe(), actual);
        Assert.Equal(reference.AcceptedPairs, result.AcceptedPairs); Assert.Equal(reference.OverlapPairs, result.OverlapPairs);
        for (int i = 0; i < state.Count; i++) { NumericalAssert.Close(expected[i].X, actual[i].X, 1e-11, 1e-11); NumericalAssert.Close(expected[i].Y, actual[i].Y, 1e-11, 1e-11); }
    }
}
