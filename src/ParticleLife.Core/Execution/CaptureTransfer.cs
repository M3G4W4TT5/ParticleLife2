using ParticleLife.Core.State;

namespace ParticleLife.Core.Execution;

/// <summary>Converts owned state only when the requested precision policy explicitly permits loss.</summary>
public static class CaptureTransfer
{
    /// <summary>Restores exact stored values or deliberately rounds particle components to binary32.</summary>
    /// <param name="state">Actual independently owned state and exact host clock.</param>
    /// <param name="target">Destination particle precision.</param>
    /// <param name="allowPrecisionConversion">True explicitly permits binary64-to-binary32 rounding.</param>
    /// <returns>Owned immutable state with unchanged identities, species, time and step.</returns>
    /// <exception cref="InvalidOperationException">Exact restoration would lose precision.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A component cannot be represented finitely or a precision is unknown.</exception>
    /// <remarks>Binary32-to-binary64 widening is exact. Signed zero is retained. This operation never
    /// changes model configuration or asserts trajectory equivalence after a hardware switch.</remarks>
    public static ParticleSnapshot Convert(ParticleSnapshot state, StatePrecision target, bool allowPrecisionConversion = false)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!Enum.IsDefined(target))
        {
            throw new ArgumentOutOfRangeException(nameof(target));
        }
        if (target == StatePrecision.Float64 || IsExactFloat32(state))
        {
            return state;
        }
        if (!allowPrecisionConversion)
        {
            throw new InvalidOperationException("This capture cannot be restored exactly in float32. Explicitly choose precision conversion or restore on the CPU.");
        }
        Vector2d[] positions = new Vector2d[state.Count];
        Vector2d[] velocities = new Vector2d[state.Count];
        for (int index = 0; index < state.Count; index++)
        {
            positions[index] = Round(state.Positions[index]);
            velocities[index] = Round(state.Velocities[index]);
        }
        return new(state.Ids, positions, velocities, state.Species, state.Time, state.Step);
    }

    /// <summary>Tests whether every component is exactly representable as finite binary32.</summary>
    /// <param name="state">State whose numerical components are inspected without mutation.</param>
    /// <returns>True when widening its binary32 values would recover every stored component bit.</returns>
    public static bool IsExactFloat32(ParticleSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        for (int index = 0; index < state.Count; index++)
        {
            Vector2d position = state.Positions[index];
            Vector2d velocity = state.Velocities[index];
            if (!Exact(position.X) || !Exact(position.Y) || !Exact(velocity.X) || !Exact(velocity.Y))
            {
                return false;
            }
        }
        return true;
    }

    private static bool Exact(double value) => float.IsFinite((float)value)
        && BitConverter.DoubleToInt64Bits(value) == BitConverter.DoubleToInt64Bits((float)value);

    private static Vector2d Round(Vector2d value)
    {
        float x = (float)value.X, y = (float)value.Y;
        if (!float.IsFinite(x) || !float.IsFinite(y))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "The state exceeds the finite float32 range.");
        }
        return new(x, y);
    }
}
