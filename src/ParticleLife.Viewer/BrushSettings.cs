using ParticleLife.Core.State;
using ParticleLife.Core.Models;

namespace ParticleLife.Viewer;

/// <summary>Selects a direct completed-boundary particle edit.</summary>
public enum BrushKind
{
    /// <summary>Drags the identities grabbed at the first sample.</summary>
    Move,
    /// <summary>Adds actual particles with fresh identities.</summary>
    Add,
    /// <summary>Changes species of particles under the brush.</summary>
    PaintSpecies,
    /// <summary>Removes particles under the brush.</summary>
    Erase,
    /// <summary>Applies a velocity impulse towards the brush centre.</summary>
    Attract,
    /// <summary>Applies a velocity impulse away from the brush centre.</summary>
    Repel,
    /// <summary>Applies a velocity impulse along the selected angle.</summary>
    DirectionalForce
}

/// <summary>Selects the world-space brush footprint.</summary>
public enum BrushShape
{
    /// <summary>A circular disc.</summary>
    Circle,
    /// <summary>An axis-aligned square with half-width equal to radius.</summary>
    Square
}

/// <summary>Immutable direct-tool settings captured once at stroke start.</summary>
/// <remarks>For force tools intensity is acceleration in L/T²; each actually applied sample changes
/// velocity by intensity times the current fixed dt. Pointer wall-time is never used by replay.
/// A species filter of -1 affects all species. Palette and display radius do not set brush geometry.</remarks>
public sealed record BrushSettings
{
    /// <summary>Gets the explicit live action.</summary>
    public BrushKind Kind { get; init; }
    /// <summary>Gets the footprint shape.</summary>
    public BrushShape Shape { get; init; }
    /// <summary>Gets positive radius/half-width in reduced world units.</summary>
    public double Radius { get; init; } = 0.06;
    /// <summary>Gets finite nonnegative force-tool acceleration in reduced units.</summary>
    public double Intensity { get; init; } = 2;
    /// <summary>Gets the directional-tool angle in radians.</summary>
    public double DirectionRadians { get; init; }
    /// <summary>Gets the number added by each actual applied add sample, from 1 through 4096.</summary>
    public int AddCount { get; init; } = 8;
    /// <summary>Gets the species assigned by add or paint tools.</summary>
    public int Species { get; init; }
    /// <summary>Gets the affected species, or -1 for all.</summary>
    public int SpeciesFilter { get; init; } = -1;
    /// <summary>Validates settings against current species dimensions.</summary>
    /// <param name="speciesCount">Positive active species count.</param>
    public void Validate(int speciesCount)
    {
        if (!Enum.IsDefined(Kind) || !Enum.IsDefined(Shape) || !double.IsFinite(Radius) || Radius <= 0
            || !double.IsFinite(Intensity) || Intensity < 0 || !double.IsFinite(DirectionRadians)
            || AddCount < 1 || AddCount > 4096 || Species < 0 || Species >= speciesCount
            || SpeciesFilter < -1 || SpeciesFilter >= speciesCount)
        {
            throw new ArgumentOutOfRangeException(nameof(Radius), "Brush settings must be finite, with valid species, positive radius and 1..4096 additions per sample.");
        }
    }
    /// <summary>Tests the footprint using normalized differences to avoid squaring large world lengths.</summary>
    /// <param name="position">Particle world position.</param><param name="centre">Brush centre.</param>
    /// <returns>Whether the point lies inside or on the footprint.</returns>
    public bool Contains(Vector2d position, Vector2d centre)
    {
        return ContainsDisplacement(position - centre);
    }

    /// <summary>Tests the footprint using the world's directed displacement, including periodic seams.</summary>
    /// <param name="position">Particle position.</param><param name="centre">Brush centre.</param>
    /// <param name="parameters">Validated explicit world geometry.</param><returns>Whether the point lies in the footprint.</returns>
    public bool Contains(Vector2d position, Vector2d centre, ModelParameters parameters) =>
        ContainsDisplacement(WorldBoundary.Displacement(centre, position, parameters));

    private bool ContainsDisplacement(Vector2d displacement)
    {
        double x = displacement.X / Radius, y = displacement.Y / Radius;
        return Shape == BrushShape.Square ? Math.Abs(x) <= 1 && Math.Abs(y) <= 1 : x * x + y * y <= 1;
    }
}
