namespace ParticleLife.Core.State;

/// <summary>A two-component, double-precision numerical value without graphics dependencies.</summary>
/// <param name="X">The first component, in the units of the represented quantity.</param>
/// <param name="Y">The second component, in the same units as <paramref name="X"/>.</param>
/// <remarks>Positions, velocities and forces share this representation, but not their units.</remarks>
public readonly record struct Vector2d(double X, double Y)
{
    /// <summary>Gets whether both components are finite.</summary>
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);

    /// <summary>Gets the Euclidean magnitude, in the components' units.</summary>
    /// <remarks>Scaling avoids intermediate overflow and underflow when the magnitude is representable.</remarks>
    public double Length
    {
        get
        {
            double largest = Math.Max(Math.Abs(X), Math.Abs(Y));
            if (largest == 0 || !double.IsFinite(largest))
            {
                return largest;
            }

            double x = X / largest;
            double y = Y / largest;
            return largest * Math.Sqrt(x * x + y * y);
        }
    }

    /// <summary>Adds two values with the same units component by component.</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns>The component-wise sum.</returns>
    public static Vector2d operator +(Vector2d left, Vector2d right) => new(left.X + right.X, left.Y + right.Y);

    /// <summary>Subtracts two values with the same units component by component.</summary>
    /// <param name="left">The value from which to subtract.</param>
    /// <param name="right">The value to subtract.</param>
    /// <returns>The component-wise difference.</returns>
    public static Vector2d operator -(Vector2d left, Vector2d right) => new(left.X - right.X, left.Y - right.Y);

    /// <summary>Multiplies both components by a scalar.</summary>
    /// <param name="value">The vector quantity.</param>
    /// <param name="scale">The scalar; its units multiply the vector's units.</param>
    /// <returns>The scaled value.</returns>
    public static Vector2d operator *(Vector2d value, double scale) => new(value.X * scale, value.Y * scale);

    /// <summary>Multiplies both components by a scalar.</summary>
    /// <param name="scale">The scalar; its units multiply the vector's units.</param>
    /// <param name="value">The vector quantity.</param>
    /// <returns>The scaled value.</returns>
    public static Vector2d operator *(double scale, Vector2d value) => value * scale;

    /// <summary>Divides both components by a scalar using IEEE 754 arithmetic.</summary>
    /// <param name="value">The vector quantity.</param>
    /// <param name="divisor">The divisor; zero may produce non-finite components.</param>
    /// <returns>The divided value.</returns>
    public static Vector2d operator /(Vector2d value, double divisor) => new(value.X / divisor, value.Y / divisor);
}
