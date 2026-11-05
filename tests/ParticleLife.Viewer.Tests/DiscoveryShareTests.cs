using System.IO.Compression;
using System.Text;
using ParticleLife.Core.Experiments;
using ParticleLife.Viewer;

namespace ParticleLife.Viewer.Tests;

public sealed class DiscoveryShareTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "particle-life-share-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CompressedImportPreservesExactScientificCompanionMetadataAndProtectsExistingFiles()
    {
        Directory.CreateDirectory(directory);
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 12, 2, 21));
        ExperimentEditor editor = new(playback);
        string source = Path.Combine(directory, "initial.json"), archive = Path.Combine(directory, "initial.plshare"), imported = Path.Combine(directory, "imported.json");
        editor.SaveInitial(source, "initial");
        DiscoveryLibrary.Write(source, DiscoveryLibrary.Capture(editor, DiscoveryKind.Initial, "My discovery", "Saved reset", ["test"]) with { Favourite = true });
        DiscoveryShare.Export(source, archive);
        Assert.Equal(DiscoveryKind.Initial, DiscoveryShare.Import(archive, imported));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(imported));
        Assert.Equal(File.ReadAllBytes(source + ".viewer.json"), File.ReadAllBytes(imported + ".viewer.json"));
        Assert.Equal("My discovery", DiscoveryLibrary.Inspect(imported)!.Metadata.Name);
        Assert.True(DiscoveryLibrary.Inspect(imported)!.Metadata.Favourite);
        Assert.Throws<IOException>(() => DiscoveryShare.Import(archive, imported));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(imported));
        Assert.Throws<IOException>(() => DiscoveryShare.Export(source, archive));
    }

    [Fact]
    public void CompanionOnlyCollisionRetainsExistingBytesAndPublishesNoScientificFile()
    {
        Directory.CreateDirectory(directory);
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 12, 2, 21));
        ExperimentEditor editor = new(playback);
        string source = Path.Combine(directory, "source.json"), archive = Path.Combine(directory, "source.plshare"), imported = Path.Combine(directory, "new.json");
        editor.SaveCurrent(source, "source");
        DiscoveryShare.Export(source, archive);
        File.WriteAllText(imported + ".viewer.json", "protected");
        Assert.Throws<IOException>(() => DiscoveryShare.Import(archive, imported));
        Assert.False(File.Exists(imported));
        Assert.Equal("protected", File.ReadAllText(imported + ".viewer.json"));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("path")]
    [InlineData("mismatch")]
    [InlineData("unknown-schema")]
    public void MalformedSharesAreRejectedBeforeDestinationPublication(string failure)
    {
        Directory.CreateDirectory(directory);
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 12, 2, 21));
        ExperimentEditor editor = new(playback);
        string source = Path.Combine(directory, "source.json"), archive = Path.Combine(directory, "bad.plshare"), destination = Path.Combine(directory, "result.json");
        editor.SaveCurrent(source, "source");
        using (ZipArchive zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            Add("document.json", File.ReadAllText(source) + (failure == "mismatch" ? " " : ""));
            Add("share.json", failure == "unknown-schema" ? "{\"documentKind\":\"particle-life-share\",\"schemaVersion\":9,\"metadata\":null}"
                : "{\"documentKind\":\"particle-life-share\",\"schemaVersion\":1,\"metadata\":null}");
            if (failure == "duplicate") { Add("document.json", "{}"); }
            if (failure == "path") { Add("../outside.json", "{}"); }
            if (failure == "mismatch")
            {
                Add("document.json.viewer.json", File.ReadAllText(source + ".viewer.json"));
            }
            void Add(string name, string text)
            {
                using StreamWriter writer = new(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
        }
        Assert.ThrowsAny<Exception>(() => DiscoveryShare.Import(archive, destination));
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".viewer.json"));
    }

    [Fact]
    public void OversizedEntryDeclarationIsRejectedBeforeReadingOrPublishingItsPayload()
    {
        Directory.CreateDirectory(directory);
        string archive = Path.Combine(directory, "oversize.plshare"), destination = Path.Combine(directory, "result.json");
        using (ZipArchive zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using (StreamWriter writer = new(zip.CreateEntry("document.json").Open())) { writer.Write("{}"); }
            using (StreamWriter writer = new(zip.CreateEntry("share.json").Open())) { writer.Write("{}"); }
        }
        byte[] bytes = File.ReadAllBytes(archive);
        for (int i = 0; i < bytes.Length - 28; i++)
        {
            if (BitConverter.ToUInt32(bytes, i) == 0x02014b50)
            {
                BitConverter.GetBytes((uint)DiscoveryShare.MaximumDocumentBytes + 1).CopyTo(bytes, i + 24);
                break;
            }
        }
        File.WriteAllBytes(archive, bytes);
        Assert.Throws<ArgumentException>(() => DiscoveryShare.Import(archive, destination));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void ClipboardValidatesStrictPresetAndRejectsOversizeBeforeApplyingAnything()
    {
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 12, 2, 21));
        ExperimentEditor editor = new(playback);
        string text = DiscoveryShare.ClipboardText(editor.CapturePreset("clipboard"));
        PresetDefinition parsed = DiscoveryShare.ParseClipboard(text);
        Assert.Equal("clipboard", parsed.Name);
        Assert.Throws<ArgumentException>(() => DiscoveryShare.ParseClipboard(new string(' ', DiscoveryShare.MaximumClipboardCharacters + 1)));
        Assert.ThrowsAny<Exception>(() => DiscoveryShare.ParseClipboard(ExperimentFile.Serialize(playback.Experiment)));
        Assert.False(editor.CanUndo);
        Assert.Equal(0, editor.SessionEventCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
    }
}
