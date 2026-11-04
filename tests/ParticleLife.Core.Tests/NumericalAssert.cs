using ParticleLife.Core.State;
using Xunit;

namespace ParticleLife.Core.Tests;

internal static class NumericalAssert
{
    // Short, double-precision cases use reduced-unit quantities of order one.
    // 1e-12 absolute + 1e-12 relative allows accumulation/ordering roundoff while
    // remaining far below the changes caused by a wrong sign, duplicate pair or dt.
    internal const double AbsoluteTolerance = 1e-12;
    internal const double RelativeTolerance = 1e-12;

    internal static void Close(double expected, double actual,
        double absoluteTolerance = AbsoluteTolerance, double relativeTolerance = RelativeTolerance)
    {
        Assert.True(double.IsFinite(expected) && double.IsFinite(actual),
            $"Expected finite values; expected {expected:R}, actual {actual:R}.");
        double tolerance = absoluteTolerance + relativeTolerance * Math.Max(Math.Abs(expected), Math.Abs(actual));
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"Expected {expected:R}, actual {actual:R}; tolerance {tolerance:R}.");
    }

    internal static void Close(Vector2d expected, Vector2d actual)
    {
        Close(expected.X, actual.X);
        Close(expected.Y, actual.Y);
    }

    internal static void SameState(ParticleSnapshot expected, ParticleSnapshot actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected.Step, actual.Step);
        Close(expected.Time, actual.Time);
        for (int i = 0; i < expected.Count; i++)
        {
            int matchingIndex = actual.Ids.IndexOf(expected.Ids[i]);
            Assert.True(matchingIndex >= 0, $"Missing stable identity {expected.Ids[i]}.");
            Assert.Equal(expected.Species[i], actual.Species[matchingIndex]);
            Close(expected.Positions[i], actual.Positions[matchingIndex]);
            Close(expected.Velocities[i], actual.Velocities[matchingIndex]);
        }
    }
}
