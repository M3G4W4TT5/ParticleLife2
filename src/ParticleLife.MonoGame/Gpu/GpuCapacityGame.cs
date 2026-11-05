using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ParticleLife.Core.Experiments;

namespace ParticleLife.MonoGame.Gpu;

/// <summary>Hosts one explicitly requested saved-input GPU capacity screen with a bounded process lifetime.</summary>
/// <remarks>Input bytes are hashed before creating a graphics context and parsed from those same bytes.
/// The caller supplies the independently recorded expected hash. Update polls run unpaced without
/// presentation; this is not a real editor, 60 Hz frame-paced rate or input-responsiveness measurement.
/// An external supervisor may impose a shorter deadline; progress identifies incomplete phases.</remarks>
internal sealed class GpuCapacityGame : Game
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private readonly GraphicsDeviceManager graphics;
    private readonly string inputPath, inputHash, outputDirectory;
    private readonly ExperimentDefinition input;
    private readonly bool profileFirstStep;
    private readonly Stopwatch watchdog = Stopwatch.StartNew();
    private GpuCapacityRun? run;
    private IEnumerator<bool>? work;
    private bool reported;
    internal int ExitCode { get; private set; } = 1;

    internal GpuCapacityGame(string inputPath, string expectedHash, string outputDirectory, bool profileFirstStep = false)
    {
        this.profileFirstStep = profileFirstStep;
        this.inputPath = Path.GetFullPath(inputPath);
        this.outputDirectory = Path.GetFullPath(outputDirectory);
        byte[] bytes = File.ReadAllBytes(this.inputPath);
        inputHash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(inputHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        { throw new IOException("GPU capacity input does not match the supplied SHA-256."); }
        input = ExperimentFile.Deserialize(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
        if (Directory.Exists(this.outputDirectory) && Directory.EnumerateFileSystemEntries(this.outputDirectory).Any())
        { throw new IOException("GPU capacity evidence requires a fresh or empty output directory."); }
        Directory.CreateDirectory(this.outputDirectory);
        graphics = new(this)
        {
            PreferredBackBufferWidth = 640,
            PreferredBackBufferHeight = 360,
            GraphicsProfile = GraphicsProfile.Reach,
            SynchronizeWithVerticalRetrace = false
        };
        IsFixedTimeStep = false;
        InactiveSleepTime = TimeSpan.Zero;
        Window.Title = "Particle Life 2 - bounded unpaced GPU capacity screen";
    }

    protected override void LoadContent()
    {
        try
        {
            run = new(GraphicsDevice, input, inputHash, outputDirectory, Progress, profileFirstStep);
            work = run.Execute().GetEnumerator();
        }
        catch (Exception failure) { Fail(failure); }
    }

    protected override void Update(GameTime gameTime)
    {
        SuppressDraw();
        if (reported) { Exit(); return; }
        try
        {
            int seconds = profileFirstStep ? 45 : 180;
            if (watchdog.Elapsed.TotalSeconds > seconds) { throw new TimeoutException($"GPU diagnostic/capacity run exceeded its {seconds}-second host watchdog."); }
            long started = Stopwatch.GetTimestamp();
            do
            {
                if (work is null || !work.MoveNext())
                {
                    using FileStream source = File.OpenRead(inputPath);
                    if (Convert.ToHexString(SHA256.HashData(source)) != inputHash)
                    { throw new IOException("GPU capacity input changed during the screen."); }
                    ExitCode = 0;
                    WriteReport(null);
                    Console.WriteLine(profileFirstStep ? "GPU_PROFILE_PASS" : "GPU_CAPACITY_PASS");
                    Exit();
                    break;
                }
            }
            while (Stopwatch.GetElapsedTime(started).TotalMilliseconds < 4);
        }
        catch (Exception failure) { Fail(failure); }
        base.Update(gameTime);
    }

    private void Progress(string phase)
    {
        WriteJson(Path.Combine(outputDirectory, "progress.json"), new
        { phase, updatedAtUtc = DateTimeOffset.UtcNow, warmupSteps = run?.WarmupSteps ?? 0,
            measuredSteps = run?.MeasuredSteps ?? 0, latestCompletedMeasurement = run?.LastCompletedMeasurement,
            submissionProgress = run?.SubmissionProgress });
        Console.WriteLine("GPU_CAPACITY_STAGE " + phase);
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
        WriteJson(Path.Combine(outputDirectory, profileFirstStep ? "gpu-first-step-profile.json" : "gpu-capacity.json"), new
        {
            schemaVersion = 1,
            status = failure is null ? profileFirstStep ? "completed-cold-diagnostic" : "completed-screen" : "failed",
            mode = profileFirstStep ? "one-cold-step-diagnostic" : "warmed-capacity-screen",
            passed = failure is null,
            runtime = Environment.Version.ToString(),
            operatingSystem = Environment.OSVersion.ToString(),
            processorCount = Environment.ProcessorCount,
            inputFile = inputPath,
            inputSha256 = inputHash,
            input.Name,
            input.Generator,
            input.Provenance,
            count = input.InitialState.Count,
            input.TimeStep,
            phase = run?.Phase,
            warmupSteps = run?.WarmupSteps ?? 0,
            measuredSteps = run?.MeasuredSteps ?? 0,
            latestCompletedMeasurement = run?.LastCompletedMeasurement,
            submissionProgress = run?.SubmissionProgress,
            wallSecondsIncludingLoadWarmupRenderCaptureAndIo = watchdog.Elapsed.TotalSeconds,
            evidence = run?.Evidence,
            comparisonPolicy = profileFirstStep
                ? "One cold completed step identifies grid, integration and submission costs. No warmup, capacity, steady-state allocation, percentile or throughput claim. Explicit float conversion retains the hashed input externally."
                : "Actual saved common CPU-evolved input, explicitly converted to float32. Seven continuing steps start again at that saved input after warmup. GPU ordering/precision may diverge; no trajectory-equivalence assertion.",
            pacingPolicy = "Unpaced, rapid zero-timeout polling with normal Game drawing/present suppressed. Neither 60 Hz editor achieved speed nor interactive responsiveness is measured.",
            memoryPolicy = "Retained device bytes are requested buffer payload only; driver allocations, shader/program/query objects are excluded. Whole-process memory includes input, host, snapshots and reporting.",
            percentilePolicy = "Seven exploratory samples; nearest-rank p95 is the maximum and provides no confident tail-latency estimate.",
            failure
        });
    }

    internal static void WriteJson(string path, object value)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { work?.Dispose(); }
        base.Dispose(disposing);
    }
}
