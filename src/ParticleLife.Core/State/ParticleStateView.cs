namespace ParticleLife.Core.State;

/// <summary>A non-owning read-only observation of one completed particle state.</summary>
/// <remarks>
/// A simulation view is valid only until the next step or reset. Do not retain its spans across
/// either operation or read them concurrently with mutation. A snapshot view remains valid for
/// the snapshot's lifetime. Obtaining a view allocates no managed memory.
/// </remarks>
public readonly ref struct ParticleStateView
{
    internal ParticleStateView(ReadOnlySpan<long> ids, ReadOnlySpan<Vector2d> positions,
        ReadOnlySpan<Vector2d> velocities, ReadOnlySpan<int> species, double time, long step)
    {
        Ids = ids;
        Positions = positions;
        Velocities = velocities;
        Species = species;
        Time = time;
        Step = step;
    }

    /// <summary>Gets stable, distinct particle identities in storage order.</summary>
    public ReadOnlySpan<long> Ids { get; }

    /// <summary>Gets positions in reduced length units.</summary>
    public ReadOnlySpan<Vector2d> Positions { get; }

    /// <summary>Gets velocities in reduced length units per reduced time unit.</summary>
    public ReadOnlySpan<Vector2d> Velocities { get; }

    /// <summary>Gets zero-based species identifiers in storage order.</summary>
    public ReadOnlySpan<int> Species { get; }

    /// <summary>Gets the number of particles.</summary>
    public int Count => Positions.Length;

    /// <summary>Gets elapsed simulation time in reduced time units.</summary>
    public double Time { get; }

    /// <summary>Gets the number of completed steps represented by this state.</summary>
    public long Step { get; }
}
