using ParticleLife.Core.Models;
using ParticleLife.Core.State;
using Xunit;

namespace ParticleLife.Core.Tests;

public sealed class ModelTests
{
    [Theory]
    [InlineData(0, 0.8, 0)]
    [InlineData(0.15, 0.8, -0.5)]
    [InlineData(0.15, -0.8, -0.5)]
    [InlineData(0.3, 0.8, 0)]
    [InlineData(0.475, 0.8, 0.4)]
    [InlineData(0.65, 0.8, 0.8)]
    [InlineData(0.825, 0.8, 0.4)]
    [InlineData(0.65, -0.8, -0.8)]
    [InlineData(1, 0.8, 0)]
    [InlineData(2, 0.8, 0)]
    public void RadialCoefficientHasSpecifiedValues(double distanceRatio, double attraction, double expected)
    {
        NumericalAssert.Close(expected, ParticleLifeForce.RadialCoefficient(distanceRatio, attraction, 0.3));
    }

    [Fact]
    public void RadialCurveIsContinuousAtCoreTransitionAndCutoff()
    {
        const double offset = 1e-9;
        const double beta = 0.3;
        foreach (double attraction in new[] { -1.0, 0.0, 1.0 })
        {
            // The largest slope here is 1/beta. A 4*offset absolute bound
            // therefore checks the two limits without requiring exact arithmetic.
            NumericalAssert.Close(0, ParticleLifeForce.RadialCoefficient(beta - offset, attraction, beta), 4 * offset, 0);
            NumericalAssert.Close(0, ParticleLifeForce.RadialCoefficient(beta + offset, attraction, beta), 4 * offset, 0);
            NumericalAssert.Close(0, ParticleLifeForce.RadialCoefficient(1 - offset, attraction, beta), 4 * offset, 0);
            Assert.Equal(0, ParticleLifeForce.RadialCoefficient(1 + offset, attraction, beta));
        }
    }

    [Theory]
    [InlineData(-0.1, 0, 0.3)]
    [InlineData(double.NaN, 0, 0.3)]
    [InlineData(double.PositiveInfinity, 0, 0.3)]
    [InlineData(0.5, double.NaN, 0.3)]
    [InlineData(0.5, 1.01, 0.3)]
    [InlineData(0.5, -1.01, 0.3)]
    [InlineData(0.5, 0, 0)]
    [InlineData(0.5, 0, 1)]
    [InlineData(0.5, 0, double.NaN)]
    public void RadialCoefficientRejectsInvalidArguments(double q, double attraction, double beta)
    {
        Assert.ThrowsAny<ArgumentException>(() => ParticleLifeForce.RadialCoefficient(q, attraction, beta));
    }

    [Theory]
    [InlineData(0.5, 0.5, 0, 0)]
    [InlineData(0.05, 0.95, 0, 0)]
    [InlineData(0.025, 0.5, 0.72, 0)]
    [InlineData(0.975, 0.5, -0.72, 0)]
    [InlineData(0, 0, 1.44, 1.44)]
    [InlineData(1, 1, -1.44, -1.44)]
    [InlineData(-0.05, 1.05, 2.88, -2.88)]
    public void SoftWallIsLinearInwardAndContinuesOutsideWorld(double x, double y, double fx, double fy)
    {
        Vector2d actual = SoftWall.Force(new Vector2d(x, y), new ModelParameters());
        NumericalAssert.Close(new Vector2d(fx, fy), actual);
    }

    [Fact]
    public void RectangularWorldUsesEachAxisExtent()
    {
        ModelParameters parameters = new() { WorldWidth = 2, WorldHeight = 3 };
        NumericalAssert.Close(new Vector2d(-0.72, -0.72), SoftWall.Force(new Vector2d(1.975, 2.975), parameters));
    }

    [Fact]
    public void UpperWallRetainsItsBoundaryForceWhenLayerThresholdRoundsToExtent()
    {
        ModelParameters parameters = new() { WorldWidth = 1e20, WorldHeight = 1e20 };
        // At this scale, subtracting the layer width loses the entire threshold.
        // Distance from the edge is still exactly zero at the nominal boundary.
        Assert.Equal(parameters.WorldWidth, parameters.WorldWidth - parameters.WallWidth);
        Vector2d force = SoftWall.Force(new Vector2d(parameters.WorldWidth, parameters.WorldHeight), parameters);

        NumericalAssert.Close(new Vector2d(-parameters.WallStrength, -parameters.WallStrength), force);
    }

    [Fact]
    public void MatrixPreservesDirectedEntriesAndOwnsACopy()
    {
        double[] values = [0.1, 0.8, -0.6, 0.2];
        InteractionMatrix matrix = new(2, values);
        values[1] = -1;
        Assert.Equal(2, matrix.SpeciesCount);
        Assert.Equal(0.8, matrix[0, 1]);
        Assert.Equal(-0.6, matrix[1, 0]);
        Assert.Equal(new[] { 0.1, 0.8, -0.6, 0.2 }, matrix.Values.ToArray());
    }

    [Fact]
    public void MatrixRejectsInvalidShapeOrCoefficients()
    {
        Assert.ThrowsAny<ArgumentException>(() => new InteractionMatrix(0, []));
        Assert.ThrowsAny<ArgumentException>(() => new InteractionMatrix(2, [0, 1, 0]));
        Assert.ThrowsAny<ArgumentException>(() => new InteractionMatrix(1, [1.01]));
        Assert.ThrowsAny<ArgumentException>(() => new InteractionMatrix(1, [double.NaN]));
        Assert.ThrowsAny<ArgumentException>(() => new InteractionMatrix(1, [double.NegativeInfinity]));
    }

    [Fact]
    public void ConfigurationRejectsInvalidOrUnrepresentableParameters()
    {
        ModelParameters defaults = new();
        ModelParameters[] invalid =
        [
            defaults with { WorldWidth = 0 },
            defaults with { WorldHeight = double.PositiveInfinity },
            defaults with { Cutoff = -1 },
            defaults with { Cutoff = double.Epsilon },
            defaults with { Cutoff = double.MaxValue },
            defaults with { ForceScale = -1 },
            defaults with { ForceScale = double.NaN },
            defaults with { RepulsionFraction = 0 },
            defaults with { RepulsionFraction = 1 },
            defaults with { DampingHalfLife = 0 },
            defaults with { DampingHalfLife = double.PositiveInfinity },
            defaults with { WallWidth = 0 },
            defaults with { WallWidth = 0.6 },
            defaults with { WallStrength = -0.1 },
            defaults with { WallStrength = double.NaN }
        ];

        foreach (ModelParameters parameters in invalid)
        {
            Assert.ThrowsAny<ArgumentException>(parameters.Validate);
        }

        (defaults with { DampingHalfLife = null, ForceScale = 0, WallStrength = 0 }).Validate();
    }
}
