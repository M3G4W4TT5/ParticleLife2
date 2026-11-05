using System.Text.Json;
using ParticleLife.Core.Execution;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.State;

namespace ParticleLife.Viewer;

/// <summary>Distinguishes recipes, completed captures, reset baselines and recorded timelines.</summary>
public enum DiscoveryKind
{
    /// <summary>A reusable recipe; its preview is generated and sampled, not an evolved state.</summary>
    Preset,
    /// <summary>An actual captured completed state rebased as an experiment.</summary>
    Pattern,
    /// <summary>The exact original reset experiment.</summary>
    Initial,
    /// <summary>A recorded timeline; the library preview explicitly shows its initial state.</summary>
    Session,
    /// <summary>An authoritative terminal capture plus action history; restore and CPU re-simulation are separate actions.</summary>
    CapturedSession
}

/// <summary>One sampled thumbnail point in normalized nominal-world coordinates.</summary>
/// <param name="X">Horizontal coordinate, clamped to [0,1].</param><param name="Y">Vertical coordinate, clamped to [0,1].</param>
/// <param name="Rgb">24-bit species colour.</param>
public sealed record DiscoveryPoint(float X, float Y, uint Rgb);

/// <summary>Local library description and a bounded preview, separate from strict scientific file formats.</summary>
/// <param name="SchemaVersion">Metadata schema, currently one.</param><param name="Name">Searchable display name; scientific file bytes are unchanged by rename.</param>
/// <param name="Description">Searchable user description.</param><param name="Tags">Searchable tags.</param><param name="Favourite">Favourite filter flag.</param>
/// <param name="Kind">Document purpose.</param><param name="CreatedAtUtc">Original library creation time.</param><param name="LastOpenedAtUtc">Most recent successful load/replay.</param>
/// <param name="PreviewLabel">Honest source of the preview.</param><param name="Population">Full source population, which may exceed sampled points.</param>
/// <param name="WorldAspect">Nominal world width divided by height.</param><param name="Points">At most 256 sampled points.</param>
/// <param name="FileBytes">Scientific file size at preview creation.</param><param name="FileModifiedUtcTicks">Scientific modification time binding for stale-preview detection.</param>
public sealed record DiscoveryMetadata(int SchemaVersion, string Name, string Description, string[] Tags, bool Favourite,
    DiscoveryKind Kind, DateTimeOffset CreatedAtUtc, DateTimeOffset? LastOpenedAtUtc, string PreviewLabel,
    int Population, double WorldAspect, DiscoveryPoint[] Points, long FileBytes, long FileModifiedUtcTicks);

/// <summary>One locally indexed item, containing no live simulation view or GPU resource.</summary>
/// <param name="Path">Absolute scientific file path.</param><param name="Metadata">Validated local description.</param><param name="Notice">Optional stale/missing-preview or metadata error.</param>
public sealed record DiscoveryEntry(string Path, DiscoveryMetadata Metadata, string? Notice = null);

/// <summary>Indexes local discovery metadata with bounded refresh I/O and no rendering-time file reads.</summary>
/// <remarks>Display rename never renames or rewrites the scientific file or bound viewer companion.
/// Sidecar updates use an exclusive reservation and atomic same-directory replacement. Scientific
/// content is validated only by an explicit load/import; a prefix classification is not validation.</remarks>
public static class DiscoveryLibrary
{
    /// <summary>Gets the maximum visible indexed files in one refresh.</summary>
    public const int MaximumEntries = 300;
    /// <summary>Gets the maximum sampled thumbnail points, independent of source population.</summary>
    public const int MaximumPreviewPoints = 256;
    /// <summary>Gets the local metadata suffix; companions are not scientific inputs.</summary>
    public const string MetadataSuffix = ".discovery.json";

    /// <summary>Captures a bounded preview at a completed editor boundary, never in Draw.</summary>
    /// <param name="editor">Owning sequential editor.</param><param name="kind">Correct saved document purpose.</param>
    /// <param name="name">Library display name.</param><param name="description">Optional searchable description.</param><param name="tags">Optional tags.</param>
    /// <returns>Unbound metadata ready for <see cref="Write"/> after a scientific save succeeds.</returns>
    public static DiscoveryMetadata Capture(ExperimentEditor editor, DiscoveryKind kind, string name, string description = "", string[]? tags = null)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (kind == DiscoveryKind.Preset) { return PreviewPreset(editor.CapturePreset(name), description, tags); }
        if (kind == DiscoveryKind.CapturedSession || kind == DiscoveryKind.Session && editor.HasCapturedExecution)
        { return PreviewCaptured(editor.CaptureRecordedSession(name), editor.Appearance, description, tags); }
        var source = kind == DiscoveryKind.Pattern ? editor.CaptureCurrentExperiment(name) : editor.Playback.Experiment;
        var named = new ExperimentDefinition(name, source.Parameters, source.Matrix, source.InitialState,
            source.TimeStep, source.Generator, source.Backend, source.WorkerCount, source.Provenance);
        return PreviewExperiment(named, editor.Appearance, kind, description, tags);
    }

    internal static DiscoveryMetadata PreviewPreset(PresetDefinition preset, string description, string[]? tags)
    {
        int population = preset.Populations.ToArray().Sum(), samples = Math.Min(population, MaximumPreviewPoints);
        double width = preset.Configuration.Parameters.WorldWidth, height = preset.Configuration.Parameters.WorldHeight;
        DiscoveryPoint[] points = new DiscoveryPoint[samples];
        Random random = new(preset.Generation.Seed);
        int species = 0, end = preset.Populations[0];
        for (int i = 0; i < samples; i++)
        {
            long sourceIndex = (long)i * population / samples;
            while (sourceIndex >= end && species < preset.Populations.Length - 1) { end += preset.Populations[++species]; }
            Vector2d position = ParticleGeneration.Position(random, i, samples, species, preset.Configuration.Matrix.SpeciesCount,
                preset.Configuration.Parameters, preset.Generation);
            points[i] = Point(position, width, height, preset.Appearance.ColorForSpecies(species));
        }
        return Preview(preset.Name, description, tags, DiscoveryKind.Preset, population, width, height, points, "Generated recipe sample; not an evolved pattern");
    }

    internal static DiscoveryMetadata PreviewExperiment(ExperimentDefinition experiment, AppearanceSettings appearance,
        DiscoveryKind kind, string description, string[]? tags)
    {
        ParticleStateView state = experiment.InitialState.Observe();
        double width = experiment.Parameters.WorldWidth, height = experiment.Parameters.WorldHeight;
        string label = kind == DiscoveryKind.Session ? "Session INITIAL state; terminal pattern may differ"
            : kind == DiscoveryKind.Initial ? "Original reset baseline" : $"Captured completed state, step {experiment.Provenance?.SourceStep ?? state.Step}";
        return PreviewValues(experiment.Name, state, width, height, appearance, kind, description, tags, label);
    }

    internal static DiscoveryMetadata PreviewCaptured(CapturedSessionDefinition captured, AppearanceSettings appearance, string description, string[]? tags)
    {
        SimulationCapture terminal = captured.TerminalCapture;
        return PreviewCapturedBoundary(captured.Actions.InitialExperiment.Name, terminal.State, terminal.Configuration, terminal.Execution, appearance, description, tags);
    }

    internal static DiscoveryMetadata PreviewCapturedBoundary(string name, ParticleSnapshot terminal, ParticleLife.Core.Numerics.SimulationConfiguration configuration,
        ExecutionProvenance execution, AppearanceSettings appearance, string description, string[]? tags) =>
        PreviewValues(name, terminal.Observe(), configuration.Parameters.WorldWidth, configuration.Parameters.WorldHeight,
            appearance, DiscoveryKind.CapturedSession, description, tags, $"Stored TERMINAL capture, step {terminal.Step}; {execution.Hardware}/{execution.Precision}");

    /// <summary>Samples only the supplied owned source boundary; no simulation or graphics owner is retained.</summary>
    private static DiscoveryMetadata PreviewValues(string name, ParticleStateView state, double width, double height, AppearanceSettings appearance,
        DiscoveryKind kind, string description, string[]? tags, string label)
    {
        int samples = Math.Min(state.Count, MaximumPreviewPoints);
        DiscoveryPoint[] points = new DiscoveryPoint[samples];
        for (int i = 0; i < samples; i++)
        {
            int source = (int)((long)i * state.Count / samples);
            points[i] = Point(state.Positions[source], width, height, appearance.ColorForSpecies(state.Species[source]));
        }
        return Preview(name, description, tags, kind, state.Count, width, height, points, label);
    }

    private static DiscoveryMetadata Preview(string name, string description, string[]? tags, DiscoveryKind kind, int population,
        double width, double height, DiscoveryPoint[] points, string label)
    {
        double aspect = width / height;
        if (!double.IsFinite(aspect) || aspect <= 0) { aspect = 1; label += "; aspect outside preview range"; }
        DiscoveryMetadata metadata = new(1, name, description, tags ?? [], false, kind, DateTimeOffset.UtcNow, null,
            label, population, aspect, points, 0, 0);
        Validate(metadata);
        return metadata;
    }

    /// <summary>Refreshes a bounded local index; search spans name, description, tags and filename.</summary>
    /// <param name="directory">Existing or new local library folder.</param><param name="query">Optional case-insensitive search.</param>
    /// <param name="favouritesOnly">Restricts to favourites.</param><param name="recentFirst">Orders successful load timestamps before creation time.</param>
    /// <param name="cancellationToken">Cancels between files; one in-progress operating-system read may finish first.</param>
    /// <returns>At most <see cref="MaximumEntries"/> entries, with previews already loaded.</returns>
    public static IReadOnlyList<DiscoveryEntry> Read(string directory, string query = "", bool favouritesOnly = false, bool recentFirst = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        IComparer<DiscoveryEntry> bestFirst = Comparer<DiscoveryEntry>.Create((left, right) =>
        {
            int order = recentFirst ? (right.Metadata.LastOpenedAtUtc ?? right.Metadata.CreatedAtUtc).CompareTo(left.Metadata.LastOpenedAtUtc ?? left.Metadata.CreatedAtUtc)
                : StringComparer.OrdinalIgnoreCase.Compare(left.Metadata.Name, right.Metadata.Name);
            return order == 0 ? StringComparer.Ordinal.Compare(left.Path, right.Path) : order;
        });
        // Worst retained entry is the heap root; a complete search retains only the best 300
        // previews instead of collecting every matching thumbnail before truncation.
        PriorityQueue<DiscoveryEntry, DiscoveryEntry> entries = new(Comparer<DiscoveryEntry>.Create((left, right) => bestFirst.Compare(right, left)));
        foreach (string path in Directory.EnumerateFiles(directory, "*.json").Where(path => !path.EndsWith(".viewer.json", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith(MetadataSuffix, StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DiscoveryEntry? entry;
            try { entry = Inspect(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            if (entry is null || favouritesOnly && !entry.Metadata.Favourite) { continue; }
            string text = entry.Metadata.Name + " " + entry.Metadata.Description + " " + string.Join(' ', entry.Metadata.Tags) + " " + System.IO.Path.GetFileName(path);
            if (text.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                entries.Enqueue(entry, entry);
                if (entries.Count > MaximumEntries) { _ = entries.Dequeue(); }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return entries.UnorderedItems.Select(item => item.Element).Order(bestFirst).ToArray();
    }

    /// <summary>Reads one metadata sidecar or a bounded header classification, without loading a particle array.</summary>
    /// <param name="path">Existing scientific JSON file.</param><returns>An entry, or null for unsupported documents.</returns>
    public static DiscoveryEntry? Inspect(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        FileInfo file = new(path);
        string? notice = null;
        string sidecar = path + MetadataSuffix;
        if (File.Exists(sidecar))
        {
            try
            {
                if (new FileInfo(sidecar).Length > 128 * 1024) { throw new ArgumentException("Library metadata exceeds 128 KiB."); }
                DiscoveryMetadata metadata = JsonSerializer.Deserialize<DiscoveryMetadata>(File.ReadAllText(sidecar), EditorJson.Options)
                    ?? throw new JsonException("Library metadata must be an object.");
                Validate(metadata);
                if (metadata.Kind == DiscoveryKind.Session && HasCapturedHeader(path))
                {
                    metadata = metadata with { Kind = DiscoveryKind.CapturedSession, Points = [], Population = 0,
                        PreviewLabel = "No preview indexed for stored TERMINAL capture" };
                }
                if (metadata.FileBytes != file.Length || metadata.FileModifiedUtcTicks != file.LastWriteTimeUtc.Ticks)
                {
                    metadata = metadata with { Points = [], Population = 0, PreviewLabel = "Preview stale: scientific file changed" };
                    notice = "Refresh the preview by saving a new discovery.";
                }
                return new(path, metadata, notice);
            }
            catch (Exception error) when (error is JsonException or ArgumentException or IOException or NotSupportedException)
            {
                notice = "Metadata ignored: " + error.Message;
            }
        }
        byte[] prefix = new byte[(int)Math.Min(file.Length, 16 * 1024)];
        using (FileStream stream = File.OpenRead(path)) { stream.ReadExactly(prefix); }
        Utf8JsonReader reader = new(prefix, isFinalBlock: false, state: default);
        DiscoveryKind? kind = null;
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) { continue; }
                string? property = reader.GetString();
                if (!reader.Read()) { break; }
                if (reader.TokenType == JsonTokenType.String)
                {
                    string? value = reader.GetString();
                    if (property == "documentKind" && value == "particle-life-preset") { kind = DiscoveryKind.Preset; }
                    if (property == "format" && value == "particle-life-session") { kind = DiscoveryKind.Session; }
                    if (property == "format" && value == ExecutionCaptureFile.Format) { kind = DiscoveryKind.CapturedSession; }
                    if (property == "modelVersion" && value?.StartsWith("particle-life/", StringComparison.Ordinal) == true)
                    {
                        kind = path.EndsWith(".initial.json", StringComparison.OrdinalIgnoreCase) ? DiscoveryKind.Initial : DiscoveryKind.Pattern;
                    }
                    if (property == "name" && value is not null) { name = value; }
                }
                if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject) { break; }
            }
        }
        catch (JsonException) { return null; }
        if (kind is null) { return null; }
        DiscoveryMetadata fallback = new(1, name, "", [], false, kind.Value, file.CreationTimeUtc, null,
            "No preview yet; save from the app to create one", 0, 1, [], file.Length, file.LastWriteTimeUtc.Ticks);
        return new(path, fallback, notice);
    }

    /// <summary>Atomically creates or updates local metadata after validating the complete description.</summary>
    /// <param name="path">Existing scientific file; it is never changed.</param><param name="metadata">Desired display values and bounded preview.</param>
    public static void Write(string path, DiscoveryMetadata metadata) => _ = WriteBound(path, metadata);

    private static DiscoveryMetadata WriteBound(string path, DiscoveryMetadata metadata)
    {
        Validate(metadata);
        FileInfo file = new(path);
        if (!file.Exists) { throw new FileNotFoundException("Save the scientific file before library metadata.", path); }
        if (metadata.FileBytes > 0 && (metadata.FileBytes != file.Length || metadata.FileModifiedUtcTicks != file.LastWriteTimeUtc.Ticks))
        { metadata = metadata with { Points = [], Population = 0, PreviewLabel = "Preview stale: scientific file changed" }; }
        DiscoveryMetadata bound = metadata with { FileBytes = file.Length, FileModifiedUtcTicks = file.LastWriteTimeUtc.Ticks };
        DiscoveryLibraryStorage.Publish(path + MetadataSuffix, stream => JsonSerializer.Serialize(stream, bound, EditorJson.Options), replace: true);
        return bound;
    }

    /// <summary>Records optional library metadata after a scientific artifact has already been saved.</summary>
    /// <param name="path">Successfully published scientific file.</param><param name="metadata">Desired local description.</param>
    /// <returns>A successful save outcome even if optional metadata cannot be written.</returns>
    public static DiscoveryFileOutcome RecordSaved(string path, DiscoveryMetadata metadata)
    {
        string? warning = null;
        try { metadata = WriteBound(path, metadata); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException)
        { warning = "Library metadata was not saved: " + error.Message; }
        return new(Path.GetFullPath(path), metadata.Kind, warning, new DiscoveryEntry(Path.GetFullPath(path), metadata, warning));
    }

    /// <summary>Validates local metadata and bounded previews before persistence or import.</summary>
    /// <param name="metadata">Complete metadata candidate.</param>
    public static void Validate(DiscoveryMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.SchemaVersion != 1 || !Enum.IsDefined(metadata.Kind)) { throw new NotSupportedException("Unsupported discovery metadata version/kind."); }
        if (string.IsNullOrWhiteSpace(metadata.Name) || metadata.Name.Length > 200 || metadata.Description is null || metadata.Description.Length > 4000
            || metadata.Tags is null || metadata.Tags.Length > 32 || metadata.Tags.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Length > 64)
            || metadata.PreviewLabel is null || metadata.PreviewLabel.Length > 300 || metadata.Population < 0 || !double.IsFinite(metadata.WorldAspect) || metadata.WorldAspect <= 0
            || metadata.Points is null || metadata.Points.Length > MaximumPreviewPoints || metadata.FileBytes < 0 || metadata.FileModifiedUtcTicks < 0)
        { throw new ArgumentException("Invalid discovery name, metadata limits or preview."); }
        foreach (DiscoveryPoint point in metadata.Points)
        {
            if (point is null || !float.IsFinite(point.X) || !float.IsFinite(point.Y) || point.X is < 0 or > 1 || point.Y is < 0 or > 1 || point.Rgb > 0xFFFFFF)
            { throw new ArgumentException("Invalid thumbnail point."); }
        }
    }

    private static DiscoveryPoint Point(Vector2d position, double width, double height, uint color) => new(
        (float)Math.Clamp(position.X / width, 0, 1), (float)Math.Clamp(position.Y / height, 0, 1), color);

    private static bool HasCapturedHeader(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] prefix = new byte[(int)Math.Min(stream.Length, 16 * 1024)]; stream.ReadExactly(prefix);
        Utf8JsonReader reader = new(prefix, isFinalBlock: false, state: default);
        try
        {
            while (reader.Read())
            {
                if (reader.CurrentDepth == 1 && reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("format"))
                { return reader.Read() && reader.TokenType == JsonTokenType.String && reader.ValueTextEquals(ExecutionCaptureFile.Format); }
            }
        }
        catch (JsonException) { }
        return false;
    }
}
