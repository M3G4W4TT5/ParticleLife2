using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;

namespace ParticleLife.Benchmarks;

/// <summary>Runs each large CPU workload in a separately killable process with an explicit wall budget.</summary>
/// <remarks>
/// The deadline covers process startup, input verification/loading, environment capture, warmup,
/// measurement and reporting. Killing a process can interrupt an arbitrarily expensive single step;
/// a timeout is not a measured latency or evidence that no step completed. Dense cases are opt-in.
/// </remarks>
internal static class P1Runner
{
    private const int WarmupSteps = 2;

    internal static void Run(string outputDirectory, string inputDirectory, string selection, int samples, int timeoutSeconds,
        int workers, string mode)
    {
        Validate(samples, workers, mode);
        if (timeoutSeconds is < 1 or > 3_600)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Use a per-case wall budget between 1 and 3600 seconds.");
        }

        string[] requested = P1Inputs.Select(selection).ToArray();
        inputDirectory = Path.GetFullPath(inputDirectory);
        outputDirectory = Path.GetFullPath(outputDirectory);
        using JsonDocument manifest = P1Inputs.ReadVerifiedManifest(inputDirectory);
        Dictionary<string, JsonElement> prepared = manifest.RootElement.GetProperty("inputs").EnumerateArray()
            .ToDictionary(input => input.GetProperty("name").GetString()!, input => input.Clone(), StringComparer.Ordinal);
        Directory.CreateDirectory(outputDirectory);
        if (Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            throw new IOException("Measure P1 into an empty output directory so old results cannot masquerade as new measurements.");
        }

        object environment = LatencyRunner.CaptureEnvironment("P1 supervisor; measurements run in isolated CPU double uniform-grid child processes", workers);
        List<object> results = [];
        WriteReport();
        foreach (string name in requested)
        {
            if (!prepared.TryGetValue(name, out JsonElement input))
            {
                results.Add(new { name, status = "skipped-not-prepared", reason = "Prepare the requested selection into a new input directory." });
                WriteReport();
                continue;
            }

            string outputPath = Path.Combine(outputDirectory, name + ".json");
            string inputPath = Path.Combine(inputDirectory, input.GetProperty("file").GetString()!);
            ProcessStartInfo start = ChildStart(inputPath, outputPath, samples, workers, mode, input.GetProperty("sha256").GetString()!);
            Console.WriteLine($"Measuring {name}: {timeoutSeconds}s whole-child budget, {WarmupSteps} warmup steps, {samples} {mode} samples, {workers} grid worker(s).");
            long began = Stopwatch.GetTimestamp();
            using Process process = Process.Start(start) ?? throw new IOException("Could not start isolated P1 measurement.");
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();
            bool completed = process.WaitForExit(timeoutSeconds * 1_000);
            if (!completed)
            {
                // This process and its simulation workers belong to this one measurement only.
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // Natural exit can race the deadline check; retain the budget-expired status.
                }
                if (!process.WaitForExit(5_000))
                {
                    throw new IOException("Timed-out measurement did not exit after termination; stop before starting competing work.");
                }
            }

            double childWallSeconds = Stopwatch.GetElapsedTime(began).TotalSeconds;
            File.WriteAllText(Path.Combine(outputDirectory, name + ".stdout.txt"), standardOutput.GetAwaiter().GetResult());
            File.WriteAllText(Path.Combine(outputDirectory, name + ".stderr.txt"), standardError.GetAwaiter().GetResult());
            string status = !completed ? "timed-out" : process.ExitCode != 0 ? "failed" : File.Exists(outputPath) ? "completed" : "failed-missing-report";
            JsonElement? measurement = File.Exists(outputPath) ? ReadJson(outputPath) : null;
            JsonElement? progress = File.Exists(outputPath + ".progress.json") ? ReadJson(outputPath + ".progress.json") : null;
            results.Add(new
            {
                name,
                status,
                input,
                childWallSeconds,
                timeoutSeconds,
                exitCode = process.ExitCode,
                measurement,
                progress,
                note = completed ? "See child report and stderr for completed/failed execution."
                    : "Whole-child budget expired; no completed latency distribution is claimed, including when expiry was during startup, loading or warmup."
            });
            Console.WriteLine($"{name}: {status}");
            WriteReport();
        }

        Console.WriteLine($"Report: {Path.Combine(outputDirectory, "p1.json")}");

        void WriteReport() => P1Inputs.WriteJson(Path.Combine(outputDirectory, "p1.json"), new
        {
            schemaVersion = 1,
            measuredAtUtc = DateTimeOffset.UtcNow,
            environment,
            inputManifest = Path.Combine(inputDirectory, "inputs.json"),
            inputManifestSha256 = P1Inputs.HashFile(Path.Combine(inputDirectory, "inputs.json")),
            selection,
            samples,
            warmupSteps = WarmupSteps,
            timeoutSecondsPerCase = timeoutSeconds,
            workers,
            mode,
            targetPresentationFramesPerSecond = 60,
            targetPlaybackSpeed = 1,
            method = "Isolated serial sessions; exact saved inputs, binary64 CPU uniform-grid. Two bounded warmup steps, no minimum warmup duration. Nearest-rank step p50/p95. Short screening is exploratory, not a stable microbenchmark.",
            timeoutMeaning = "Includes child startup, hashing, loading, warmup, measurement and writing. Parent manifest verification is outside that budget. Five-second termination grace. No timed-out case yields an inferred step latency.",
            allocations = "Process-wide precise allocation deltas around each warmed step include parallel workers; reset and telemetry/report allocations excluded.",
            memory = "Managed live bytes after full GC, grid array-payload estimate, and whole-process working/private/peak working set. Includes saved input, runtime and tooling; not solver-only resident memory.",
            rendering = "Pending consolidated native review: FPS, upload/draw/present and visual settings are not measured by this CPU runner.",
            inputResponsiveness = "Pending consolidated native review: measure idle and dragging latency separately from solver throughput.",
            gpu = "Not measured here; separate GPU interoperability/device/timing evidence required.",
            results
        });
    }

    /// <summary>Measures one verified input; invoked by the supervisor rather than inside the UI process.</summary>
    internal static void MeasureCase(string inputPath, string outputPath, int samples, int workers, string mode, string expectedHash)
    {
        Validate(samples, workers, mode);
        Progress("verifying-input");
        if (P1Inputs.HashFile(inputPath) != expectedHash)
        {
            throw new IOException("Input changed between manifest verification and child measurement.");
        }

        object environment = LatencyRunner.CaptureEnvironment("uniform-grid CPU binary64 / damped-semi-implicit-euler/1", workers);
        object baselineMemory = Memory();
        Progress("loading-input");
        ExperimentDefinition experiment = ExperimentFile.Load(inputPath);
        using Simulation simulation = experiment.CreateSimulation(ForceBackend.UniformGrid, workers);
        Progress("warming-up");
        for (int i = 0; i < WarmupSteps; i++)
        {
            if (mode == "static")
            {
                simulation.Reset(experiment.InitialState);
            }

            simulation.Step();
        }

        if (mode == "static")
        {
            simulation.Reset(experiment.InitialState);
        }

        long startStep = simulation.Observe().Step;
        double startTime = simulation.Observe().Time;
        double[] milliseconds = new double[samples];
        object warmedMemory = Memory();
        Progress("measuring");
        long allocatedBytes = 0;
        long totalCandidatePairs = 0;
        long totalAcceptedPairs = 0;
        int fallbackSamples = 0;
        long began = Stopwatch.GetTimestamp();
        for (int i = 0; i < samples; i++)
        {
            if (mode == "static")
            {
                simulation.Reset(experiment.InitialState);
            }

            long bytesBefore = GC.GetTotalAllocatedBytes(precise: true);
            long start = Stopwatch.GetTimestamp();
            simulation.Step();
            long ticks = Stopwatch.GetTimestamp() - start;
            allocatedBytes += GC.GetTotalAllocatedBytes(precise: true) - bytesBefore;
            milliseconds[i] = 1_000d * ticks / Stopwatch.Frequency;
            totalCandidatePairs += simulation.LastStatistics.CandidatePairs;
            totalAcceptedPairs += simulation.LastStatistics.AcceptedPairs;
            if (simulation.LastExecution?.ActualBackend == ForceBackend.Direct)
            {
                fallbackSamples++;
            }
        }

        double samplingWallSeconds = Stopwatch.GetElapsedTime(began).TotalSeconds;
        long finalStep = simulation.Observe().Step;
        double finalTime = simulation.Observe().Time;
        double[] sorted = (double[])milliseconds.Clone();
        Array.Sort(sorted);
        double computeSeconds = milliseconds.Sum() / 1_000;
        object finalMemory = Memory();
        P1Inputs.WriteJson(outputPath + ".tmp", new
        {
            schemaVersion = 1,
            status = "completed",
            environment,
            inputFile = Path.GetFullPath(inputPath),
            inputSha256 = expectedHash,
            experiment.Name,
            population = experiment.InitialState.Count,
            speciesCount = experiment.Matrix.SpeciesCount,
            experiment.Parameters,
            experiment.TimeStep,
            experiment.Generator,
            experiment.Provenance,
            savedBackend = experiment.Backend,
            savedWorkers = experiment.WorkerCount,
            requestedBackend = ForceBackend.UniformGrid,
            requestedWorkers = workers,
            actualLastExecution = simulation.LastExecution,
            precision = "IEEE 754 binary64 state/force/integration",
            modelVersion = ExperimentFile.ModelVersion,
            integratorVersion = ExperimentFile.IntegratorVersion,
            mode,
            warmupSteps = WarmupSteps,
            samples,
            startStep,
            startTime,
            finalStep,
            finalTime,
            step = new
            {
                p50Milliseconds = sorted[(int)Math.Ceiling(0.5 * samples) - 1],
                p95Milliseconds = sorted[(int)Math.Ceiling(0.95 * samples) - 1],
                meanMilliseconds = milliseconds.Average(),
                milliseconds,
                allocatedBytes,
                allocatedBytesPerStep = (double)allocatedBytes / samples,
                totalCandidatePairs,
                totalAcceptedPairs,
                fallbackSamples
            },
            samplingWallSeconds,
            achievedSimulationTimePerWallSecond = mode == "evolving" ? (finalTime - startTime) / samplingWallSeconds : (double?)null,
            completedStepsPerWallSecond = samples / samplingWallSeconds,
            stepEquivalentSimulationTimePerComputeSecond = samples * experiment.TimeStep / computeSeconds,
            sustainsNominalPlaybackSpeedOne = mode == "evolving" ? (finalTime - startTime) >= samplingWallSeconds : (bool?)null,
            throughputMeaning = "Evolving throughput counts measured completed simulation time, excluding load, warmup and reporting. Static resets to the same input before each timed step; only compute-equivalent speed is meaningful there. No rendering/input work is included.",
            memory = new { baseline = baselineMemory, warmed = warmedMemory, final = finalMemory },
            gridEstimatedRetainedBytes = simulation.LastExecution?.Index.EstimatedRetainedBytes,
            renderingAndInput = "Pending separate native review at 60 FPS target. Solver throughput alone does not establish responsiveness."
        });
        File.Move(outputPath + ".tmp", outputPath, overwrite: false);
        Progress("completed");
        GC.KeepAlive(experiment);

        void Progress(string stage)
        {
            string path = outputPath + ".progress.json";
            P1Inputs.WriteJson(path + ".tmp", new { stage, updatedAtUtc = DateTimeOffset.UtcNow });
            File.Move(path + ".tmp", path, overwrite: true);
        }
    }

    private static void Validate(int samples, int workers, string mode)
    {
        if (samples is < 5 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(samples), "Use 5..10000 P1 samples.");
        }

        ForceWorkerConfiguration.Validate(ForceBackend.UniformGrid, workers);
        if (mode is not "static" and not "evolving")
        {
            throw new ArgumentException("Use static or evolving mode.", nameof(mode));
        }
    }

    private static ProcessStartInfo ChildStart(string inputPath, string outputPath, int samples, int workers, string mode, string hash)
    {
        string executable = Environment.ProcessPath ?? throw new IOException("Current executable path is unavailable.");
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        }

        foreach (string argument in new[] { "_measure-p1-case", inputPath, outputPath, samples.ToString(CultureInfo.InvariantCulture),
            workers.ToString(CultureInfo.InvariantCulture), mode, hash })
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }

    private static JsonElement ReadJson(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    /// <summary>Samples managed live memory and whole-process residence outside numerical timing.</summary>
    private static object Memory()
    {
        long managedLiveBytes = GC.GetTotalMemory(forceFullCollection: true);
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        return new
        {
            managedLiveBytes,
            processWorkingSetBytes = process.WorkingSet64,
            processPrivateBytes = process.PrivateMemorySize64,
            processPeakWorkingSetBytes = process.PeakWorkingSet64
        };
    }
}
