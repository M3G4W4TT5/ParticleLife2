using ParticleLife.Core.State;

namespace ParticleLife.Core.Models;

/// <summary>Implements rectangular wrapping, minimum-image separation and specular reflection.</summary>
/// <remarks>Display particle size has no role in these rules. Wrapping maps to [0,L); reflection
/// maps to [0,L] and reverses each crossed normal velocity independently. Multiple crossings in
/// one step use a modulo triangular wave. These operations allocate no managed storage.</remarks>
public static class WorldBoundary
{
    /// <summary>Wraps a finite coordinate into a positive finite extent.</summary>
    /// <param name="coordinate">Finite coordinate in reduced length units.</param>
    /// <param name="extent">Positive finite extent in the same units.</param>
    /// <returns>A coordinate in [0, extent), including for negative and multi-world inputs.</returns>
    public static double Wrap(double coordinate, double extent)
    {
        ModelParameters.RequirePositiveFinite(extent, nameof(extent));
        if (!double.IsFinite(coordinate))
        {
            throw new ArgumentOutOfRangeException(nameof(coordinate));
        }
        return WrapValidated(coordinate, extent);
    }

    /// <summary>Computes the directed source-minus-receiver separation under the selected world rule.</summary>
    /// <param name="receiver">Finite completed receiver position.</param>
    /// <param name="source">Finite completed source position.</param>
    /// <param name="parameters">Validated world configuration.</param>
    /// <returns>The ordinary displacement, or one minimum-image displacement in periodic space.</returns>
    /// <remarks>Exactly half-extent ties retain their directed sign. Inputs may be outside the world;
    /// each is wrapped before subtraction, avoiding overflow from subtracting distant finite images.</remarks>
    public static Vector2d Displacement(Vector2d receiver, Vector2d source, ModelParameters parameters) =>
        parameters.Boundary != BoundaryKind.Periodic ? source - receiver
            : new(MinimumImage(receiver.X, source.X, parameters.WorldWidth),
                MinimumImage(receiver.Y, source.Y, parameters.WorldHeight));

    /// <summary>Maps a completed candidate position and velocity using the selected explicit boundary.</summary>
    /// <param name="position">Finite position, replaced only for reflecting/periodic worlds.</param>
    /// <param name="velocity">Finite velocity, reflected with the position when appropriate.</param>
    /// <param name="parameters">Validated world configuration.</param>
    /// <remarks>Use after all forces were evaluated from the stable input. Live boundary changes must
    /// save the resulting mapped state as an explicit structural edit rather than silently rewriting it.</remarks>
    public static void Map(ref Vector2d position, ref Vector2d velocity, ModelParameters parameters)
    {
        if (parameters.Boundary == BoundaryKind.Periodic)
        {
            position = new(WrapValidated(position.X, parameters.WorldWidth), WrapValidated(position.Y, parameters.WorldHeight));
        }
        else if (parameters.Boundary == BoundaryKind.Reflecting)
        {
            (double x, double vx) = Reflect(position.X, velocity.X, parameters.WorldWidth);
            (double y, double vy) = Reflect(position.Y, velocity.Y, parameters.WorldHeight);
            position = new(x, y);
            velocity = new(vx, vy);
        }
    }

    internal static double WrapValidated(double coordinate, double extent)
    {
        if (coordinate >= 0 && coordinate < extent)
        {
            return coordinate;
        }
        double wrapped = coordinate % extent;
        if (wrapped < 0)
        {
            wrapped += extent;
        }
        // A negative value smaller than an ulp can round to extent when adding it.
        return wrapped >= extent ? 0 : wrapped;
    }

    private static double MinimumImage(double receiver, double source, double extent)
    {
        double difference = WrapValidated(source, extent) - WrapValidated(receiver, extent);
        double half = extent / 2;
        return difference > half ? difference - extent : difference < -half ? difference + extent : difference;
    }

    private static (double Position, double Velocity) Reflect(double position, double velocity, double extent)
    {
        double folded = WrapValidated(position, 2 * extent);
        if (folded == 0)
        {
            return (0, Math.Abs(velocity));
        }
        if (folded == extent)
        {
            return (extent, -Math.Abs(velocity));
        }
        return folded < extent ? (folded, velocity) : (2 * extent - folded, -velocity);
    }
}
