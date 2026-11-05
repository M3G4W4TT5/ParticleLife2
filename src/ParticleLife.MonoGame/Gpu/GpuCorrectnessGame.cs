using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ParticleLife.Core.Execution;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using ParticleLife.MonoGame.Rendering;
using ParticleLife.Viewer;

namespace ParticleLife.MonoGame.Gpu;

/// <summary>Runs a bounded explicit hardware correctness harness against the production resident engine.</summary>
/// <remarks>This mode is opt-in and creates the same MonoGame DesktopGL context as the editor. It
/// measures no performance claims. Every wait is a zero-timeout fence poll across update calls. Actual
/// small captures and a framebuffer artifact are saved only after explicit test capture operations.
/// Ordinary numerical loops assert zero full-state downloads. A ninety-second watchdog fails visibly.</remarks>
internal sealed class GpuCorrectnessGame : Game
{
    private readonly string outputDirectory;
    private readonly Stopwatch timeout = Stopwatch.StartNew();
    private readonly List<object> evidence = [];
    private readonly GraphicsDeviceManager graphics;
    private IEnumerator<bool>? checks;
    private GlGpuSimulation? gpu;
    private GlResidentRenderer? renderer;
    private ExecutionProvenance? provenance;
    private bool reported;
    internal int ExitCode { get; private set; } = 1;

    internal GpuCorrectnessGame(string outputDirectory)
    {
        this.outputDirectory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(this.outputDirectory) && Directory.EnumerateFileSystemEntries(this.outputDirectory).Any())
        {
            throw new IOException("GPU correctness evidence requires a fresh or empty output directory.");
        }
        Directory.CreateDirectory(this.outputDirectory);
        graphics = new(this)
        {
            PreferredBackBufferWidth = 640,
            PreferredBackBufferHeight = 420,
            GraphicsProfile = GraphicsProfile.Reach,
            SynchronizeWithVerticalRetrace = false
        };
        IsFixedTimeStep = false;
        InactiveSleepTime = TimeSpan.Zero;
        Window.Title = "Particle Life 2 - bounded GPU correctness checks";
    }

    protected override void LoadContent()
    {
        try
        {
            renderer = new(GraphicsDevice);
            checks = Verify().GetEnumerator();
        }
        catch (Exception failure) { Fail(failure); }
    }

    protected override void Update(GameTime gameTime)
    {
        if (reported) { Exit(); return; }
        try
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(90)) { throw new TimeoutException("GPU correctness checks exceeded the 90-second watchdog."); }
            long started = Stopwatch.GetTimestamp();
            do
            {
                if (checks is null || !checks.MoveNext())
                {
                    ExitCode = 0;
                    WriteReport(null);
                    Console.WriteLine("GPU_CORRECTNESS_PASS");
                    Exit();
                    break;
                }
            }
            while (Stopwatch.GetElapsedTime(started).TotalMilliseconds < 4);
        }
        catch (Exception failure) { Fail(failure); }
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(new Color(12, 20, 24));
        base.Draw(gameTime);
    }

    private IEnumerable<bool> Verify()
    {
        foreach (GpuCorrectnessCase fixture in GpuCorrectnessCase.Create())
        {
            Console.WriteLine("GPU_CHECK_BEGIN " + fixture.Name);
            SimulationConfiguration configuration = fixture.Configuration;
            gpu = new(configuration, fixture.Initial);
            provenance ??= gpu.Execution;
            using Simulation? reference = fixture.StationaryReference ? null : new(fixture.Initial, configuration.Parameters,
                configuration.Matrix, configuration.TimeStep);
            List<SessionEvent> events = [];
            int completed = 0;
            for (int step = 0; step < fixture.Steps; step++)
            {
                if (fixture.ScalarEdit && step == fixture.Steps / 2)
                {
                    configuration = new(configuration.Parameters with { ForceScale = 0.375 }, configuration.Matrix,
                        configuration.TimeStep, configuration.Backend, configuration.WorkerCount);
                    gpu.Reconfigure(configuration);
                    reference?.Reconfigure(configuration);
                    events.Add(new(gpu.Boundary.Step, configuration, label: "Capture-free scalar edit"));
                }
                gpu.SubmitStep();
                while (gpu.IsStepPending)
                {
                    if (gpu.PollStep()) { completed++; }
                    else { yield return false; }
                }
                reference?.Step();
                Require(gpu.FullStateReadbacks == 0, fixture.Name + " performed an ordinary-step full-state download.");
            }
            Require(completed == fixture.Steps, "Published step count differs from submitted steps.");
            Require(gpu.Boundary.Step == fixture.Steps, "The GPU host clock did not advance once per completed step.");
            gpu.RequestSummary();
            while (gpu.LatestSummary is null)
            {
                gpu.PollSummary();
                yield return false;
            }
            ResidentSummary summary = gpu.LatestSummary!;
            Require(summary.Count == fixture.Initial.Count, "The device summary lost particle membership.");
            Require(summary.Step == gpu.Boundary.Step, "A device summary has the wrong source boundary.");
            Require(gpu.FullStateReadbacks == 0, "A reduced summary downloaded the full state.");
            long normalMetadataBytes = gpu.MetadataReadbackBytes;
            gpu.BeginCapture();
            ParticleSnapshot? actual = null;
            while (actual is null)
            {
                actual = gpu.PollCapture();
                if (actual is null) { yield return false; }
            }
            ParticleSnapshot expected = reference is null
                ? new(fixture.Initial.Ids, fixture.Initial.Positions, fixture.Initial.Velocities, fixture.Initial.Species, actual.Time, actual.Step)
                : reference.CaptureSnapshot();
            (double positionError, double velocityError) comparison;
            try
            {
                comparison = Compare(expected, actual, fixture.AbsoluteTolerance, fixture.RelativeTolerance, configuration.Parameters);
            }
            catch
            {
                if (fixture.Initial.Count <= 4096)
                {
                    SaveCaptureEvidence(fixture, configuration, events, actual, gpu.Execution, ".failed-actual.capture.json");
                    SaveCaptureEvidence(fixture, configuration, events, expected, ExecutionProvenance.Cpu(configuration), ".failed-reference.capture.json");
                }
                throw;
            }
            (double positionError, double velocityError) = comparison;
            if (fixture.RequireMotion) { Require(actual.Velocities.ToArray().Any(value => value.X != 0 || value.Y != 0), "A nonzero force vanished."); }
            if (fixture.Name == "soft-global-evolving") { VerifyResidentDrawing(gpu, actual); }
            string hash = StateHash(actual);
            gpu.Replace(configuration, actual);
            gpu.BeginCapture();
            ParticleSnapshot? restored = null;
            while (restored is null)
            {
                restored = gpu.PollCapture();
                if (restored is null) { yield return false; }
            }
            Require(StateHash(restored) == hash, "Explicit upload/capture did not restore exact float bits, IDs and clock.");
            if (fixture.Initial.Count <= 4096)
            {
                ExperimentDefinition initial = new(fixture.Name, fixture.Configuration.Parameters, fixture.Configuration.Matrix,
                    fixture.Initial, fixture.Configuration.TimeStep, new("gpu-correctness", "1", null, Environment.Version.ToString()));
                SessionDefinition actions = new(initial, events, actual.Step);
                CapturedSessionDefinition recorded = new(actions, [new(configuration, actual, gpu.Execution, events.Count, "terminal")], []);
                string saved = ExecutionCaptureFile.Serialize(recorded);
                File.WriteAllText(Path.Combine(outputDirectory, fixture.Name + ".capture.json"), saved);
                Require(StateHash(ExecutionCaptureFile.Deserialize(saved).TerminalCapture.State) == hash, "Capture codec changed actual GPU values.");
            }
            evidence.Add(new
            {
                Case = fixture.Name,
                fixture.Initial.Count,
                Steps = completed,
                Boundary = configuration.Parameters.Boundary.ToString(),
                PairRanges = configuration.Matrix.Ranges is not null,
                MaximumPositionError = positionError,
                MaximumVelocityError = velocityError,
                fixture.AbsoluteTolerance,
                fixture.RelativeTolerance,
                OrdinaryFullStateReadbacks = 0,
                MetadataReadbackBytes = normalMetadataBytes,
                ExplicitFullStateReadbacks = gpu.FullStateReadbacks,
                ActualStateSha256 = hash,
                CaptureRestoreExact = true,
                SavedCaptureRoundTripExact = fixture.Initial.Count <= 4096
            });
            Console.WriteLine($"GPU_CHECK_PASS {fixture.Name} count={actual.Count} steps={completed} positionError={positionError:G9} velocityError={velocityError:G9}");
            gpu.Dispose();
            gpu = null;
            yield return true;
        }
        foreach (bool result in VerifyAtomicityAndStructure()) { yield return result; }
    }

    private IEnumerable<bool> VerifyAtomicityAndStructure()
    {
        ModelParameters parameters = new() { Boundary = BoundaryKind.Open, ForceScale = 0, WallStrength = 0, DampingHalfLife = null, Cutoff = 0.25 };
        SimulationConfiguration configuration = new(parameters, new(2, [0, 0.5, -0.5, 0]), 1.0 / 64);
        ParticleSnapshot initial = GpuCorrectnessCase.State([new(0.25, 0.25), new(0.75, 0.5)], [new(0.125, 0), new(0, 0.25)], 2);
        gpu = new(configuration, initial);
        long metadataBefore = gpu.MetadataReadbackBytes;
        gpu.SubmitStep();
        ExpectRejected(() => gpu.Reconfigure(configuration), "An in-flight rule change must be rejected.");
        while (gpu.MetadataReadbackBytes == metadataBefore)
        {
            Require(!gpu.PollStep(), "The cancellation fixture published before its force submission.");
            yield return false;
        }
        gpu.CancelPendingStep();
        while (gpu.IsStepPending)
        {
            Require(!gpu.PollStep(), "A cancelled numerical candidate was published.");
            yield return false;
        }
        Require(gpu.Boundary.Step == 0 && gpu.Boundary.Time == 0, "Cancellation changed the completed clock.");
        gpu.BeginCapture();
        ParticleSnapshot? cancelled = null;
        while (cancelled is null) { cancelled = gpu.PollCapture(); if (cancelled is null) { yield return false; } }
        Require(StateHash(initial) == StateHash(cancelled), "Cancelled integration changed the completed buffer.");
        uint retainedBuffer = gpu.CompletedBuffer;
        ExpectRejected(() => gpu.Reconfigure(new(parameters with { ForceScale = double.MaxValue }, configuration.Matrix, configuration.TimeStep)),
            "Unsupported float model must reject before publication.");
        Require(ReferenceEquals(configuration, gpu.Configuration) && retainedBuffer == gpu.CompletedBuffer, "Rejected model replaced active resources.");
        ParticleSnapshot structural = new([long.MaxValue, 987654321], [new(0.75, 0.5), new(0.5, 0.25)], [new(0, 0.25), new(0.125, 0)], [1, 0]);
        gpu.Reconfigure(configuration, structural);
        using Simulation cpu = new(structural, configuration.Parameters, configuration.Matrix, configuration.TimeStep);
        gpu.SubmitStep();
        while (gpu.IsStepPending) { gpu.PollStep(); yield return false; }
        cpu.Step();
        gpu.BeginCapture();
        ParticleSnapshot? edited = null;
        while (edited is null) { edited = gpu.PollCapture(); if (edited is null) { yield return false; } }
        Compare(cpu.CaptureSnapshot(), edited, 0, 0);
        using Simulation cpuContinuation = new(edited, configuration.Parameters, configuration.Matrix, configuration.TimeStep);
        Require(StateHash(cpuContinuation.CaptureSnapshot()) == StateHash(edited), "GPU-to-CPU widening changed captured state.");
        evidence.Add(new
        {
            Case = "cancel-model-rejection-structural-transfer",
            CancellationRetainsCompletedBits = true,
            InvalidModelRetainsResources = true,
            StructuralIdsAndClockExact = true,
            CpuTransferExact = true
        });
        gpu.Dispose();
        gpu = null;

        foreach (bool overflow in new[] { false, true })
        {
            ParticleSnapshot dangerous = GpuCorrectnessCase.State([new(262143.75, 0.5), new(0.5, 0.5)],
                [new(overflow ? float.MaxValue : 64, 0), new(0.125, 0)], 2);
            SimulationConfiguration failing = new(parameters, configuration.Matrix, overflow ? 2 : 1.0 / 64);
            gpu = new(failing, dangerous);
            gpu.SubmitStep();
            bool rejected = false;
            while (gpu.IsStepPending)
            {
                try { Require(!gpu.PollStep(), "A failed numerical candidate was published."); }
                catch (InvalidOperationException failure) when (failure.Message.StartsWith("GPU candidate rejected", StringComparison.Ordinal)) { rejected = true; }
                yield return false;
            }
            Require(rejected, "The numeric failure fixture did not reject its candidate.");
            Require(gpu.Boundary.Step == 0, "A failed candidate advanced the clock.");
            gpu.BeginCapture();
            ParticleSnapshot? retained = null;
            while (retained is null) { retained = gpu.PollCapture(); if (retained is null) { yield return false; } }
            Require(StateHash(retained) == StateHash(dangerous), "Numerical failure did not retain every completed input bit.");
            evidence.Add(new { Case = overflow ? "nonfinite-candidate-atomicity" : "coordinate-range-candidate-atomicity", RetainedExactInput = true, CompletedStep = gpu.Boundary.Step });
            gpu.Dispose();
            gpu = null;
        }
        Console.WriteLine("GPU_CHECK_PASS cancellation/rejection/structural/CPU-transfer atomicity");
    }

    private void VerifyResidentDrawing(GlGpuSimulation simulation, ParticleSnapshot actual)
    {
        using RenderTarget2D target = new(GraphicsDevice, 256, 192, false, SurfaceFormat.Color, DepthFormat.None);
        AppearanceSettings appearance = new();
        WorldTransform transform = new(1, 1, 0, 0, target.Width, target.Height);
        long capturesBefore = simulation.FullStateReadbacks;
        uint stateBuffer = simulation.CompletedBuffer;
        GraphicsDevice.SetRenderTarget(target);
        GraphicsDevice.Clear(Color.Black);
        renderer!.Draw(simulation, transform, new(0, 0, target.Width, target.Height), appearance);
        GraphicsDevice.SetRenderTarget(null);
        Color[] pixels = new Color[target.Width * target.Height];
        target.GetData(pixels);
        int coloured = pixels.Count(pixel => pixel.R != 0 || pixel.G != 0 || pixel.B != 0);
        int centres = 0;
        for (int index = 0; index < actual.Count; index++)
        {
            Vector2d point = transform.ToScreen(actual.Positions[index]);
            int x = (int)Math.Floor(point.X), y = (int)Math.Floor(point.Y);
            if (x >= 0 && x < target.Width && y >= 0 && y < target.Height)
            {
                Color pixel = pixels[y * target.Width + x];
                Require(pixel.R != 0 || pixel.G != 0 || pixel.B != 0, "A resident particle centre did not reach its expected target pixel.");
                centres++;
            }
        }
        Require(centres == actual.Count && coloured > actual.Count, "The resident draw did not show every fixture particle.");
        using (FileStream file = File.Create(Path.Combine(outputDirectory, "resident-framebuffer.png"))) { target.SaveAsPng(file, target.Width, target.Height); }
        GraphicsDevice.SetRenderTarget(target);
        ParticleRenderMetrics cameraOnly = renderer.Draw(simulation, new(1, 1, 0, 0, target.Width, target.Height, new() { Zoom = 1.5 }),
            new(0, 0, target.Width, target.Height), appearance with { ParticleRadius = 3 });
        GraphicsDevice.SetRenderTarget(null);
        Require(cameraOnly.UploadedBytes == 0, "Camera/radius edits uploaded particle or palette data.");
        Require(simulation.CompletedBuffer == stateBuffer && simulation.FullStateReadbacks == capturesBefore, "Drawing downloaded or replaced resident simulation state.");
        evidence.Add(new
        {
            Case = "compute-to-render-shared-buffer",
            ExpectedCentres = actual.Count,
            VerifiedCentres = centres,
            ColouredPixels = coloured,
            ParticleUploadBytes = 0,
            CameraRadiusUploadBytes = cameraOnly.UploadedBytes,
            StateReadbacksDuringDraw = 0,
            TargetWidth = target.Width,
            TargetHeight = target.Height
        });
    }

    private void SaveCaptureEvidence(GpuCorrectnessCase fixture, SimulationConfiguration configuration,
        IReadOnlyList<SessionEvent> events, ParticleSnapshot state, ExecutionProvenance execution, string suffix)
    {
        ExperimentDefinition initial = new(fixture.Name, fixture.Configuration.Parameters, fixture.Configuration.Matrix,
            fixture.Initial, fixture.Configuration.TimeStep, new("gpu-correctness", "1", null, Environment.Version.ToString()));
        CapturedSessionDefinition captured = new(new(initial, events, state.Step),
            [new(configuration, state, execution, events.Count, "failed-comparison")], []);
        File.WriteAllText(Path.Combine(outputDirectory, fixture.Name + suffix), ExecutionCaptureFile.Serialize(captured));
    }

    private static (double Position, double Velocity) Compare(ParticleSnapshot expected, ParticleSnapshot actual, double absolute, double relative,
        ModelParameters? parameters = null)
    {
        Require(expected.Ids.SequenceEqual(actual.Ids) && expected.Species.SequenceEqual(actual.Species), "Stable logical identity/species order changed.");
        Require(expected.Step == actual.Step && expected.Time == actual.Time, "Capture clock differs from the reference boundary.");
        double positionError = 0, velocityError = 0;
        for (int index = 0; index < expected.Count; index++)
        {
            Check(expected.Positions[index].X, actual.Positions[index].X, index, "position X", ref positionError, parameters?.WorldWidth);
            Check(expected.Positions[index].Y, actual.Positions[index].Y, index, "position Y", ref positionError, parameters?.WorldHeight);
            Check(expected.Velocities[index].X, actual.Velocities[index].X, index, "velocity X", ref velocityError, null);
            Check(expected.Velocities[index].Y, actual.Velocities[index].Y, index, "velocity Y", ref velocityError, null);
        }
        return (positionError, velocityError);
        void Check(double left, double right, int index, string component, ref double maximum, double? extent)
        {
            double error = Math.Abs(left - right);
            if (parameters?.Boundary == BoundaryKind.Periodic && extent.HasValue)
            {
                error = Math.Min(error, Math.Abs(extent.Value - error));
            }
            maximum = Math.Max(maximum, error);
            Require(error <= absolute + relative * Math.Max(Math.Abs(left), Math.Abs(right)),
                $"CPU/GPU {component} differs at logical slot {index}: CPU {left:G17}, GPU {right:G17}, error {error:G9}.");
        }
    }

    private static string StateHash(ParticleSnapshot state)
    {
        using MemoryStream bytes = new();
        using (BinaryWriter writer = new(bytes, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(state.Count); writer.Write(state.Step); writer.Write(state.Time);
            for (int index = 0; index < state.Count; index++)
            {
                writer.Write(state.Ids[index]); writer.Write(state.Species[index]);
                writer.Write(state.Positions[index].X); writer.Write(state.Positions[index].Y);
                writer.Write(state.Velocities[index].X); writer.Write(state.Velocities[index].Y);
            }
        }
        return Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
    }

    private static void ExpectRejected(Action action, string message)
    {
        try { action(); }
        catch (Exception failure) when (failure is NotSupportedException or InvalidOperationException or ArgumentException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private void Fail(Exception failure)
    {
        Console.Error.WriteLine(failure);
        WriteReport(failure.ToString());
        Exit();
    }

    private void WriteReport(string? failure)
    {
        if (reported) { return; }
        reported = true;
        string report = JsonSerializer.Serialize(new
        {
            Passed = failure is null,
            Runtime = Environment.Version.ToString(),
            OperatingSystem = Environment.OSVersion.ToString(),
            Execution = provenance,
            WallSeconds = timeout.Elapsed.TotalSeconds,
            PerformanceClaim = false,
            Evidence = evidence,
            Failure = failure
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(outputDirectory, "gpu-correctness.json"), report);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            checks?.Dispose();
            gpu?.Dispose();
            renderer?.Dispose();
        }
        base.Dispose(disposing);
    }
}
