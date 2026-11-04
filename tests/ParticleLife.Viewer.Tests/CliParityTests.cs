using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.State;
using ParticleLife.Viewer;

namespace ParticleLife.Viewer.Tests;

public sealed class CliParityTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("saved-fixture")]
    [InlineData("random")]
    [InlineData("chains")]
    [InlineData("snakes")]
    [InlineData("resampled")]
    [InlineData("new-matrix")]
    [InlineData("off-world")]
    public async Task SavedExperimentMatchesActualCliThroughViewerController(string scenario)
    {
        string repository = FindRepository();
        string directory = Path.Combine(Path.GetTempPath(), $"particlelife-parity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            ExperimentDefinition original = CreateExperiment(scenario, repository);
            string inputPath = Path.Combine(directory, "experiment.json");
            string reportPath = Path.Combine(directory, "run.json");
            ExperimentStorage.SaveNew(inputPath, original);
            ExperimentDefinition saved = ExperimentFile.Load(inputPath);
            Assert.Equal(ExperimentFile.Serialize(original), ExperimentFile.Serialize(saved));

            PlaybackController controller = new(saved, diagnosticEverySteps: 7);
            ExercisePlayback(controller, 120);
            await RunCli(repository, inputPath, reportPath, 120);

            using JsonDocument report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken));
            JsonElement final = report.RootElement.GetProperty("finalState");
            Assert.Equal(120, final.GetProperty("step").GetInt64());
            Assert.Equal(120, controller.Observe().Step);
            double maximumError = CompareByIdentity(final, controller.Observe());
            output.WriteLine($"{scenario}: 120 completed steps, {saved.InitialState.Count} particles; " +
                $"maximum absolute position/velocity/time difference = {maximumError:R}; " +
                "tolerance = 1e-12 absolute + 1e-12 relative in reduced units.");

            // Reports deliberately describe results, not a replacement experiment or a checkpoint.
            Assert.Throws<JsonException>(() => ExperimentFile.Load(reportPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ExperimentDefinition CreateExperiment(string scenario, string repository)
    {
        ExperimentDefinition baseline = PresetGenerator.Create(PresetKind.Random, 37, 4, 1729);
        return scenario switch
        {
            "saved-fixture" => ExperimentFile.Load(Path.Combine(repository, "examples", "experiments", "two-species.json")),
            "random" => baseline,
            "chains" => PresetGenerator.Create(PresetKind.Chains, 37, 4, 1729),
            "snakes" => PresetGenerator.Create(PresetKind.Snakes, 37, 4, 1729),
            "resampled" => ExperimentEditing.ResampleState(baseline, 90210),
            "new-matrix" => ExperimentEditing.RandomizeMatrix(baseline, 97531),
            "off-world" => new ExperimentDefinition("off-world-parity",
                new ModelParameters { WorldWidth = 2, WorldHeight = 0.5, DampingHalfLife = null },
                new InteractionMatrix(2, [0.75, -0.25, 0.5, 0.1]),
                new ParticleSnapshot([91, -7, 405, 12],
                    [new(-0.1, 0.25), new(2.1, 0.25), new(0.5, -0.05), new(0.53, -0.02)],
                    [new(-0.01, 0.02), new(0.01, -0.02), new(0.02, 0.01), new(-0.01, 0.02)],
                    [0, 1, 0, 1]),
                1d / 128, new GeneratorMetadata("manual-off-world", "1", null, "explicit test values")),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
    }

    /// <summary>
    /// Drives the same public controller used by MonoGame through fractional frames, pauses,
    /// a reset and single steps. Observations are consumed before the next mutation.
    /// </summary>
    private static void ExercisePlayback(PlaybackController controller, int steps)
    {
        double timeStep = controller.Experiment.TimeStep;
        Assert.Equal(0, controller.AdvanceFrame(0.75 * timeStep));
        controller.SetPaused(true);
        Assert.Equal(0, controller.AdvanceFrame(600));
        controller.SetPaused(false);
        Assert.Equal(0, controller.AdvanceFrame(0.3 * timeStep));
        controller.SingleStep();
        Assert.True(controller.IsPaused);
        Assert.Equal(1, controller.Observe().Step);
        controller.Reset();
        Assert.Equal(0, controller.Observe().Step);
        Assert.Equal(0, controller.Observe().Time);
        controller.SetPaused(false);

        double[] frameLengths = [0.25, 1.5, 0, 0.75, 3.125, 0.125, 2.25];
        int frames = 0;
        while (controller.Observe().Step < steps - 8)
        {
            Assert.True(frames < 500, "Playback controller made no progress through positive frame durations.");
            controller.AdvanceFrame(frameLengths[frames++ % frameLengths.Length] * timeStep);
        }

        Assert.True(frames > 20, "Parity must exercise frame scheduling, not only single stepping.");
        controller.SetPaused(true);
        long beforePausedFrame = controller.Observe().Step;
        Assert.Equal(0, controller.AdvanceFrame(600));
        Assert.Equal(beforePausedFrame, controller.Observe().Step);
        while (controller.Observe().Step < steps)
        {
            controller.SingleStep();
        }

        Assert.Equal(steps, controller.Observe().Step);
    }

    private static double CompareByIdentity(JsonElement expected, ParticleStateView actual)
    {
        long[] ids = expected.GetProperty("ids").EnumerateArray().Select(value => value.GetInt64()).ToArray();
        Assert.Equal(ids.Length, actual.Count);
        Assert.Equal(expected.GetProperty("step").GetInt64(), actual.Step);
        double maximumError = Close(expected.GetProperty("time").GetDouble(), actual.Time);
        JsonElement positions = expected.GetProperty("positions");
        JsonElement velocities = expected.GetProperty("velocities");
        JsonElement species = expected.GetProperty("species");
        Assert.Equal(ids.Length, ids.Distinct().Count());
        for (int i = 0; i < ids.Length; i++)
        {
            int matchingIndex = actual.Ids.IndexOf(ids[i]);
            Assert.True(matchingIndex >= 0, $"Viewer lost particle identity {ids[i]}.");
            Assert.Equal(species[i].GetInt32(), actual.Species[matchingIndex]);
            maximumError = Math.Max(maximumError, Close(positions[i].GetProperty("x").GetDouble(), actual.Positions[matchingIndex].X));
            maximumError = Math.Max(maximumError, Close(positions[i].GetProperty("y").GetDouble(), actual.Positions[matchingIndex].Y));
            maximumError = Math.Max(maximumError, Close(velocities[i].GetProperty("x").GetDouble(), actual.Velocities[matchingIndex].X));
            maximumError = Math.Max(maximumError, Close(velocities[i].GetProperty("y").GetDouble(), actual.Velocities[matchingIndex].Y));
        }

        return maximumError;
    }

    private static double Close(double expected, double actual)
    {
        // These short double-precision cases use positions/speeds of order one in reduced units.
        // Allow tiny platform roundoff while remaining far below a missed step or changed equation.
        const double absoluteTolerance = 1e-12;
        const double relativeTolerance = 1e-12;
        double error = Math.Abs(expected - actual);
        double tolerance = absoluteTolerance + relativeTolerance * Math.Max(Math.Abs(expected), Math.Abs(actual));
        Assert.True(double.IsFinite(expected) && double.IsFinite(actual) && error <= tolerance,
            $"Expected {expected:R}, actual {actual:R}; absolute error {error:R}, tolerance {tolerance:R}.");
        return error;
    }

    private static async Task RunCli(string repository, string inputPath, string reportPath, int steps)
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        string executable = Path.Combine(repository, "src", "ParticleLife.Cli", "bin", configuration, "net10.0", "ParticleLife.Cli.dll");
        Assert.True(File.Exists(executable), $"Build the solution first; missing CLI assembly: {executable}");
        ProcessStartInfo start = new("dotnet")
        {
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in new[] { executable, "run", inputPath, "--steps", steps.ToString(CultureInfo.InvariantCulture),
            "--sample-every", "11", "--output", reportPath })
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the CLI parity process.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(45));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw new TimeoutException("The actual CLI parity run did not finish within 45 seconds.");
        }

        Assert.True(process.ExitCode == 0,
            $"CLI exited {process.ExitCode}.\n{await standardOutput}\n{await standardError}");
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
