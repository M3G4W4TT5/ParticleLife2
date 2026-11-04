using ParticleLife.Core.State;

namespace ParticleLife.Core.Models;

/// <summary>A linear inward soft-wall force for a rectangular nominal world.</summary>
public static class SoftWall
{
    /// <summary>Evaluates the external confining force without applying a timestep.</summary>
    /// <param name="position">Finite position in reduced length units, inside or outside the nominal world.</param>
    /// <param name="parameters">Validated wall and world configuration; validation is repeated at this public boundary.</param>
    /// <returns>A force in reduced M L/T^2 units, not a velocity increment.</returns>
    /// <remarks>
    /// For a coordinate x, world extent L, width w and strength S, F = S(1 - x/w) when x &lt; w;
    /// F = -S(1 - (L-x)/w) when L-x &lt; w; otherwise F = 0. Apply independently to x and y.
    /// Testing distance to the upper edge avoids losing a narrow wall layer when L-w rounds to L.
    /// The response continues linearly outside the world. It neither clips nor reflects positions.
    /// Layers do not overlap under the parameter contract. S = 0 disables the force.
    /// The default S = 1.44 yields a 0.01 velocity kick at the boundary for dt = 1/144 and unit mass.
    /// Finite inputs do not guarantee representable intermediate arithmetic: an extreme coordinate
    /// divided by a narrow layer can overflow before multiplication by the strength. Such cases
    /// fail explicitly. Evaluation allocates no memory when all intermediate values remain finite.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Parameters are null.</exception>
    /// <exception cref="ArgumentException">The position is non-finite.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is invalid.</exception>
    /// <exception cref="ArithmeticException">An intermediate wall calculation or the resulting force is non-finite.</exception>
    public static Vector2d Force(Vector2d position, ModelParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.Validate();
        if (!position.IsFinite)
        {
            throw new ArgumentException("Wall position must be finite.", nameof(position));
        }

        Vector2d force = EvaluateValidated(position, parameters);
        if (!force.IsFinite)
        {
            throw new ArithmeticException("Soft-wall calculation is non-finite; reduce the position magnitude or strength, or increase the wall width.");
        }

        return force;
    }

    /// <summary>Evaluates the separable wall response after configuration and state validation.</summary>
    /// <remarks>The caller checks the accumulated force for numerical overflow before publishing state.</remarks>
    internal static Vector2d EvaluateValidated(Vector2d position, ModelParameters parameters)
    {
        if (parameters.WallStrength == 0)
        {
            return default;
        }

        return new(Component(position.X, parameters.WorldWidth, parameters.WallWidth, parameters.WallStrength),
            Component(position.Y, parameters.WorldHeight, parameters.WallWidth, parameters.WallStrength));
    }

    /// <summary>Extends the linear inward boundary-layer force beyond the nominal edge without clipping.</summary>
    private static double Component(double coordinate, double extent, double width, double strength)
    {
        if (coordinate < width)
        {
            return strength * (1 - coordinate / width);
        }

        double upperDistance = extent - coordinate;
        if (upperDistance < width)
        {
            return -strength * (1 - upperDistance / width);
        }

        return 0;
    }
}
