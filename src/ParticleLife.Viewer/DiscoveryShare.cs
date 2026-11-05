using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using ParticleLife.Core.Experiments;

namespace ParticleLife.Viewer;

/// <summary>Shares local scientific files as fixed-entry compressed artifacts without publishing them online.</summary>
/// <remarks>The .plshare ZIP contract contains share.json, document.json and optionally
/// document.json.viewer.json. Original scientific/companion UTF-8 bytes are preserved. There are
/// no archive-controlled extraction paths. Every entry and strict inner document validates before
/// exclusive publication. Limits are explicit and do not truncate oversized recordings.</remarks>
public static class DiscoveryShare
{
    /// <summary>Gets the maximum uncompressed scientific document size (256 MiB).</summary>
    public const long MaximumDocumentBytes = 256L * 1024 * 1024;
    /// <summary>Gets the maximum clipboard preset text length; large artifacts use files.</summary>
    public const int MaximumClipboardCharacters = 1024 * 1024;
    private const long MaximumCompanionBytes = 4L * 1024 * 1024;
    private const long MaximumArchiveBytes = 256L * 1024 * 1024;
    private const long MaximumExpandedBytes = 512L * 1024 * 1024;

    /// <summary>Creates a protected compressed share from a validated scientific file and bound companion.</summary>
    /// <param name="sourcePath">Existing experiment, preset or session.</param><param name="destinationPath">New .plshare destination; never overwritten.</param>
    public static void Export(string sourcePath, string destinationPath)
    {
        byte[] scientific = ReadBoundedFile(sourcePath, MaximumDocumentBytes);
        byte[]? companion = File.Exists(sourcePath + ".viewer.json") ? ReadBoundedFile(sourcePath + ".viewer.json", MaximumCompanionBytes) : null;
        DiscoveryKind kind = ExperimentEditor.ValidateDiscoveryContent(Text(scientific), companion is null ? null : Text(companion));
        DiscoveryMetadata? metadata = DiscoveryLibrary.Inspect(sourcePath)?.Metadata;
        if (metadata is not null && metadata.Kind != kind && !(kind == DiscoveryKind.Pattern && metadata.Kind == DiscoveryKind.Initial))
        { throw new ArgumentException("Library metadata kind does not match the scientific document."); }
        DiscoveryLibraryStorage.Publish(destinationPath, output =>
        {
            using (ZipArchive archive = new(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                Write("document.json", scientific);
                if (companion is not null) { Write("document.json.viewer.json", companion); }
                using Stream manifest = archive.CreateEntry("share.json", CompressionLevel.Optimal).Open();
                JsonSerializer.Serialize(manifest, new ShareManifest("particle-life-share", 1, metadata), EditorJson.Options);
                void Write(string name, byte[] bytes)
                {
                    using Stream entry = archive.CreateEntry(name, CompressionLevel.Optimal).Open(); entry.Write(bytes);
                }
            }
            if (output.Position > MaximumArchiveBytes) { throw new ArgumentException("Compressed share exceeds 256 MiB; no truncated artifact was published."); }
        });
    }

    /// <summary>Validates and imports a share into a new explicit local scientific path without applying it.</summary>
    /// <param name="archivePath">Existing .plshare ZIP.</param><param name="destinationPath">New explicit scientific filename; all existing destinations are protected.</param>
    /// <returns>The imported document purpose; load/replay remains a separate user action.</returns>
    public static DiscoveryKind Import(string archivePath, string destinationPath) => ImportDetailed(archivePath, destinationPath).Kind;

    /// <summary>Imports a validated artifact and reports optional metadata failure separately from scientific success.</summary>
    /// <param name="archivePath">Existing .plshare ZIP.</param><param name="destinationPath">New explicit scientific filename.</param>
    /// <returns>Successful scientific publication with an optional metadata warning.</returns>
    public static DiscoveryFileOutcome ImportDetailed(string archivePath, string destinationPath)
    {
        if (new FileInfo(archivePath).Length > MaximumArchiveBytes) { throw new ArgumentException("Share exceeds the 256 MiB compressed import limit."); }
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count is < 2 or > 3) { throw new ArgumentException("A share must contain document.json, share.json and an optional bound viewer companion."); }
        HashSet<string> names = new(StringComparer.Ordinal);
        long expanded = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.FullName is not ("document.json" or "share.json" or "document.json.viewer.json") || !names.Add(entry.FullName))
            { throw new ArgumentException("Unknown or duplicate share entry; archive extraction paths are never accepted."); }
            expanded = checked(expanded + entry.Length);
        }
        if (expanded > MaximumExpandedBytes || !names.Contains("document.json") || !names.Contains("share.json"))
        { throw new ArgumentException("Incomplete share or expanded size above 512 MiB."); }
        byte[] scientific = Read(archive.GetEntry("document.json")!, MaximumDocumentBytes);
        byte[]? companion = archive.GetEntry("document.json.viewer.json") is ZipArchiveEntry settings ? Read(settings, MaximumCompanionBytes) : null;
        ShareManifest manifest = JsonSerializer.Deserialize<ShareManifest>(Read(archive.GetEntry("share.json")!, 128 * 1024), EditorJson.Options)
            ?? throw new JsonException("Share manifest must be an object.");
        if (manifest.DocumentKind != "particle-life-share" || manifest.SchemaVersion != 1) { throw new NotSupportedException("Unsupported share wrapper version."); }
        if (manifest.Metadata is not null) { DiscoveryLibrary.Validate(manifest.Metadata); }
        DiscoveryKind kind = ExperimentEditor.ValidateDiscoveryContent(Text(scientific), companion is null ? null : Text(companion));
        if (manifest.Metadata is not null && manifest.Metadata.Kind != kind && !(kind == DiscoveryKind.Pattern && manifest.Metadata.Kind == DiscoveryKind.Initial))
        { throw new ArgumentException("Share metadata kind does not match its scientific document."); }
        DiscoveryLibraryStorage.PublishScientific(destinationPath, scientific, companion);
        DiscoveryMetadata metadata = manifest.Metadata ?? new DiscoveryMetadata(1, Path.GetFileNameWithoutExtension(destinationPath), "", [], false,
            kind, DateTimeOffset.UtcNow, null, "No preview supplied by shared artifact", 0, 1, [], 0, 0);
        // Exact validated bytes were deliberately published at a new path/time, so the incoming
        // thumbnail is rebound rather than mistaken for an externally changed local artifact.
        return DiscoveryLibrary.RecordSaved(destinationPath, metadata with { FileBytes = 0, FileModifiedUtcTicks = 0 });
    }

    /// <summary>Produces validated uncompressed preset text for an explicit clipboard gesture.</summary>
    /// <param name="preset">Reusable recipe to share.</param><returns>Strict preset JSON within the clipboard limit.</returns>
    public static string ClipboardText(PresetDefinition preset)
    {
        string text = PresetFile.Serialize(preset);
        if (text.Length > MaximumClipboardCharacters) { throw new ArgumentException("Preset exceeds the 1 Mi-character clipboard limit; export a file instead."); }
        return text;
    }

    /// <summary>Validates clipboard preset text without changing any active editor.</summary>
    /// <param name="text">Strict reusable-preset JSON.</param><returns>A validated preset for explicit selective application or saving.</returns>
    public static PresetDefinition ParseClipboard(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaximumClipboardCharacters) { throw new ArgumentException("Clipboard content exceeds 1 Mi characters; use file import."); }
        return PresetFile.Deserialize(text);
    }

    private static byte[] ReadBoundedFile(string path, long maximum)
    {
        using FileStream stream = File.OpenRead(path);
        if (stream.Length > maximum) { throw new ArgumentException($"Document exceeds its explicit {maximum / 1024 / 1024} MiB sharing limit; no data was truncated."); }
        using MemoryStream output = new();
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            int read = stream.Read(buffer);
            if (read == 0) { break; }
            if (output.Length + read > maximum) { throw new ArgumentException("File grew beyond its sharing limit."); }
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static byte[] Read(ZipArchiveEntry entry, long maximum)
    {
        if (entry.Length > maximum) { throw new ArgumentException("Share entry exceeds its explicit storage limit."); }
        using Stream input = entry.Open();
        using MemoryStream output = new();
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            int read = input.Read(buffer);
            if (read == 0) { break; }
            if (output.Length + read > maximum) { throw new ArgumentException("Expanded share entry exceeds its storage limit."); }
            output.Write(buffer, 0, read);
        }
        if (output.Length != entry.Length) { throw new ArgumentException("Share entry length does not match its actual contents."); }
        return output.ToArray();
    }

    private static string Text(byte[] bytes)
    {
        string text = new UTF8Encoding(false, true).GetString(bytes);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    private sealed record ShareManifest(string DocumentKind, int SchemaVersion, DiscoveryMetadata? Metadata);
}

public sealed partial class ExperimentEditor
{
    internal static DiscoveryKind ValidateDiscoveryContent(string content, string? companion)
    {
        using JsonDocument document = JsonDocument.Parse(content, new JsonDocumentOptions { AllowDuplicateProperties = false });
        JsonElement root = document.RootElement;
        int species;
        DiscoveryKind kind;
        if (root.TryGetProperty("documentKind", out JsonElement presetKind) && presetKind.GetString() == "particle-life-preset")
        {
            _ = PresetFile.Deserialize(content);
            if (companion is not null) { throw new ArgumentException("A preset cannot carry a scientific viewer companion."); }
            return DiscoveryKind.Preset;
        }
        if (root.TryGetProperty("format", out JsonElement format) && format.GetString() == SessionFile.Format)
        {
            SessionDefinition session = SessionFile.Deserialize(content);
            species = session.Events.Count == 0 ? session.InitialExperiment.Matrix.SpeciesCount : session.Events[^1].Configuration.Matrix.SpeciesCount;
            kind = DiscoveryKind.Session;
        }
        else
        {
            species = ExperimentFile.Deserialize(content).Matrix.SpeciesCount;
            kind = DiscoveryKind.Pattern;
        }
        if (companion is not null)
        {
            ViewerSettingsData settings = JsonSerializer.Deserialize<ViewerSettingsData>(companion, EditorJson.Options)
                ?? throw new JsonException("Viewer companion must be an object.");
            if (settings.DocumentKind != "particle-life-viewer-settings" || settings.SchemaVersion != 1
                || settings.ScientificSha256 != Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))))
            { throw new ArgumentException("Invalid or mismatched viewer companion."); }
            PresetDefinition.ValidatePopulations(settings.Populations, species);
            _ = settings.Generation.ToSettings(); _ = settings.Appearance.ToSettings(); _ = settings.Playback.ToSettings();
        }
        return kind;
    }
}
