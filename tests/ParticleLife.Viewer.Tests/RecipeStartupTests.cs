using System.Diagnostics;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;
using ParticleLife.MonoGame;

namespace ParticleLife.Viewer.Tests;

public sealed class RecipeStartupTests
{
    [Theory]
    [InlineData("core-halo", PresetKind.CoreHalo, 2)]
    [InlineData("alternating-bands", PresetKind.AlternatingBands, 2)]
    [InlineData("ribbons", PresetKind.Ribbons, 7)]
    [InlineData("cyclic-pursuit", PresetKind.CyclicPursuit, 3)]
    [InlineData("chase-cycle-5", PresetKind.ChaseCycle5, 5)]
    [InlineData("chase-cycle-6", PresetKind.ChaseCycle6, 6)]
    [InlineData("species-islands", PresetKind.SpeciesIslands, 6)]
    [InlineData("biased-ribbons", PresetKind.BiasedRibbons, 7)]
    public async Task NativeAndCliRecipesSelectTheSameCompleteStartingExperiment(string key, PresetKind kind, int species)
    {
        ViewerOptions options = ViewerOptions.Parse(["--preset", key]);
        Assert.Equal(kind, options.Preset);
        Assert.Equal(420, options.Particles);
        Assert.Equal(species, options.Species);
        ExperimentDefinition native = options.CreateDefaultExperiment();
        Assert.Equal(ForceBackend.UniformGrid, native.Backend);
        Assert.Equal(420, native.InitialState.Count);
        Assert.Equal(species, native.Matrix.SpeciesCount);
        int[] counts = Enumerable.Range(0, species)
            .Select(index => native.InitialState.Species.ToArray().Count(value => value == index)).ToArray();
        Assert.Equal(kind == PresetKind.CoreHalo ? [84, 336] : Enumerable.Repeat(420 / species, species).ToArray(), counts);

        string directory = NewDirectory();
        try
        {
            string path = Path.Combine(directory, "recipe.json");
            (int exitCode, string output) = await RunCli("create", "--preset", key, "--output", path);
            Assert.True(exitCode == 0, output);
            ExperimentDefinition cli = ExperimentFile.Load(path);
            Assert.Equal(ExperimentFile.Serialize(native), ExperimentFile.Serialize(cli));
            Assert.Equal(ExperimentFile.Serialize(cli), ExperimentFile.Serialize(ExperimentFile.Deserialize(ExperimentFile.Serialize(cli))));
            if (kind == PresetKind.BiasedRibbons)
            {
                Assert.Contains("Experimental", output);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExplicitCountsAreIndependentOfOptionOrderAndRetainTheHaloRatio()
    {
        ViewerOptions first = ViewerOptions.Parse(["--particles", "7", "--species", "2", "--preset", "core-halo", "--seed", "23"]);
        ViewerOptions second = ViewerOptions.Parse(["--preset", "core-halo", "--seed", "23", "--species", "2", "--particles", "7"]);
        Assert.Equal(first, second);
        ExperimentDefinition expected = first.CreateDefaultExperiment();
        Assert.Equal(7, expected.InitialState.Count);
        Assert.Equal(1, expected.InitialState.Species.ToArray().Count(species => species == 0));
        Assert.Equal(6, expected.InitialState.Species.ToArray().Count(species => species == 1));
        string directory = NewDirectory();
        try
        {
            string path = Path.Combine(directory, "override.json");
            (int exitCode, string output) = await RunCli("create", "--particles", "7", "--species", "2", "--seed", "23",
                "--preset", "core-halo", "--output", path);
            Assert.True(exitCode == 0, output);
            Assert.Equal(ExperimentFile.Serialize(expected), ExperimentFile.Serialize(ExperimentFile.Load(path)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ExplicitCompatibleSpeciesCountCanExploreBeyondTheRecommendedRibbonCount()
    {
        ViewerOptions options = ViewerOptions.Parse(["--species", "5", "--preset", "ribbons", "--particles", "17"]);
        ExperimentDefinition generated = options.CreateDefaultExperiment();
        Assert.Equal(5, generated.Matrix.SpeciesCount);
        Assert.Equal(17, generated.InitialState.Count);
        Assert.Equal(new[] { 4, 4, 3, 3, 3 }, Enumerable.Range(0, 5)
            .Select(index => generated.InitialState.Species.ToArray().Count(value => value == index)).ToArray());
        Assert.Equal(0.8, generated.Matrix[0, 4]);
        Assert.Equal(-0.5, generated.Matrix[0, 2]);
    }

    [Fact]
    public async Task UnsupportedRecipeDimensionsFailAtGenerationWithoutPublishingAnOutput()
    {
        ViewerOptions options = ViewerOptions.Parse(["--preset", "core-halo", "--species", "3"]);
        Assert.ThrowsAny<ArgumentException>(() => options.CreateDefaultExperiment());
        string directory = NewDirectory();
        try
        {
            string path = Path.Combine(directory, "invalid.json");
            (int exitCode, _) = await RunCli("create", "--preset", "core-halo", "--species", "3", "--output", path);
            Assert.Equal(1, exitCode);
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SavedStartupInputCanBypassIncompatibleUnusedRecipeDimensions()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "two-species.json");
        ViewerOptions options = ViewerOptions.Parse([path, "--preset", "core-halo", "--species", "50000"]);
        Assert.Equal(Path.GetFullPath(path), options.InputPath);
        Assert.Equal(50000, options.Species);
        ExperimentEditor loaded = ExperimentEditor.CreateFromFile(options.InputPath!, options.BackendOverride, options.WorkerOverride);
        using PlaybackController controller = loaded.Playback;
        Assert.Equal(2, loaded.Configuration.Matrix.SpeciesCount);
        Assert.Equal(ExperimentFile.Serialize(ExperimentFile.Load(path)), ExperimentFile.Serialize(controller.Experiment));
    }

    [Fact]
    public void HistoricalDefaultsRemainUnchanged()
    {
        ViewerOptions options = ViewerOptions.Parse([]);
        Assert.Equal(PresetKind.Random, options.Preset);
        Assert.Equal(500, options.Particles);
        Assert.Equal(7, options.Species);
        Assert.Equal(ExperimentFile.Serialize(PresetGenerator.Create(PresetKind.Random, 500, 7, 1, backend: ForceBackend.UniformGrid)),
            ExperimentFile.Serialize(options.CreateDefaultExperiment()));
    }

    [Theory]
    [InlineData("CoreHalo")]
    [InlineData("CORE-HALO")]
    [InlineData("1")]
    [InlineData("unknown")]
    public void NativeStartupRejectsNonCatalogKeys(string key) =>
        Assert.Throws<ArgumentException>(() => ViewerOptions.Parse(["--preset", key]));

    private static string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"particlelife-recipe-startup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<(int ExitCode, string Output)> RunCli(params string[] arguments)
    {
        string repository = FindRepository();
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        string assembly = Path.Combine(repository, "src", "ParticleLife.Cli", "bin", configuration, "net10.0", "ParticleLife.Cli.dll");
        Assert.True(File.Exists(assembly), $"Build the solution first; missing CLI assembly: {assembly}");
        ProcessStartInfo start = new("dotnet")
        {
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(assembly);
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the CLI recipe process.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        return (process.ExitCode, await standardOutput + "\n" + await standardError);
    }

    private static string FindRepository()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ParticleLife.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("Could not locate the repository containing the test output.");
    }
}
