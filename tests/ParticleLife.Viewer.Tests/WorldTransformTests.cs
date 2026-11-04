using ParticleLife.Core.State;
using ParticleLife.Viewer;

namespace ParticleLife.Viewer.Tests;

public sealed class WorldTransformTests
{
    [Theory]
    [InlineData(800, 400, 400, 210, 20)]
    [InlineData(400, 800, 400, 10, 220)]
    [InlineData(500, 500, 500, 10, 20)]
    public void SquareWorldRemainsSquareAndCentered(double width, double height, double scale, double x, double y)
    {
        WorldTransform transform = new(1, 1, 10, 20, width, height);
        Assert.Equal(scale, transform.Scale);
        Assert.Equal(scale, transform.Width);
        Assert.Equal(scale, transform.Height);
        Assert.Equal(new Vector2d(x, y), transform.ToScreen(default));
        Assert.Equal(new Vector2d(x + scale, y + scale), transform.ToScreen(new Vector2d(1, 1)));
    }

    [Theory]
    [InlineData(1200, 300)]
    [InlineData(300, 1200)]
    [InlineData(700, 500)]
    public void RectangularWorldUsesEqualScaleForBothDirections(double width, double height)
    {
        WorldTransform transform = new(2, 1, 17, 29, width, height);
        Vector2d center = transform.ToScreen(new Vector2d(1, 0.5));
        Vector2d horizontal = transform.ToScreen(new Vector2d(1.25, 0.5));
        Vector2d vertical = transform.ToScreen(new Vector2d(1, 0.75));
        Assert.Equal(horizontal.X - center.X, vertical.Y - center.Y);
        Assert.Equal(17 + width / 2, center.X);
        Assert.Equal(29 + height / 2, center.Y);
        Assert.Equal(2 * transform.Height, transform.Width);
    }

    [Fact]
    public void OffWorldCoordinatesAreMappedWithoutClamping()
    {
        WorldTransform transform = new(1, 1, 0, 0, 500, 500);
        Assert.Equal(new Vector2d(-50, 600), transform.ToScreen(new Vector2d(-0.1, 1.2)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidDimensionsAreRejected(double dimension)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldTransform(dimension, 1, 0, 0, 500, 500));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldTransform(1, 1, 0, 0, dimension, 500));
    }
}
