namespace ParticleLife.Core.Models;

/// <summary>Parameters of the phenomenological, unit-mass two-dimensional Particle Life model.</summary>
/// <remarks>
/// <para>Reference length L, reference time T and reference particle mass M define reduced units;
/// all particles have mass 1 M. Positions use L, velocities L/T, pair and wall forces M L/T^2.
/// The defaults use a unit square, without assigning metres or seconds. The species matrix and
/// radial coefficient are dimensionless; neither is a potential energy.</para>
/// <para>Pair forces may be nonreciprocal. Soft walls are external forces and damping dissipates
/// motion, so the general model does not conserve particle momentum or mechanical energy.
/// This is not a validated model of molecular dynamics or electromagnetism.</para>
/// <para>Values are immutable after initialization. Call <see cref="Validate"/> at a configuration
/// boundary; simulation and evaluator construction do so automatically.</para>
/// </remarks>
public sealed record ModelParameters
{
    /// <summary>Gets the explicit world boundary rule; legacy files use soft walls.</summary>
    public BoundaryKind Boundary { get; init; } = BoundaryKind.SoftWall;

    /// <summary>Gets the positive finite width of the nominal world in L.</summary>
    public double WorldWidth { get; init; } = 1;

    /// <summary>Gets the positive finite height of the nominal world in L.</summary>
    public double WorldHeight { get; init; } = 1;

    /// <summary>Gets the positive interaction cutoff R in L; its square must be positive and finite.</summary>
    public double Cutoff { get; init; } = 0.1;

    /// <summary>Gets the finite nonnegative pair-force scale F0 in M L/T^2.</summary>
    public double ForceScale { get; init; } = 1;

    /// <summary>Gets the dimensionless repulsive-core fraction beta, strictly between zero and one.</summary>
    public double RepulsionFraction { get; init; } = 0.3;

    /// <summary>Gets the positive finite velocity half-life in T, or null to disable damping.</summary>
    public double? DampingHalfLife { get; init; } = 0.025;

    /// <summary>Gets the positive finite wall-layer width in L, at most half either world dimension.</summary>
    public double WallWidth { get; init; } = 0.05;

    /// <summary>Gets the finite nonnegative inward force magnitude at a nominal world boundary, in M L/T^2.</summary>
    /// <remarks>Zero disables the soft-wall force. Outside the world the linear force continues without saturation.</remarks>
    public double WallStrength { get; init; } = 1.44;

    /// <summary>Checks the supported ranges and numerical prerequisites without allocating on success.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is non-finite or outside its documented range.</exception>
    public void Validate()
    {
        if (!Enum.IsDefined(Boundary))
        {
            throw new ArgumentOutOfRangeException(nameof(Boundary));
        }
        RequirePositiveFinite(WorldWidth, nameof(WorldWidth));
        RequirePositiveFinite(WorldHeight, nameof(WorldHeight));
        if (Boundary == BoundaryKind.Reflecting && (!double.IsFinite(2 * WorldWidth) || !double.IsFinite(2 * WorldHeight)))
        {
            throw new ArgumentOutOfRangeException(nameof(WorldWidth), "Reflecting extents must have finite doubled lengths.");
        }
        RequirePositiveFinite(Cutoff, nameof(Cutoff));
        RequirePositiveFinite(Cutoff * Cutoff, nameof(Cutoff));
        RequireNonnegativeFinite(ForceScale, nameof(ForceScale));
        if (!double.IsFinite(RepulsionFraction) || RepulsionFraction <= 0 || RepulsionFraction >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(RepulsionFraction), "Repulsion fraction must be strictly between zero and one.");
        }

        if (DampingHalfLife is double halfLife)
        {
            RequirePositiveFinite(halfLife, nameof(DampingHalfLife));
        }

        RequirePositiveFinite(WallWidth, nameof(WallWidth));
        if (WallWidth > WorldWidth / 2 || WallWidth > WorldHeight / 2)
        {
            throw new ArgumentOutOfRangeException(nameof(WallWidth), "Wall layers must not overlap: width must be at most half either world dimension.");
        }

        RequireNonnegativeFinite(WallStrength, nameof(WallStrength));
    }

    internal static void RequirePositiveFinite(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, "Value must be positive and finite.");
        }
    }

    private static void RequireNonnegativeFinite(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(name, "Value must be nonnegative and finite.");
        }
    }
}
