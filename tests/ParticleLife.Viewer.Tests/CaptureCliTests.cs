using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ParticleLife.Core.Execution;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Viewer.Tests;

/// <summary>Exercises capture restoration and explicit action re-simulation through the real CPU-only CLI.</summary>
public sealed class CaptureCliTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "particlelife-capture-cli-" + Guid.NewGuid().ToString("N"));

    public CaptureCliTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task RestoreRetainsFloatBitsTerminalEditsAndClockThenContinuesOnCpu()
    {
        CapturedSessionDefinition captured = Fixture();
        string input = PathFor("capture.json");
        ExecutionCaptureFile.Save(input, captured);
        string validation = await Cli(0, "validate-session", input);
        Assert.Contains("Valid captured session structure", validation, StringComparison.Ordinal);
        Assert.Contains("--restore-capture or --resimulate-on-cpu", validation, StringComparison.Ordinal);

        string output = PathFor("restored.json");
        await Cli(0, "replay", input, "--restore-capture", "--sample-every", "0", "--output", output);
        using (JsonDocument report = JsonDocument.Parse(File.ReadAllText(output)))
        {
            JsonElement root = report.RootElement;
            AssertState(captured.TerminalCapture.State, root.GetProperty("finalState"));
            JsonElement mode = root.GetProperty("captureReplay");
            Assert.Equal("restore-capture", mode.GetProperty("mode").GetString());
            Assert.True(mode.GetProperty("restoredValuesExactly").GetBoolean());
            Assert.False(mode.GetProperty("actionsResimulated").GetBoolean());
            Assert.Equal("gpu", mode.GetProperty("sourceExecution").GetProperty("hardware").GetString());
            Assert.Equal("float32", mode.GetProperty("sourceExecution").GetProperty("precision").GetString());
            Assert.Equal("fixture GPU", mode.GetProperty("sourceExecution").GetProperty("device").GetString());
            Assert.Equal("cpu", mode.GetProperty("selectedExecution").GetProperty("hardware").GetString());
            Assert.Equal("float64", mode.GetProperty("selectedExecution").GetProperty("precision").GetString());
            Assert.Equal(captured.TerminalCapture.State.Time, mode.GetProperty("sourceTerminalTime").GetDouble());
            Assert.Equal(captured.TerminalCapture.State.Step, mode.GetProperty("sourceTerminalStep").GetInt64());
            Assert.Equal(captured.Actions.Events.Count, mode.GetProperty("sourceAppliedEventCount").GetInt32());
            Assert.Equal(0, root.GetProperty("replay").GetProperty("appliedEventCount").GetInt32());
            Assert.True(captured.TerminalCapture.Configuration.EquivalentTo(
                ConfigurationFile.Decode(root.GetProperty("execution").GetProperty("currentConfiguration"))));
            Assert.Equal(ExecutionCaptureFile.Format, root.GetProperty("capturedSession").GetProperty("format").GetString());
        }

        SimulationCapture terminal = captured.TerminalCapture;
        using Simulation continued = new(terminal.State, terminal.Configuration.Parameters, terminal.Configuration.Matrix,
            terminal.Configuration.TimeStep, terminal.Configuration.Backend, terminal.Configuration.WorkerCount);
        continued.Step();
        continued.Step();
        string continuedOutput = PathFor("continued.json");
        await Cli(0, "replay", input, "--restore-capture", "--continue-steps", "2", "--sample-every", "0", "--output", continuedOutput);
        using JsonDocument continuedReport = JsonDocument.Parse(File.ReadAllText(continuedOutput));
        AssertState(continued.CaptureSnapshot(), continuedReport.RootElement.GetProperty("finalState"));
        Assert.Equal(terminal.State.Time, continuedReport.RootElement.GetProperty("replay").GetProperty("terminalTime").GetDouble());
        Assert.Equal(2, continuedReport.RootElement.GetProperty("replay").GetProperty("continueSteps").GetInt32());
    }

    [Fact]
    public async Task ExplicitCpuResimulationUsesSharedDeltaReplayAndRetainsSourceProvenance()
    {
        CapturedSessionDefinition captured = Fixture();
        string input = PathFor("capture.json");
        ExecutionCaptureFile.Save(input, captured);
        ReplayOverrides overrides = new(ForceBackend.Direct, 1);
        using Simulation expected = SessionReplay.Replay(captured.Actions, overrides);
        Assert.NotEqual(captured.TerminalCapture.State.Positions[0].X, expected.Observe().Positions[0].X);
        Assert.Equal(0.025, expected.TimeStep);
        expected.Step();

        string output = PathFor("resimulated.json");
        await Cli(0, "replay", input, "--resimulate-on-cpu", "--backend", "direct", "--workers", "1",
            "--continue-steps", "1", "--sample-every", "0", "--output", output);
        using JsonDocument report = JsonDocument.Parse(File.ReadAllText(output));
        JsonElement root = report.RootElement;
        AssertState(expected.CaptureSnapshot(), root.GetProperty("finalState"));
        Assert.Equal(captured.Actions.Events.Count, root.GetProperty("replay").GetProperty("appliedEventCount").GetInt32());
        Assert.Equal("direct/1", root.GetProperty("replay").GetProperty("backendOverride").GetString());
        Assert.Equal(1, root.GetProperty("replay").GetProperty("workerCountOverride").GetInt32());
        Assert.Equal("resimulate-on-cpu", root.GetProperty("captureReplay").GetProperty("mode").GetString());
        Assert.False(root.GetProperty("captureReplay").GetProperty("restoredValuesExactly").GetBoolean());
        Assert.True(root.GetProperty("captureReplay").GetProperty("actionsResimulated").GetBoolean());
        Assert.Contains("not promised", root.GetProperty("captureReplay").GetProperty("policy").GetString()!, StringComparison.Ordinal);
        Assert.Equal("float32", root.GetProperty("captureReplay").GetProperty("sourceExecution").GetProperty("precision").GetString());
        Assert.Equal("cpu", root.GetProperty("captureReplay").GetProperty("selectedExecution").GetProperty("hardware").GetString());
        Assert.Equal(3, root.GetProperty("session").GetProperty("schemaVersion").GetInt32());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("session").GetProperty("events")[0].GetProperty("stateDelta").ValueKind);
    }

    [Fact]
    public async Task AmbiguousModesInvalidCapturesAndExistingOutputsFailClearlyWithoutOverwrite()
    {
        CapturedSessionDefinition captured = Fixture();
        string input = PathFor("capture.json");
        ExecutionCaptureFile.Save(input, captured);
        string output = PathFor("must-not-exist.json");
        string ambiguity = await Cli(1, "replay", input, "--output", output);
        Assert.Contains("--restore-capture or --resimulate-on-cpu", ambiguity, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
        await Cli(1, "replay", input, "--restore-capture", "--resimulate-on-cpu");
        await Cli(1, "replay", input, "--restore-capture", "--restore-capture");
        await Cli(1, "replay", input, "--restore-capture", "--steps", "1");
        await Cli(1, "run", input, "--steps", "0");

        string actions = PathFor("actions.json");
        SessionFile.Save(actions, captured.Actions);
        await Cli(1, "replay", actions, "--restore-capture");
        await Cli(1, "replay", actions, "--resimulate-on-cpu");

        string malformed = PathFor("invalid.json");
        JsonObject node = JsonNode.Parse(File.ReadAllText(input))!.AsObject();
        node["captures"] = new JsonArray();
        File.WriteAllText(malformed, node.ToJsonString());
        await Cli(1, "validate-session", malformed);
        await Cli(1, "replay", malformed, "--restore-capture", "--output", output);
        Assert.False(File.Exists(output));

        byte[] original = File.ReadAllBytes(input);
        await Cli(1, "replay", input, "--restore-capture", "--output", input);
        Assert.Equal(original, File.ReadAllBytes(input));
        File.WriteAllText(output, "existing output must remain intact");
        foreach (string mode in new[] { "--restore-capture", "--resimulate-on-cpu" })
        {
            string failure = await Cli(1, "replay", input, mode, "--output", output);
            Assert.Contains("Output already exists", failure, StringComparison.Ordinal);
            Assert.Equal("existing output must remain intact", File.ReadAllText(output));
        }
    }

    private static CapturedSessionDefinition Fixture()
    {
        ModelParameters parameters = new() { ForceScale = 0, WallStrength = 0, DampingHalfLife = null };
        InteractionMatrix matrix = new(2, [0, 0, 0, 0]);
        ExperimentDefinition initial = new("capture actions", parameters, matrix,
            new ParticleSnapshot([42, 9001], [new(0.1, 0.2), new(0.7, 0.8)], [new(0.1, 0), default], [0, 1]),
            0.01, new GeneratorMetadata("actual fixture", "1", null, "test"), ForceBackend.UniformGrid);
        SimulationConfiguration deltaConfiguration = new(parameters, matrix, 0.01, ForceBackend.UniformGrid);
        SimulationConfiguration terminalConfiguration = new(parameters with { Cutoff = 0.2 },
            new InteractionMatrix(2, [0.75, -0.2, 0.1, -0.4]), 0.025, ForceBackend.UniformGrid);
        double time = initial.TimeStep + initial.TimeStep;
        ParticleSnapshot empty = new([], [], [], [], time, 2);
        ParticleStateDelta delta = new([9001], empty,
            new ParticleSnapshot([777], [new(0.3, 0.4)], [new(0.02, -0.01)], [1], time, 2));
        SessionDefinition actions = new(initial,
            [new SessionEvent(2, deltaConfiguration, null, "terminal removal and addition", stateDelta: delta),
             new SessionEvent(2, terminalConfiguration, null, "terminal rules and timestep")], 2);
        ExecutionProvenance gpu = new(ExecutionHardware.Gpu, StatePrecision.Float32,
            "fixture-gpu/1", "fixture compiler", "fixture GPU", "fixture API", "fixture driver", false);
        ParticleSnapshot actual = new([42, 777],
            [new((double)MathF.BitIncrement(0.1f), -0f), new(0.375f, 0.625f)],
            [new(MathF.BitIncrement(1f), -float.Epsilon), new(-0f, 0.125f)], [0, 1], time, 2);
        return new(actions, [new SimulationCapture(terminalConfiguration, actual, gpu, actions.Events.Count, "terminal")], []);
    }

    private static void AssertState(ParticleSnapshot expected, JsonElement state)
    {
        EqualBits(expected.Time, state.GetProperty("time").GetDouble());
        Assert.Equal(expected.Step, state.GetProperty("step").GetInt64());
        Assert.Equal(expected.Ids.ToArray(), state.GetProperty("ids").EnumerateArray().Select(value => value.GetInt64()).ToArray());
        Assert.Equal(expected.Species.ToArray(), state.GetProperty("species").EnumerateArray().Select(value => value.GetInt32()).ToArray());
        for (int index = 0; index < expected.Count; index++)
        {
            JsonElement position = state.GetProperty("positions")[index];
            JsonElement velocity = state.GetProperty("velocities")[index];
            EqualBits(expected.Positions[index].X, position.GetProperty("x").GetDouble());
            EqualBits(expected.Positions[index].Y, position.GetProperty("y").GetDouble());
            EqualBits(expected.Velocities[index].X, velocity.GetProperty("x").GetDouble());
            EqualBits(expected.Velocities[index].Y, velocity.GetProperty("y").GetDouble());
        }
    }

    private static void EqualBits(double expected, double actual) =>
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual));

    private async Task<string> Cli(int expectedExit, params string[] arguments)
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
        foreach (string argument in arguments) { start.ArgumentList.Add(argument); }
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("CLI did not start.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw new TimeoutException("Capture CLI exceeded 45 seconds.");
        }
        string text = await stdout;
        string error = await stderr;
        Assert.True(process.ExitCode == expectedExit, $"Expected exit {expectedExit}, got {process.ExitCode}: {text}\n{error}");
        if (expectedExit != 0)
        {
            Assert.StartsWith("Error:", error, StringComparison.Ordinal);
            Assert.DoesNotContain("Unhandled exception", error, StringComparison.Ordinal);
        }
        return text + error;
    }

    private string PathFor(string name) => Path.Combine(directory, name);

    private static string FindRepository()
    {
        for (DirectoryInfo? current = new(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "ParticleLife.slnx"))) { return current.FullName; }
        }
        throw new DirectoryNotFoundException("Cannot find repository from test output.");
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
