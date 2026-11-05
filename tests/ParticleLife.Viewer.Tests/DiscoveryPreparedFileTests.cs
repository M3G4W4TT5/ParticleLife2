using ParticleLife.Core.Experiments;
using ParticleLife.Viewer;

namespace ParticleLife.Viewer.Tests;

public sealed class DiscoveryPreparedFileTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "particle-life-prepared-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PreparedSaveRetainsCapturedValuesAfterTheOwnerChangesAndIsDisposed()
    {
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 12, 2, 21));
        ExperimentEditor editor = new(playback);
        playback.SingleStep();
        var captured = editor.CaptureCurrentExperiment("captured");
        PreparedDiscoverySave prepared = editor.PrepareDiscoverySave(DiscoveryKind.Pattern, "captured");
        editor.ApplyPopulation(2, [1, 1]);
        editor.SetMatrixCoefficient(0, 1, -0.99);
        playback.Dispose();
        string path = Path.Combine(directory, "captured.json");
        DiscoveryFileOutcome outcome = await Task.Run(() => prepared.Write(path));
        Assert.Null(outcome.MetadataWarning);
        ExperimentDefinition loaded = ExperimentFile.Load(path);
        Assert.Equal(captured.InitialState.Positions.ToArray(), loaded.InitialState.Positions.ToArray());
        Assert.Equal(captured.InitialState.Ids.ToArray(), loaded.InitialState.Ids.ToArray());
        Assert.Equal(captured.Matrix.Values.ToArray(), loaded.Matrix.Values.ToArray());
        Assert.Equal(12, outcome.Entry!.Metadata.Population);
    }

    [Fact]
    public async Task PreparedLoadPerformsNoFileReadDuringOwnerPublication()
    {
        Directory.CreateDirectory(directory);
        using PlaybackController source = new(PresetGenerator.Create(PresetKind.Random, 12, 2, 21));
        ExperimentEditor sourceEditor = new(source);
        string path = Path.Combine(directory, "source.json");
        sourceEditor.SaveInitial(path, "source");
        PreparedDiscoveryLoad prepared = await Task.Run(() => ExperimentEditor.PrepareDiscoveryLoad(path, DiscoveryLoadAction.LoadState));
        File.Delete(path); File.Delete(path + ".viewer.json");
        using PlaybackController target = new(PresetGenerator.Create(PresetKind.Random, 4, 3, 99));
        ExperimentEditor targetEditor = new(target);
        target.SingleStep();
        prepared.Apply(targetEditor);
        Assert.Equal(12, target.Observe().Count);
        Assert.Equal(0, target.Observe().Step);
        Assert.Equal(source.Observe().Positions.ToArray(), target.Observe().Positions.ToArray());
        Assert.True(target.IsPaused);
    }

    [Fact]
    public async Task SuccessfulSaveWithLockedMetadataReportsSavedArtifactAndWarning()
    {
        Directory.CreateDirectory(directory);
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 12, 2, 21));
        ExperimentEditor editor = new(playback);
        PreparedDiscoverySave prepared = editor.PrepareDiscoverySave(DiscoveryKind.Initial, "saved");
        string path = Path.Combine(directory, "saved.json");
        using FileStream locked = new(path + DiscoveryLibrary.MetadataSuffix + ".save.lock", FileMode.CreateNew, FileAccess.Write, FileShare.None);
        DiscoveryFileOutcome outcome = await Task.Run(() => prepared.Write(path));
        Assert.Equal(Path.GetFullPath(path), outcome.Path);
        Assert.Contains("metadata", outcome.MetadataWarning!);
        Assert.True(File.Exists(path)); Assert.True(File.Exists(path + ".viewer.json"));
        Assert.Equal(12, ExperimentFile.Load(path).InitialState.Count);
        Assert.False(File.Exists(path + DiscoveryLibrary.MetadataSuffix));
    }

    [Fact]
    public async Task SuccessfulImportWithLockedMetadataRetainsValidByteExactArtifactAndWarning()
    {
        Directory.CreateDirectory(directory);
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 12, 2, 21));
        ExperimentEditor editor = new(playback);
        string source = Path.Combine(directory, "source.json"), archive = Path.Combine(directory, "source.plshare"), path = Path.Combine(directory, "import.json");
        editor.SaveCurrent(source, "source"); DiscoveryShare.Export(source, archive);
        using FileStream locked = new(path + DiscoveryLibrary.MetadataSuffix + ".save.lock", FileMode.CreateNew, FileAccess.Write, FileShare.None);
        DiscoveryFileOutcome outcome = await Task.Run(() => DiscoveryShare.ImportDetailed(archive, path));
        Assert.Contains("metadata", outcome.MetadataWarning!);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(path));
        Assert.Equal(File.ReadAllBytes(source + ".viewer.json"), File.ReadAllBytes(path + ".viewer.json"));
    }

    public void Dispose() { if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); } }
}
