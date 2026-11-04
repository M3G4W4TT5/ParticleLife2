using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using Xunit;

namespace ParticleLife.Core.Tests;

public sealed class ForceEvaluationTests
{
    [Fact]
    public void DirectEvaluationVisitsEachDirectedPairOnceAndExcludesSelf()
    {
        ParticleSnapshot input = new([1, 2, 3, 4],
            [new(-0.02, 0), new(0.045, 0), new(0.08, 0.04), new(0.08, 0.04)],
            [default, default, default, default], [0, 0, 0, 0]);
        ModelParameters parameters = new() { WallStrength = 0, DampingHalfLife = null };
        DirectForceEvaluator evaluator = new(parameters, new InteractionMatrix(1, [0.8]));
        Vector2d[] forces = new Vector2d[input.Count];

        ForceEvaluationStatistics statistics = evaluator.Evaluate(input.Observe(), forces);

        double diagonalDistance = Math.Sqrt(0.035 * 0.035 + 0.04 * 0.04);
        double diagonalCoefficient = 0.8 * (2 * diagonalDistance / 0.1 - 0.6) / 0.7;
        double fx = diagonalCoefficient * 0.035 / diagonalDistance;
        double fy = diagonalCoefficient * 0.04 / diagonalDistance;
        NumericalAssert.Close(new Vector2d(0.8, 0), forces[0]);
        NumericalAssert.Close(new Vector2d(-0.8 + 2 * fx, 2 * fy), forces[1]);
        NumericalAssert.Close(new Vector2d(-fx, -fy), forces[2]);
        NumericalAssert.Close(new Vector2d(-fx, -fy), forces[3]);
        Assert.Equal(12, statistics.CandidatePairs);
        Assert.Equal(6, statistics.AcceptedPairs);
        Assert.Equal(1, statistics.OverlapPairs);
    }

    [Fact]
    public void EvaluationOverwritesReusableForceBufferAndDoesNotMutateInput()
    {
        ParticleSnapshot initial = new([1, 2], [new(0.2, 0.5), new(0.265, 0.5)], [default, default], [0, 0]);
        DirectForceEvaluator evaluator = new(new ModelParameters(), new InteractionMatrix(1, [0.8]));
        Vector2d[] forces = [new(double.NaN, double.NaN), new(1e10, -1e10)];
        evaluator.Evaluate(initial.Observe(), forces);
        Vector2d[] first = (Vector2d[])forces.Clone();
        evaluator.Evaluate(initial.Observe(), forces);

        Assert.Equal(first, forces);
        Assert.Equal(new Vector2d(0.2, 0.5), initial.Positions[0]);
        Assert.Equal(new Vector2d(0.265, 0.5), initial.Positions[1]);
        Assert.Equal(default, initial.Velocities[0]);
        Assert.Equal(default, initial.Velocities[1]);
    }

    [Fact]
    public void ForceScaleMultipliesPairContributionWithoutChangingDimensionlessCoefficient()
    {
        ParticleSnapshot initial = new([1, 2], [new(0.2, 0.5), new(0.265, 0.5)], [default, default], [0, 0]);
        DirectForceEvaluator evaluator = new(new ModelParameters { ForceScale = 3, WallStrength = 0 }, new InteractionMatrix(1, [0.8]));
        Vector2d[] forces = new Vector2d[2];
        evaluator.Evaluate(initial.Observe(), forces);

        NumericalAssert.Close(new Vector2d(2.4, 0), forces[0]);
        NumericalAssert.Close(new Vector2d(-2.4, 0), forces[1]);
        NumericalAssert.Close(0.8, ParticleLifeForce.RadialCoefficient(0.65, 0.8, 0.3));
    }

    [Fact]
    public void PairAndWallTermsAddInForceUnitsBeforeIntegration()
    {
        ParticleSnapshot initial = new([1, 2], [new(0.025, 0.5), new(0.09, 0.5)], [default, default], [0, 0]);
        DirectForceEvaluator evaluator = new(new ModelParameters(), new InteractionMatrix(1, [0.8]));
        Vector2d[] forces = new Vector2d[2];
        evaluator.Evaluate(initial.Observe(), forces);

        NumericalAssert.Close(new Vector2d(0.72 + 0.8, 0), forces[0]);
        NumericalAssert.Close(new Vector2d(-0.8, 0), forces[1]);
    }

    [Fact]
    public void IntegratorDampsExistingVelocityBeforeAddingUndampedForceKick()
    {
        ParticleSnapshot initial = new([1], [new(0.5, 0.5)], [new(0.4, -0.2)], [0]);
        Vector2d[] forces = [new(0.7, -0.3)];
        Vector2d[] positions = new Vector2d[1];
        Vector2d[] velocities = new Vector2d[1];
        SemiImplicitEuler.Integrate(initial.Observe(), forces, 0.1, 0.5, positions, velocities);

        NumericalAssert.Close(new Vector2d(0.27, -0.13), velocities[0]);
        NumericalAssert.Close(new Vector2d(0.527, 0.487), positions[0]);
        Assert.Equal(new Vector2d(0.5, 0.5), initial.Positions[0]);
        Assert.Equal(new Vector2d(0.4, -0.2), initial.Velocities[0]);
    }

    [Fact]
    public void DisabledDampingUsesUnitMultiplier()
    {
        Assert.Equal(1, SemiImplicitEuler.DampingFactor(0.01, null));
        NumericalAssert.Close(0.5, SemiImplicitEuler.DampingFactor(0.05, 0.05));
    }

    [Fact]
    public void NumericalStagesRejectWrongBufferSizesAndOverlappingOutputs()
    {
        ParticleSnapshot initial = new([1], [new(0.5, 0.5)], [default], [0]);
        DirectForceEvaluator evaluator = new(new ModelParameters(), new InteractionMatrix(1, [0]));
        Vector2d[] buffer = new Vector2d[1];
        Vector2d[] independent = new Vector2d[1];

        Assert.ThrowsAny<ArgumentException>(() => evaluator.Evaluate(initial.Observe(), []));
        Assert.ThrowsAny<ArgumentException>(() => SemiImplicitEuler.Integrate(initial.Observe(), [], 0.01, 1, buffer, independent));
        Assert.ThrowsAny<ArgumentException>(() => SemiImplicitEuler.Integrate(initial.Observe(), independent, 0.01, 1, buffer, buffer));
        Assert.ThrowsAny<ArgumentException>(() => SemiImplicitEuler.Integrate(initial.Observe(), buffer, 0.01, 1, buffer, independent));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void IntegratorRejectsInvalidDampingFactor(double invalid)
    {
        ParticleSnapshot initial = new([1], [new(0.5, 0.5)], [default], [0]);
        Assert.ThrowsAny<ArgumentException>(() => SemiImplicitEuler.Integrate(initial.Observe(), new Vector2d[1],
            0.01, invalid, new Vector2d[1], new Vector2d[1]));
    }
}
