using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using ParticleLife.Viewer;

namespace ParticleLife.Viewer.Tests;

/// <summary>Exercises the same editor used by the native host, serialized sessions and the real CLI process.</summary>
public sealed class SessionCliParityTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(ForceBackend.Direct, 1)]
    [InlineData(ForceBackend.UniformGrid, 1)]
    [InlineData(ForceBackend.UniformGrid, 4)]
    public async Task LiveEditorSessionsReplayThroughActualCli(ForceBackend backend, int workers)
    {
        string directory = CreateDirectory();
        try
        {
            ExperimentDefinition initial = PresetGenerator.Create(PresetKind.Random, 24, 3, 761,
                timeStep: 0.004, backend: backend, workerCount: workers);
            using PlaybackController playback = new(initial, diagnosticEverySteps: 0);
            ExperimentEditor editor = new(playback);
            editor.ApplyConfiguration(Change(editor.Configuration, parameters: initial.Parameters with { Cutoff = 0.0625 }));
            editor.SetMatrixCoefficient(0, 1, -0.7);
            Step(playback, 6);
            editor.BeginGesture("Directed coefficient drag");
            foreach (double coefficient in new[] { 0.2, 0.4, 0.6 })
            {
                editor.SetMatrixCoefficient(0, 1, coefficient);
                Step(playback, 1);
            }

            editor.EndGesture();
            editor.Undo();
            Step(playback, 1);
            editor.Redo();
            editor.ApplyConfiguration(Change(editor.Configuration, timeStep: 0.002,
                parameters: editor.Configuration.Parameters with { Cutoff = Math.BitIncrement(0.125), DampingHalfLife = null }));
            Step(playback, 4);
            editor.SetGeneration(new GenerationSettings
            {
                Seed = 118,
                Placement = PlacementKind.Disk,
                Velocity = VelocityKind.RandomDirection,
                Speed = 0.03
            });
            editor.ApplyPopulation(4, [4, 5, 6, 8]);
            Step(playback, 3);
            editor.ApplyPopulation(2, [6, 7]);
            Step(playback, 1);
            editor.Undo();
            Step(playback, 1);
            editor.Redo();
            editor.ApplyConfiguration(Change(editor.Configuration,
                parameters: editor.Configuration.Parameters with { WorldWidth = 1.5, WorldHeight = 0.9, WallWidth = 0.1 }));
            editor.ApplyMatrixTemplate(PresetKind.Chains, 31);
            Step(playback, 5);
            editor.ResampleParticles();
            Step(playback, 2);
            editor.ApplyConfiguration(Change(editor.Configuration, backend: ForceBackend.UniformGrid, workers: 4));
            Step(playback, 2);
            editor.ApplyConfiguration(Change(editor.Configuration, backend: backend, workers: workers));
            Step(playback, 2);

            // Terminal edits and multiple events at one boundary must survive with no extra step.
            editor.SetMatrixCoefficient(1, 0, -0.23);
            editor.ApplyConfiguration(Change(editor.Configuration, timeStep: 0.003));
            ParticleSnapshot expected = Capture(playback);
            string sessionPath = Path.Combine(directory, "edited.session.json");
            string reportPath = Path.Combine(directory, "report.json");
            editor.SaveSession(sessionPath, "CLI parity");
            AssertState(expected, playback.Observe());
            SessionDefinition saved = SessionFile.Load(sessionPath);
            Assert.Equal(expected.Step, saved.TerminalStep);
            Assert.True(saved.Events.Count > 15);
            Assert.Equal(expected.Step, saved.Events[^1].BoundaryStep);
            Assert.Contains(saved.Events, change => change.ReplacementState is not null);
            Assert.Contains(saved.Events.Zip(saved.Events.Skip(1)), pair => pair.First.BoundaryStep == pair.Second.BoundaryStep);

            await Cli("replay", sessionPath, "--sample-every", "3", "--output", reportPath);
            using JsonDocument report = JsonDocument.Parse(File.ReadAllText(reportPath));
            double maximumError = AssertReportState(report.RootElement.GetProperty("finalState"), expected.Observe());
            Assert.Equal(saved.Events.Count, report.RootElement.GetProperty("replay").GetProperty("appliedEventCount").GetInt32());
            Assert.Equal(0.003, report.RootElement.GetProperty("execution").GetProperty("currentTimeStep").GetDouble());
            Assert.Equal(JsonValueKind.Null, report.RootElement.GetProperty("replay").GetProperty("backendOverride").ValueKind);
            Assert.True(report.RootElement.GetProperty("execution").GetProperty("segments").GetArrayLength() >= 2);
            output.WriteLine($"Edited {backend}/{workers}: {saved.Events.Count} events, {expected.Step} steps; max state error {maximumError:R}; 1e-12 absolute + 1e-12 relative reduced-unit tolerance.");

            using PlaybackController loaded = new(initial);
            ExperimentEditor loadedEditor = new(loaded);
            loadedEditor.LoadSession(sessionPath);
            Assert.True(loaded.IsPaused);
            AssertState(expected, loaded.Observe());
            Assert.True(editor.Configuration.EquivalentTo(loadedEditor.Configuration));
            Step(loaded, 4);
            string continuedPath = Path.Combine(directory, "continued.json");
            await Cli("replay", sessionPath, "--continue-steps", "4", "--sample-every", "0", "--output", continuedPath);
            using JsonDocument continued = JsonDocument.Parse(File.ReadAllText(continuedPath));
            _ = AssertReportState(continued.RootElement.GetProperty("finalState"), loaded.Observe());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReplayReportsExplicitOverridesAndCumulativeFallbackAcrossTerminalEdits()
    {
        string directory = CreateDirectory();
        try
        {
            ExperimentDefinition initial = new("fallback session", new ModelParameters { ForceScale = 0, WallStrength = 0, DampingHalfLife = null },
                new InteractionMatrix(1, [0]), new ParticleSnapshot([9], [new(1e200, 0)], [default], [0]), 0.001,
                new GeneratorMetadata("exact", "1", null, "test"), ForceBackend.UniformGrid, 4);
            using PlaybackController playback = new(initial, diagnosticEverySteps: 0);
            ExperimentEditor editor = new(playback);
            Step(playback, 2);
            editor.ApplyConfiguration(Change(editor.Configuration, timeStep: 0.002));
            Step(playback, 2);
            editor.SetMatrixCoefficient(0, 0, 0.5);
            string input = Path.Combine(directory, "fallback.session.json");
            editor.SaveSession(input, "fallback");
            foreach (bool useOverride in new[] { false, true })
            {
                string reportPath = Path.Combine(directory, useOverride ? "override.json" : "saved.json");
                List<string> arguments = ["replay", input, "--sample-every", "0", "--output", reportPath];
                if (useOverride)
                {
                    arguments.AddRange(["--backend", "direct"]);
                }

                await Cli(arguments.ToArray());
                using JsonDocument report = JsonDocument.Parse(File.ReadAllText(reportPath));
                JsonElement execution = report.RootElement.GetProperty("execution");
                Assert.Equal(useOverride ? 0 : 4, execution.GetProperty("directFallbackSteps").GetInt64());
                Assert.Equal(useOverride ? "direct/1" : null,
                    report.RootElement.GetProperty("replay").GetProperty("backendOverride").GetString());
                Assert.Equal("uniform-grid/1", report.RootElement.GetProperty("session").GetProperty("initialExperiment")
                    .GetProperty("neighbourSearchVersion").GetString());
                Assert.Equal(useOverride ? "None" : "CoordinateRange", execution.GetProperty("segments")[0].GetProperty("fallbackReason").GetString());
                _ = AssertReportState(report.RootElement.GetProperty("finalState"), playback.Observe());
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ZeroStepTerminalSessionAndMalformedInputsUseRealCliFailurePath()
    {
        string directory = CreateDirectory();
        try
        {
            using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 0, 2, 7));
            ExperimentEditor editor = new(playback);
            editor.SetMatrixCoefficient(0, 1, 0.25);
            editor.SetMatrixCoefficient(1, 0, -0.75);
            string input = Path.Combine(directory, "zero.session.json");
            editor.SaveSession(input, "zero");
            await Cli("validate-session", input);
            string reportPath = Path.Combine(directory, "zero-report.json");
            await Cli("replay", input, "--output", reportPath);
            using JsonDocument report = JsonDocument.Parse(File.ReadAllText(reportPath));
            Assert.Equal(0, report.RootElement.GetProperty("finalState").GetProperty("step").GetInt64());
            Assert.Equal(2, report.RootElement.GetProperty("replay").GetProperty("appliedEventCount").GetInt32());
            Assert.Equal(0, report.RootElement.GetProperty("execution").GetProperty("segments").GetArrayLength());
            await CliFailure("replay", input, "--output", reportPath);
            await CliFailure("replay", reportPath);
            await CliFailure("replay", input, "--workers", "4", "--backend", "direct");
            string malformed = Path.Combine(directory, "invalid.json");
            string original = File.ReadAllText(input);
            JsonObject node = JsonNode.Parse(original)!.AsObject();
            node["events"]![0]!["boundaryStep"] = 1;
            File.WriteAllText(malformed, node.ToJsonString());
            await CliFailure("replay", malformed);
            File.WriteAllText(malformed, original.Replace("\"terminalStep\": 0", "\"terminalStep\": 0, \"terminalStep\": 0", StringComparison.Ordinal));
            await CliFailure("replay", malformed);
            File.WriteAllText(malformed, original.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99", StringComparison.Ordinal));
            await CliFailure("replay", malformed);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static SimulationConfiguration Change(SimulationConfiguration current, ModelParameters? parameters = null,
        double? timeStep = null, ForceBackend? backend = null, int? workers = null) =>
        new(parameters ?? current.Parameters, current.Matrix, timeStep ?? current.TimeStep,
            backend ?? current.Backend, workers ?? current.WorkerCount);

    private static void Step(PlaybackController playback, int count)
    {
        for (int i = 0; i < count; i++)
        {
            playback.SingleStep();
        }
    }

    private static ParticleSnapshot Capture(PlaybackController playback)
    {
        ParticleStateView state = playback.Observe();
        return new(state.Ids, state.Positions, state.Velocities, state.Species, state.Time, state.Step);
    }

    private static void AssertState(ParticleSnapshot expected, ParticleStateView actual)
    {
        Assert.Equal(expected.Ids.ToArray(), actual.Ids.ToArray());
        Assert.Equal(expected.Positions.ToArray(), actual.Positions.ToArray());
        Assert.Equal(expected.Velocities.ToArray(), actual.Velocities.ToArray());
        Assert.Equal(expected.Species.ToArray(), actual.Species.ToArray());
        Assert.Equal(expected.Time, actual.Time);
        Assert.Equal(expected.Step, actual.Step);
    }

    private static double AssertReportState(JsonElement expected, ParticleStateView actual)
    {
        long[] ids = expected.GetProperty("ids").EnumerateArray().Select(value => value.GetInt64()).ToArray();
        Assert.Equal(ids.Length, actual.Count);
        Assert.Equal(expected.GetProperty("step").GetInt64(), actual.Step);
        double maximumError = Close(expected.GetProperty("time").GetDouble(), actual.Time);
        for (int i = 0; i < ids.Length; i++)
        {
            int index = actual.Ids.IndexOf(ids[i]);
            Assert.True(index >= 0);
            Assert.Equal(expected.GetProperty("species")[i].GetInt32(), actual.Species[index]);
            maximumError = Math.Max(maximumError, Close(expected.GetProperty("positions")[i].GetProperty("x").GetDouble(), actual.Positions[index].X));
            maximumError = Math.Max(maximumError, Close(expected.GetProperty("positions")[i].GetProperty("y").GetDouble(), actual.Positions[index].Y));
            maximumError = Math.Max(maximumError, Close(expected.GetProperty("velocities")[i].GetProperty("x").GetDouble(), actual.Velocities[index].X));
            maximumError = Math.Max(maximumError, Close(expected.GetProperty("velocities")[i].GetProperty("y").GetDouble(), actual.Velocities[index].Y));
        }

        return maximumError;
    }

    private static double Close(double expected, double actual)
    {
        // Short matched-backend trajectories in reduced units permit tiny runtime roundoff only.
        double error = Math.Abs(expected - actual);
        double tolerance = 1e-12 + 1e-12 * Math.Max(Math.Abs(expected), Math.Abs(actual));
        Assert.True(double.IsFinite(actual) && error <= tolerance, $"Expected {expected:R}, actual {actual:R}; tolerance {tolerance:R}.");
        return error;
    }

    private static Task Cli(params string[] arguments) => RunCli(arguments, 0);
    private static Task CliFailure(params string[] arguments) => RunCli(arguments, 1);

    private static async Task RunCli(string[] arguments, int expectedExit)
    {
        string repository = FindRepository();
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        ProcessStartInfo start = new("dotnet")
        {
            WorkingDirectory = repository,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(repository, "src", "ParticleLife.Cli", "bin", configuration, "net10.0", "ParticleLife.Cli.dll"));
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("CLI did not start.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(45));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw new TimeoutException("Session CLI exceeded 45 seconds.");
        }

        string error = await stderr;
        Assert.True(process.ExitCode == expectedExit, $"Expected exit {expectedExit}, got {process.ExitCode}: {await stdout}\n{error}");
        if (expectedExit != 0)
        {
            Assert.StartsWith("Error:", error, StringComparison.Ordinal);
            Assert.DoesNotContain("Unhandled exception", error, StringComparison.Ordinal);
        }
    }

    private static string CreateDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "particlelife-session-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
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

        throw new DirectoryNotFoundException("Cannot find repository from test output.");
    }
}
