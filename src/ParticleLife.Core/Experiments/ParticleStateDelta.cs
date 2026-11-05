using ParticleLife.Core.State;

namespace ParticleLife.Core.Experiments;

/// <summary>Owns exact identity-based particle changes at one completed boundary.</summary>
/// <remarks>Unchanged particles are omitted. Removal preserves the order of surviving identities;
/// updates replace actual values without moving their slots; additions append in their stored order.
/// This is a recorded result, never an instruction to sample a pointer or rerun a generator. Applying
/// or constructing a delta allocates outside numerical stepping. The source state is never mutated.</remarks>
public sealed class ParticleStateDelta
{
    private readonly long[] removedIds;

    /// <summary>Copies and validates disjoint removal, update and addition payloads.</summary>
    /// <param name="removedIds">Distinct identities to remove from the current state.</param>
    /// <param name="updates">Actual replacement values for existing identities at the delta boundary.</param>
    /// <param name="additions">Actual new identities and values, with the same clock as updates.</param>
    /// <exception cref="ArgumentException">Payload identities overlap, repeat or use different clocks.</exception>
    public ParticleStateDelta(ReadOnlySpan<long> removedIds, ParticleSnapshot updates, ParticleSnapshot additions)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(additions);
        if (updates.Time != additions.Time || updates.Step != additions.Step)
        {
            throw new ArgumentException("Delta payloads must share one completed boundary clock.");
        }
        HashSet<long> identities = new();
        foreach (long id in removedIds)
        {
            if (!identities.Add(id)) { throw new ArgumentException("Removed identities must be distinct.", nameof(removedIds)); }
        }
        foreach (long id in updates.Ids)
        {
            if (!identities.Add(id)) { throw new ArgumentException("Updated and removed identities cannot overlap.", nameof(updates)); }
        }
        foreach (long id in additions.Ids)
        {
            if (!identities.Add(id)) { throw new ArgumentException("Added identities cannot overlap another delta operation.", nameof(additions)); }
        }
        this.removedIds = removedIds.ToArray();
        Updates = updates;
        Additions = additions;
    }

    /// <summary>Gets identities removed at this boundary.</summary>
    public ReadOnlySpan<long> RemovedIds => removedIds;
    /// <summary>Gets exact new values for already-present identities.</summary>
    public ParticleSnapshot Updates { get; }
    /// <summary>Gets new identities in their authoritative append order.</summary>
    public ParticleSnapshot Additions { get; }
    /// <summary>Gets the completed boundary step.</summary>
    public long Step => Updates.Step;
    /// <summary>Gets the accumulated simulation time at that boundary.</summary>
    public double Time => Updates.Time;
    /// <summary>Gets the uncompressed numeric payload bytes, excluding object/array overhead.</summary>
    public long PayloadBytes => 8L * removedIds.Length + 44L * Updates.Count + 44L * Additions.Count;

    /// <summary>Applies actual changes atomically to a borrowed completed state.</summary>
    /// <param name="source">Current state whose clock and identities must match the recorded boundary.</param>
    /// <returns>A separately owned snapshot, preserving all untouched values and survivor order.</returns>
    /// <exception cref="ArgumentException">Clocks differ, a required identity is absent or an addition already exists.</exception>
    public ParticleSnapshot Apply(ParticleStateView source)
    {
        if (source.Step != Step || source.Time != Time)
        {
            throw new ArgumentException("Apply a delta only at its recorded exact clock.", nameof(source));
        }
        Dictionary<long, int> indices = new(source.Count);
        for (int i = 0; i < source.Count; i++) { indices.Add(source.Ids[i], i); }
        HashSet<long> removed = new(removedIds);
        foreach (long id in removedIds)
        {
            if (!indices.ContainsKey(id)) { throw new ArgumentException("A removed identity is absent: " + id, nameof(source)); }
        }
        Dictionary<long, int> updated = new(Updates.Count);
        for (int i = 0; i < Updates.Count; i++)
        {
            long id = Updates.Ids[i];
            if (!indices.ContainsKey(id)) { throw new ArgumentException("An updated identity is absent: " + id, nameof(source)); }
            updated.Add(id, i);
        }
        foreach (long id in Additions.Ids)
        {
            if (indices.ContainsKey(id)) { throw new ArgumentException("An added identity already exists: " + id, nameof(source)); }
        }
        int count = checked(source.Count - removedIds.Length + Additions.Count);
        long[] ids = new long[count];
        Vector2d[] positions = new Vector2d[count], velocities = new Vector2d[count];
        int[] species = new int[count];
        int destination = 0;
        for (int i = 0; i < source.Count; i++)
        {
            long id = source.Ids[i];
            if (removed.Contains(id)) { continue; }
            ids[destination] = id;
            if (updated.TryGetValue(id, out int update))
            {
                positions[destination] = Updates.Positions[update];
                velocities[destination] = Updates.Velocities[update];
                species[destination] = Updates.Species[update];
            }
            else
            {
                positions[destination] = source.Positions[i];
                velocities[destination] = source.Velocities[i];
                species[destination] = source.Species[i];
            }
            destination++;
        }
        Additions.Ids.CopyTo(ids.AsSpan(destination));
        Additions.Positions.CopyTo(positions.AsSpan(destination));
        Additions.Velocities.CopyTo(velocities.AsSpan(destination));
        Additions.Species.CopyTo(species.AsSpan(destination));
        return new(ids, positions, velocities, species, Time, Step);
    }

    /// <summary>Derives a lossless compact edit when the resulting survivor and append order is representable.</summary>
    /// <param name="before">Actual state immediately before the operation.</param>
    /// <param name="after">Actual state immediately after, at the same clock.</param>
    /// <returns>A delta, or null when reordering requires a full structural snapshot.</returns>
    /// <remarks>Comparison retains the bit patterns of double components, including signed zero.
    /// Cost is O(N) temporary lookup storage; retained payload is O(changed or removed particles).</remarks>
    public static ParticleStateDelta? TryCreate(ParticleSnapshot before, ParticleSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.Step != after.Step || before.Time != after.Time) { throw new ArgumentException("A delta cannot span numerical steps."); }
        Dictionary<long, int> oldIndices = new(before.Count), newIndices = new(after.Count);
        for (int i = 0; i < before.Count; i++) { oldIndices.Add(before.Ids[i], i); }
        for (int i = 0; i < after.Count; i++) { newIndices.Add(after.Ids[i], i); }
        List<long> removed = [];
        List<int> updates = [], additions = [];
        int lastSurvivor = -1;
        bool appended = false;
        for (int i = 0; i < after.Count; i++)
        {
            if (oldIndices.TryGetValue(after.Ids[i], out int original))
            {
                if (appended || original <= lastSurvivor) { return null; }
                lastSurvivor = original;
                if (before.Species[original] != after.Species[i]
                    || !SameBits(before.Positions[original], after.Positions[i])
                    || !SameBits(before.Velocities[original], after.Velocities[i])) { updates.Add(i); }
            }
            else { appended = true; additions.Add(i); }
        }
        foreach (long id in before.Ids)
        {
            if (!newIndices.ContainsKey(id)) { removed.Add(id); }
        }
        return new(removed.ToArray(), Select(after, updates), Select(after, additions));
    }

    /// <summary>Validates and advances identity/species metadata while parsing an ordered session.</summary>
    internal void ApplyMembership(Dictionary<long, int> membership)
    {
        foreach (long id in removedIds)
        {
            if (!membership.Remove(id)) { throw new ArgumentException("A delta removes an absent identity: " + id); }
        }
        for (int i = 0; i < Updates.Count; i++)
        {
            long id = Updates.Ids[i];
            if (!membership.ContainsKey(id)) { throw new ArgumentException("A delta updates an absent identity: " + id); }
            membership[id] = Updates.Species[i];
        }
        for (int i = 0; i < Additions.Count; i++)
        {
            if (!membership.TryAdd(Additions.Ids[i], Additions.Species[i])) { throw new ArgumentException("A delta repeats an existing identity."); }
        }
    }

    private static bool SameBits(Vector2d left, Vector2d right) =>
        BitConverter.DoubleToInt64Bits(left.X) == BitConverter.DoubleToInt64Bits(right.X)
        && BitConverter.DoubleToInt64Bits(left.Y) == BitConverter.DoubleToInt64Bits(right.Y);

    private static ParticleSnapshot Select(ParticleSnapshot state, List<int> indices)
    {
        long[] ids = new long[indices.Count];
        Vector2d[] positions = new Vector2d[indices.Count], velocities = new Vector2d[indices.Count];
        int[] species = new int[indices.Count];
        for (int i = 0; i < indices.Count; i++)
        {
            int source = indices[i];
            ids[i] = state.Ids[source]; positions[i] = state.Positions[source];
            velocities[i] = state.Velocities[source]; species[i] = state.Species[source];
        }
        return new(ids, positions, velocities, species, state.Time, state.Step);
    }
}
