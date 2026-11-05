namespace ParticleLife.Viewer;

/// <summary>Reports successful artifact publication separately from an optional library-metadata warning.</summary>
/// <param name="Path">Saved scientific artifact path.</param><param name="Kind">Document purpose.</param>
/// <param name="MetadataWarning">Metadata failure after successful scientific publication, or null.</param>
/// <param name="Entry">Already cached description for the saved artifact; no UI re-read is needed.</param>
public sealed record DiscoveryFileOutcome(string Path, DiscoveryKind Kind, string? MetadataWarning, DiscoveryEntry? Entry);

/// <summary>Owns immutable completed-boundary values for later worker-thread serialization and publication.</summary>
/// <remarks>The serializer closes over immutable records/snapshots, never an editor or graphics object.
/// Capture belongs to the owner; <see cref="Write"/> belongs to an I/O worker. Subsequent edits or steps
/// do not change the prepared artifact. Existing destinations and viewer companions remain protected.</remarks>
public sealed class PreparedDiscoverySave
{
    private readonly Func<(string Scientific, string? Companion)> serialize;
    private readonly DiscoveryMetadata metadata;
    internal PreparedDiscoverySave(Func<(string Scientific, string? Companion)> serialize, DiscoveryMetadata metadata)
    {
        this.serialize = serialize;
        this.metadata = metadata with { Tags = (string[])metadata.Tags.Clone(), Points = (DiscoveryPoint[])metadata.Points.Clone() };
        DiscoveryLibrary.Validate(this.metadata);
    }

    /// <summary>Serializes and exclusively writes captured values; library metadata failure is a warning after success.</summary>
    /// <param name="path">New explicit destination.</param><returns>Actual successful publication and optional metadata warning.</returns>
    public DiscoveryFileOutcome Write(string path)
    {
        (string scientific, string? companion) = serialize();
        ExclusiveFileStorage.Save(path, scientific, companion);
        return DiscoveryLibrary.RecordSaved(path, metadata);
    }
}

/// <summary>Selects an explicit prepared-file operation; parsing never applies it implicitly.</summary>
public enum DiscoveryLoadAction
{
    /// <summary>Restores a saved initial/captured experiment as a paused baseline.</summary>
    LoadState,
    /// <summary>Begins incremental re-simulation of recorded scientific actions.</summary>
    ReplayActions,
    /// <summary>Applies a compatible complete preset without creating particles.</summary>
    PresetLive,
    /// <summary>Prepares a generated preset population on the worker before owner publication.</summary>
    PresetNew,
    /// <summary>Parses a preset for explicit component/alignment review, without any application.</summary>
    ReviewPreset
}

/// <summary>Owns a parsed immutable file action ready for completed-boundary owner publication.</summary>
/// <remarks>Preparing reads/validates files on an I/O worker. Apply performs no file reads or JSON
/// serialization. The owner still creates/transfers execution resources and starts replay at a boundary.</remarks>
public sealed class PreparedDiscoveryLoad
{
    private readonly Action<ExperimentEditor> apply;
    internal PreparedDiscoveryLoad(Action<ExperimentEditor> apply, PresetDefinition? preset = null)
    {
        this.apply = apply; Preset = preset;
    }
    /// <summary>Gets a parsed immutable source preset for review, or null for state/session files.</summary>
    public PresetDefinition? Preset { get; }
    /// <summary>Publishes only on the editor owner at a completed boundary; this operation performs no file I/O.</summary>
    /// <param name="editor">Exclusive active editor owner.</param>
    public void Apply(ExperimentEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor); apply(editor);
    }
}
