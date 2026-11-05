using ParticleLife.Core.Experiments;
using ParticleLife.Viewer;

namespace ParticleLife.Viewer.Tests;

public sealed class DiscoveryScannerTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "particle-life-scan-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void FullSearchFindsLateFilesWhileRetentionIsBoundedAndSorted()
    {
        PrepareFiles();
        IReadOnlyList<DiscoveryEntry> entries = DiscoveryLibrary.Read(directory, recentFirst: false, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(DiscoveryLibrary.MaximumEntries, entries.Count);
        Assert.EndsWith("item-000.json", entries[0].Path);
        Assert.Single(DiscoveryLibrary.Read(directory, "item-304", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NewestScanSupersedesQueuedQueriesAndObsoleteErrors()
    {
        PrepareFiles();
        string notAFolder = Path.Combine(directory, "file-not-folder");
        File.WriteAllText(notAFolder, "ordinary file");
        using DiscoveryLibraryScanner scanner = new();
        scanner.Request(notAFolder, "old failing request");
        scanner.Request(directory, "old query");
        scanner.Request(directory, "item-304");
        DiscoveryLibraryScan? result = null;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (result is null)
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (scanner.Poll(out DiscoveryLibraryScan? ready, out Exception? error))
            {
                Assert.Null(error); result = ready;
            }
            if (result is null) { await Task.Delay(1, timeout.Token); }
        }
        Assert.Equal("item-304", result.Query);
        Assert.Single(result.Entries);
    }

    [Fact]
    public void CancelledReadDoesNotCreateOrEnumerateTheRequestedFolder()
    {
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => DiscoveryLibrary.Read(directory, cancellationToken: cancellation.Token));
        Assert.False(Directory.Exists(directory));
    }

    private void PrepareFiles()
    {
        Directory.CreateDirectory(directory);
        string scientific = ExperimentFile.Serialize(PresetGenerator.Create(PresetKind.Random, 1, 1, 21));
        for (int i = 0; i < 305; i++) { File.WriteAllText(Path.Combine(directory, $"item-{i:000}.json"), scientific); }
    }
    public void Dispose() { if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); } }
}
