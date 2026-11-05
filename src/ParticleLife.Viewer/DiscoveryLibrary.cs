using System.Text.Json;
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
    Session
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
        int population;
        double width, height;
        DiscoveryPoint[] points;
        string label;
        if (kind == DiscoveryKind.Preset)
        {
            PresetDefinition preset = editor.CapturePreset(name);
            population = preset.Populations.ToArray().Sum();
            width = preset.Configuration.Parameters.WorldWidth; height = preset.Configuration.Parameters.WorldHeight;
            int samples = Math.Min(population, MaximumPreviewPoints);
            points = new DiscoveryPoint[samples];
            Random random = new(preset.Generation.Seed);
            int species = 0, end = preset.Populations.Length == 0 ? 0 : preset.Populations[0];
            for (int i = 0; i < samples; i++)
            {
                long sourceIndex = (long)i * population / samples;
                while (sourceIndex >= end && species < preset.Populations.Length - 1) { end += preset.Populations[++species]; }
                Vector2d position = ParticleGeneration.Position(random, i, samples, species, preset.Configuration.Matrix.SpeciesCount,
                    preset.Configuration.Parameters, preset.Generation);
                points[i] = Point(position, width, height, preset.Appearance.ColorForSpecies(species));
            }
            label = "Generated recipe sample; not an evolved pattern";
        }
        else
        {
            bool initial = kind is DiscoveryKind.Initial or DiscoveryKind.Session;
            ParticleStateView state = initial ? editor.Playback.Experiment.InitialState.Observe() : editor.Playback.Observe();
            var parameters = initial ? editor.Playback.Experiment.Parameters : editor.Configuration.Parameters;
            population = state.Count; width = parameters.WorldWidth; height = parameters.WorldHeight;
            int samples = Math.Min(population, MaximumPreviewPoints);
            points = new DiscoveryPoint[samples];
            for (int i = 0; i < samples; i++)
            {
                int source = (int)((long)i * population / samples);
                points[i] = Point(state.Positions[source], width, height, editor.Appearance.ColorForSpecies(state.Species[source]));
            }
            label = kind == DiscoveryKind.Session ? "Session INITIAL state; terminal pattern may differ"
                : initial ? "Original reset baseline" : $"Captured completed state, step {state.Step}";
        }
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
    /// <returns>At most <see cref="MaximumEntries"/> entries, with previews already loaded.</returns>
    public static IReadOnlyList<DiscoveryEntry> Read(string directory, string query = "", bool favouritesOnly = false, bool recentFirst = true)
    {
        Directory.CreateDirectory(directory);
        List<DiscoveryEntry> entries = [];
        foreach (string path in Directory.EnumerateFiles(directory, "*.json").Where(path => !path.EndsWith(".viewer.json", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith(MetadataSuffix, StringComparison.OrdinalIgnoreCase)))
        {
            DiscoveryEntry? entry = Inspect(path);
            if (entry is null || favouritesOnly && !entry.Metadata.Favourite) { continue; }
            string text = entry.Metadata.Name + " " + entry.Metadata.Description + " " + string.Join(' ', entry.Metadata.Tags) + " " + System.IO.Path.GetFileName(path);
            if (text.Contains(query, StringComparison.OrdinalIgnoreCase)) { entries.Add(entry); }
        }
        return (recentFirst ? entries.OrderByDescending(entry => entry.Metadata.LastOpenedAtUtc ?? entry.Metadata.CreatedAtUtc)
            : entries.OrderBy(entry => entry.Metadata.Name, StringComparer.OrdinalIgnoreCase)).Take(MaximumEntries).ToArray();
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
                if (metadata.FileBytes != file.Length || metadata.FileModifiedUtcTicks != file.LastWriteTimeUtc.Ticks)
                {
                    metadata = metadata with { Points = [], PreviewLabel = "Preview stale: scientific file changed" };
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
    public static void Write(string path, DiscoveryMetadata metadata)
    {
        Validate(metadata);
        FileInfo file = new(path);
        if (!file.Exists) { throw new FileNotFoundException("Save the scientific file before library metadata.", path); }
        DiscoveryMetadata bound = metadata with { FileBytes = file.Length, FileModifiedUtcTicks = file.LastWriteTimeUtc.Ticks };
        DiscoveryLibraryStorage.Publish(path + MetadataSuffix, stream => JsonSerializer.Serialize(stream, bound, EditorJson.Options), replace: true);
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
}
