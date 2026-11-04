using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Core.Tests;

public sealed class DiagnosticSamplerTests
{
    [Theory]
    [InlineData("uniform")]
    [InlineData("dense")]
    [InlineData("band")]
    [InlineData("boundary")]
    [InlineData("duplicates")]
    public void SpatialComponentsAndSpeedsEqualIndependentReference(string distribution)
    {
        Random random = new(8327);
        Vector2d[] positions = new Vector2d[257];
        Vector2d[] velocities = new Vector2d[positions.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            double x = random.NextDouble();
            double y = random.NextDouble();
            positions[i] = distribution switch
            {
                "dense" => new(0.48 + x * 0.04, 0.48 + y * 0.04),
                "band" => new(x, 0.49 + y * 0.02),
                "boundary" => new(x * 1.4 - 0.2, y * 1.4 - 0.2),
                "duplicates" => new(i % 7 * 0.125, i % 3 * 0.125),
                _ => new(x, y)
            };
            velocities[i] = new(random.NextDouble() - 0.5, random.NextDouble() - 0.5);
        }

        ParticleSnapshot state = Snapshot(positions, velocities, time: 2.5, step: 360);
        DiagnosticSampler sampler = new();
        foreach (double radius in new[] { 0.03125, 0.1, 0.125, 0.25 })
        {
            Assert.Equal(Diagnostics.Sample(state.Observe(), radius), sampler.Sample(state.Observe(), radius));
            Assert.True(sampler.LastExecution.UsedSpatialIndex);
            Assert.InRange(sampler.LastExecution.DistanceEvaluations, 0, sampler.LastExecution.CandidatePairs);
        }
    }

    [Fact]
    public void InclusiveBinaryRadiusCoversRoundedDisplacementAcrossTwoNarrowCells()
    {
        // At h=R these positions occupy cells -1 and 1, but their rounded separation is exactly R.
        ParticleSnapshot state = Snapshot([new(-double.Epsilon, 0), new(0.125, 0)]);
        Assert.Equal(0.125, double.Hypot(state.Positions[1].X - state.Positions[0].X, 0));
        DiagnosticSampler sampler = new();
        DiagnosticSample sample = sampler.Sample(state.Observe(), 0.125);
        Assert.Equal(Diagnostics.Sample(state.Observe(), 0.125), sample);
        Assert.Equal(1, sample.ClusterCount);
        Assert.True(sampler.LastExecution.UsedSpatialIndex);
    }

    [Theory]
    [InlineData(0.0625)]
    [InlineData(0.125)]
    [InlineData(0.1)]
    public void ThresholdNeighboursAndConnectedPathsRetainInclusiveMeaning(double radius)
    {
        foreach (double separation in new[] { Math.BitDecrement(radius), radius, Math.BitIncrement(radius) })
        {
            ParticleSnapshot state = Snapshot([new(0, 0), new(separation, 0)]);
            DiagnosticSampler sampler = new();
            DiagnosticSample sample = sampler.Sample(state.Observe(), radius);
            Assert.Equal(Diagnostics.Sample(state.Observe(), radius), sample);
            Assert.Equal(separation <= radius ? 1 : 2, sample.ClusterCount);
        }

        ParticleSnapshot path = Snapshot([new(-radius, 0), new(0, 0), new(radius, 0), new(0, radius)]);
        Assert.Equal(1, new DiagnosticSampler().Sample(path.Observe(), radius).ClusterCount);
    }

    [Fact]
    public void DiagonalThresholdUsesHypotRatherThanForceSquaredAcceptance()
    {
        Vector2d point = new(0.03, 0.04);
        double radius = double.Hypot(point.X, point.Y);
        ParticleSnapshot state = Snapshot([default, point]);
        DiagnosticSampler sampler = new();
        foreach (double threshold in new[] { Math.BitDecrement(radius), radius, Math.BitIncrement(radius) })
        {
            Assert.Equal(Diagnostics.Sample(state.Observe(), threshold), sampler.Sample(state.Observe(), threshold));
        }
    }

    [Fact]
    public void PositiveFiniteRadiusDomainIncludesUnderflowOverflowAndLargestRadius()
    {
        (double Radius, Vector2d[] Positions, DiagnosticFallbackReason Expected)[] cases =
        [
            (double.Epsilon, [default, new(double.Epsilon, 0), new(4 * double.Epsilon, 0)], DiagnosticFallbackReason.None),
            (1e-200, [default, new(0.5e-200, 0), new(3e-200, 0)], DiagnosticFallbackReason.None),
            (1e200, [default, new(0.5e200, 0), new(3e200, 0)], DiagnosticFallbackReason.None),
            (double.MaxValue, [new(-double.MaxValue, 0), default, new(double.MaxValue, 0)], DiagnosticFallbackReason.CellWidthRange),
            (0.1, [new(1e300, 1e300), new(1e300, 1e300), new(-1e300, -1e300)], DiagnosticFallbackReason.CoordinateRange),
            (double.Epsilon, [new(-1, 0), default, new(1, 0)], DiagnosticFallbackReason.CoordinateRange)
        ];
        DiagnosticSampler sampler = new();
        foreach ((double radius, Vector2d[] positions, DiagnosticFallbackReason expected) in cases)
        {
            ParticleSnapshot state = Snapshot(positions);
            Assert.Equal(Diagnostics.Sample(state.Observe(), radius), sampler.Sample(state.Observe(), radius));
            Assert.Equal(expected, sampler.LastExecution.FallbackReason);
            Assert.Equal(expected == DiagnosticFallbackReason.None, sampler.LastExecution.UsedSpatialIndex);
            if (expected != DiagnosticFallbackReason.None)
            {
                Assert.Equal(0, sampler.LastExecution.OccupiedCells);
            }
        }
    }

    [Fact]
    public void CompleteCoincidenceCountSurvivesEarlyConnectivityCompletion()
    {
        Vector2d[] positions = Enumerable.Range(0, 100).Select(i => new Vector2d(i % 4 * 0.01, 0)).ToArray();
        positions[0] = new(-0d, 0);
        ParticleSnapshot state = Snapshot(positions);
        DiagnosticSampler sampler = new();
        DiagnosticSample sample = sampler.Sample(state.Observe(), 0.1);
        Assert.Equal(Diagnostics.Sample(state.Observe(), 0.1), sample);
        Assert.Equal(4 * (25L * 24 / 2), sample.OverlapPairs);
        Assert.Equal(1, sample.ClusterCount);
        Assert.Equal(100, sample.LargestCluster);
        Assert.Equal(3, sampler.LastExecution.DistanceEvaluations);
    }

    [Fact]
    public void RepeatedSamplesChangingOccupancyPopulationAndRadiusDoNotReuseStaleState()
    {
        ParticleSnapshot dense = Snapshot(Enumerable.Repeat(new Vector2d(0.5, 0.5), 129).ToArray());
        ParticleSnapshot spread = Snapshot(Enumerable.Range(0, 129).Select(i => new Vector2d(i * 0.5, i % 2)).ToArray());
        ParticleSnapshot small = Snapshot([new(-0.125, 0), default, new(0.125, 0)]);
        ParticleSnapshot empty = Snapshot([]);
        DiagnosticSampler sampler = new();
        foreach (ParticleSnapshot state in new[] { dense, spread, empty, small, dense, spread, small, empty })
        {
            foreach (double radius in new[] { 0.1, 0.125, 1e-200, double.MaxValue })
            {
                Assert.Equal(Diagnostics.Sample(state.Observe(), radius), sampler.Sample(state.Observe(), radius));
                Assert.Equal(129, sampler.LastExecution.ParticleCapacity);
            }
        }
    }

    [Fact]
    public void FailurePreservesLastExecutionAndSamplerCanRecover()
    {
        DiagnosticSampler sampler = new();
        ParticleSnapshot valid = Snapshot([default, new(0.125, 0)]);
        _ = sampler.Sample(valid.Observe(), 0.125);
        DiagnosticExecution before = sampler.LastExecution;
        ParticleSnapshot speedOverflow = Snapshot([default], [new(1.3e308, 1.3e308)]);
        Assert.Throws<InvalidOperationException>(() => sampler.Sample(speedOverflow.Observe(), 0.125));
        Assert.Equal(before, sampler.LastExecution);
        Assert.Equal(Diagnostics.Sample(valid.Observe(), 0.125), sampler.Sample(valid.Observe(), 0.125));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidRadiusDoesNotPublishMetadata(double radius)
    {
        DiagnosticSampler sampler = new();
        ParticleSnapshot state = Snapshot([default]);
        _ = sampler.Sample(state.Observe(), 0.1);
        DiagnosticExecution before = sampler.LastExecution;
        Assert.Throws<ArgumentOutOfRangeException>(() => sampler.Sample(state.Observe(), radius));
        Assert.Equal(before, sampler.LastExecution);
    }

    [Fact]
    public void NonFiniteInputIsRejectedLikeReferenceWithoutPublishingMetadata()
    {
        DiagnosticSampler sampler = new();
        Vector2d[] positions = [new(double.PositiveInfinity, 0)];
        Assert.Throws<InvalidOperationException>(() => sampler.Sample(new ParticleStateView([0], positions, [default], [0], 0, 0), 0.1));
        Assert.Equal(default, sampler.LastExecution);
        Assert.Throws<InvalidOperationException>(() => Diagnostics.Sample(new ParticleStateView([0], positions, [default], [0], 0, 0), 0.1));
    }

    [Fact]
    public void SamplesRebuildAfterCompletedIntegrationAndDoNotAlterTrajectory()
    {
        ModelParameters parameters = new() { ForceScale = 0, WallStrength = 0, DampingHalfLife = null };
        ParticleSnapshot state = Snapshot([new(0, 0), new(0.3, 0)], [new(0.1, 0), new(-0.1, 0)]);
        Simulation measured = new(state, parameters, new InteractionMatrix(1, [0]), 1, ForceBackend.UniformGrid);
        Simulation control = new(state, parameters, new InteractionMatrix(1, [0]), 1, ForceBackend.UniformGrid);
        DiagnosticSampler sampler = new();
        Assert.Equal(2, sampler.Sample(measured.Observe(), 0.15).ClusterCount);
        measured.Step();
        control.Step();
        DiagnosticSample sample = sampler.Sample(measured.Observe(), 0.15);
        Assert.Equal(1, sample.ClusterCount);
        Assert.Equal(1, sample.Step);
        Assert.Equal(1, sample.Time);
        Assert.Equal(Diagnostics.Sample(measured.Observe(), 0.15), sample);
        for (int i = 0; i < 20; i++)
        {
            measured.Step();
            control.Step();
            Assert.Equal(Diagnostics.Sample(measured.Observe(), 0.15), sampler.Sample(measured.Observe(), 0.15));
        }

        Assert.Equal(control.Observe().Positions.ToArray(), measured.Observe().Positions.ToArray());
        Assert.Equal(control.Observe().Velocities.ToArray(), measured.Observe().Velocities.ToArray());
    }

    [Fact]
    public void WarmSamplesAllocateOnlyTheFixedResultEvenAfterOccupancyGrowthAndFallback()
    {
        ParticleSnapshot dense = Snapshot(Enumerable.Repeat(new Vector2d(0.5, 0.5), 256).ToArray());
        ParticleSnapshot spread = Snapshot(Enumerable.Range(0, 256).Select(i => new Vector2d(i * 0.5, i % 2)).ToArray());
        ParticleSnapshot empty = Snapshot([]);
        DiagnosticSampler sampler = new();
        for (int i = 0; i < 32; i++)
        {
            _ = sampler.Sample(dense.Observe(), 0.1);
            _ = sampler.Sample(spread.Observe(), 0.1);
            _ = sampler.Sample(spread.Observe(), double.Epsilon);
        }

        long emptyBytes = SampleAllocation(sampler, empty, 0.1);
        long denseBytes = SampleAllocation(sampler, dense, 0.1);
        long spreadBytes = SampleAllocation(sampler, spread, 0.1);
        long fallbackBytes = SampleAllocation(sampler, spread, double.Epsilon);
        Assert.InRange(emptyBytes / 32, 1, 128);
        Assert.Equal(emptyBytes, denseBytes);
        Assert.Equal(emptyBytes, spreadBytes);
        Assert.Equal(emptyBytes, fallbackBytes);
        Assert.Equal(256, sampler.LastExecution.ParticleCapacity);
    }

    private static long SampleAllocation(DiagnosticSampler sampler, ParticleSnapshot state, double radius)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 32; i++)
        {
            GC.KeepAlive(sampler.Sample(state.Observe(), radius));
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static ParticleSnapshot Snapshot(Vector2d[] positions, Vector2d[]? velocities = null, double time = 0, long step = 0) =>
        new(Enumerable.Range(0, positions.Length).Select(i => (long)i).ToArray(), positions,
            velocities ?? new Vector2d[positions.Length], new int[positions.Length], time, step);
}
