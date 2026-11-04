namespace ParticleLife.Core.State;

/// <summary>An independently owned immutable copy of a completed or initial particle state.</summary>
/// <remarks>
/// Construction copies all input arrays. Read-only observations remain valid for this object's
/// lifetime and are safe for concurrent readers. Copying and validation allocate; snapshot
/// capture belongs outside the simulation hot loop. Empty states are valid.
/// </remarks>
public sealed class ParticleSnapshot
{
    private readonly long[] ids;
    private readonly Vector2d[] positions;
    private readonly Vector2d[] velocities;
    private readonly int[] species;

    /// <summary>Copies and validates an initial state or completed checkpoint.</summary>
    /// <param name="ids">Unique stable identities, one per particle.</param>
    /// <param name="positions">Finite positions in reduced length units, including off-world positions.</param>
    /// <param name="velocities">Finite velocities in reduced length units per reduced time unit.</param>
    /// <param name="species">Nonnegative species identifiers; the model validates the upper bound.</param>
    /// <param name="time">Finite nonnegative simulation time in reduced time units.</param>
    /// <param name="step">Nonnegative count of completed steps.</param>
    /// <exception cref="ArgumentException">Lengths differ, identities repeat or vectors are non-finite.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Time, step or a species identifier is invalid.</exception>
    public ParticleSnapshot(ReadOnlySpan<long> ids, ReadOnlySpan<Vector2d> positions,
        ReadOnlySpan<Vector2d> velocities, ReadOnlySpan<int> species, double time = 0, long step = 0)
    {
        if (ids.Length != positions.Length || velocities.Length != positions.Length || species.Length != positions.Length)
        {
            throw new ArgumentException("Identity, position, velocity and species counts must match.");
        }

        if (!double.IsFinite(time) || time < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(time), "Simulation time must be finite and nonnegative.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(step);
        HashSet<long> uniqueIds = new(ids.Length);
        for (int i = 0; i < positions.Length; i++)
        {
            if (!uniqueIds.Add(ids[i]))
            {
                throw new ArgumentException($"Particle identity {ids[i]} occurs more than once.", nameof(ids));
            }

            if (!positions[i].IsFinite || !velocities[i].IsFinite)
            {
                throw new ArgumentException($"Particle {ids[i]} has a non-finite position or velocity.");
            }

            if (species[i] < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(species), $"Particle {ids[i]} has a negative species identifier.");
            }
        }

        this.ids = ids.ToArray();
        this.positions = positions.ToArray();
        this.velocities = velocities.ToArray();
        this.species = species.ToArray();
        Time = time;
        Step = step;
    }

    /// <summary>Gets stable identities through an immutable view.</summary>
    public ReadOnlySpan<long> Ids => ids;

    /// <summary>Gets positions in reduced length units through an immutable view.</summary>
    public ReadOnlySpan<Vector2d> Positions => positions;

    /// <summary>Gets velocities in reduced length units per time unit through an immutable view.</summary>
    public ReadOnlySpan<Vector2d> Velocities => velocities;

    /// <summary>Gets zero-based species identifiers through an immutable view.</summary>
    public ReadOnlySpan<int> Species => species;

    /// <summary>Gets the number of particles.</summary>
    public int Count => positions.Length;

    /// <summary>Gets elapsed simulation time in reduced time units.</summary>
    public double Time { get; }

    /// <summary>Gets the count of completed simulation steps.</summary>
    public long Step { get; }

    /// <summary>Obtains a non-allocating view valid for this snapshot's lifetime.</summary>
    /// <returns>A complete, immutable state observation.</returns>
    public ParticleStateView Observe() => new(ids, positions, velocities, species, Time, Step);
}
