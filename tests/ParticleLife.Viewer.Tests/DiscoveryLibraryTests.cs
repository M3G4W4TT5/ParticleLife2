using ParticleLife.Core.Experiments;
using ParticleLife.Viewer;

namespace ParticleLife.Viewer.Tests;

public sealed class DiscoveryLibraryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "particle-life-library-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void PreviewSourcesDistinguishInitialCurrentSessionAndBoundedGeneratedRecipe()
    {
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 12, 2, 21));
        ExperimentEditor editor = new(playback);
        DiscoveryMetadata initial = DiscoveryLibrary.Capture(editor, DiscoveryKind.Initial, "initial");
        for (int i = 0; i < 5; i++) { playback.SingleStep(); }
        DiscoveryMetadata current = DiscoveryLibrary.Capture(editor, DiscoveryKind.Pattern, "pattern");
        DiscoveryMetadata session = DiscoveryLibrary.Capture(editor, DiscoveryKind.Session, "session");
        Assert.Equal(initial.Points, session.Points);
        Assert.NotEqual(initial.Points, current.Points);
        Assert.Contains("INITIAL", session.PreviewLabel);
        editor.SetPopulationRecipe([500_000, 500_000]);
        DiscoveryMetadata preset = DiscoveryLibrary.Capture(editor, DiscoveryKind.Preset, "large recipe");
        Assert.Equal(1_000_000, preset.Population);
        Assert.Equal(DiscoveryLibrary.MaximumPreviewPoints, preset.Points.Length);
        Assert.Equal(12, playback.Observe().Count);
        Assert.Contains("Generated", preset.PreviewLabel);
    }

    [Fact]
    public void SearchFavouriteRecentAndDisplayRenamePreserveScientificAndCompanionBytes()
    {
        Directory.CreateDirectory(directory);
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 12, 2, 21));
        ExperimentEditor editor = new(playback);
        string path = Path.Combine(directory, "original.initial.json");
        editor.SaveInitial(path, "original");
        byte[] scientific = File.ReadAllBytes(path), companion = File.ReadAllBytes(path + ".viewer.json");
        DiscoveryMetadata metadata = DiscoveryLibrary.Capture(editor, DiscoveryKind.Initial, "Changed display", "A ribbon discovery", ["blue", "fast"])
            with
        { Favourite = true, LastOpenedAtUtc = DateTimeOffset.UtcNow };
        DiscoveryLibrary.Write(path, metadata);
        Assert.Single(DiscoveryLibrary.Read(directory, "ribbon", favouritesOnly: true, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(DiscoveryLibrary.Read(directory, "BLUE", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(DiscoveryLibrary.Read(directory, "missing", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(scientific, File.ReadAllBytes(path));
        Assert.Equal(companion, File.ReadAllBytes(path + ".viewer.json"));
        byte[] saved = File.ReadAllBytes(path + DiscoveryLibrary.MetadataSuffix);
        Assert.Throws<ArgumentException>(() => DiscoveryLibrary.Write(path, metadata with { Name = "" }));
        Assert.Equal(saved, File.ReadAllBytes(path + DiscoveryLibrary.MetadataSuffix));
    }

    [Fact]
    public void ChangedFilesAndMalformedMetadataNeverShowAnIncorrectRetainedPreview()
    {
        Directory.CreateDirectory(directory);
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 12, 2, 21));
        ExperimentEditor editor = new(playback);
        string path = Path.Combine(directory, "sample.experiment.json");
        editor.SaveCurrent(path, "sample");
        DiscoveryLibrary.Write(path, DiscoveryLibrary.Capture(editor, DiscoveryKind.Pattern, "sample"));
        File.AppendAllText(path, " ");
        DiscoveryEntry stale = DiscoveryLibrary.Inspect(path)!;
        Assert.Empty(stale.Metadata.Points);
        Assert.Contains("stale", stale.Metadata.PreviewLabel);
        File.WriteAllText(path + DiscoveryLibrary.MetadataSuffix, "{\"unexpected\":true}");
        DiscoveryEntry fallback = DiscoveryLibrary.Inspect(path)!;
        Assert.Empty(fallback.Metadata.Points);
        Assert.Contains("Metadata ignored", fallback.Notice!);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
    }
}
