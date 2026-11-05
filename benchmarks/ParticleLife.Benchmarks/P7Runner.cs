using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using ParticleLife.Core.Execution;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Benchmarks;

/// <summary>Bounds actual full-population evolution and continued CPU sampling in one owned child process.</summary>
/// <remarks>Warmup resets occur before evolution only. The optional snapshot is the exact input
/// boundary of the measured continuation, rebased for normal experiment loading. Use that same file
/// with zero further evolution for matched CPU/GPU starts; disclose GPU precision conversion.</remarks>
internal static class P7Runner
{
    private const int MinimumWarmupCalls = 8;
    private const double MinimumWarmupCallSeconds = 0.5;

    internal static int Run(string inputPath, string reportPath, int workers, int evolutionSteps, int samples,
        int deadlineSeconds, string? snapshotPath)
    {
        Validate(workers, evolutionSteps, samples);
        if (deadlineSeconds is < 1 or > 3_600) { throw new ArgumentOutOfRangeException(nameof(deadlineSeconds), "Use a 1..3600 second child deadline."); }
        inputPath = Path.GetFullPath(inputPath);
        reportPath = Path.GetFullPath(reportPath);
        snapshotPath = snapshotPath is null ? null : Path.GetFullPath(snapshotPath);
        string metricsPath = reportPath + ".measurement.json";
        string progressPath = reportPath + ".progress.json";
        string stdoutPath = reportPath + ".stdout.txt", stderrPath = reportPath + ".stderr.txt";
        foreach (string path in new[] { reportPath, metricsPath, progressPath, stdoutPath, stderrPath }.Concat(snapshotPath is null ? [] : new[] { snapshotPath }))
        {
            if (File.Exists(path) || Directory.Exists(path)) { throw new IOException("P7 requires fresh output paths: " + path); }
        }
        if (snapshotPath is not null && new[] { inputPath, reportPath, metricsPath, progressPath, stdoutPath, stderrPath }.Any(path => SamePath(path, snapshotPath)))
        {
            throw new ArgumentException("The optional premeasurement snapshot must have its own new path.");
        }
        string beforeHash = P1Inputs.HashFile(inputPath);
        string? inputHashReadFailure = null;
        WriteEnvelope("running", null, null, null, null, 0);
        ProcessStartInfo start = ChildStart(inputPath, metricsPath, progressPath, workers, evolutionSteps, samples, beforeHash, snapshotPath);
        long began = Stopwatch.GetTimestamp();
        using Process child = Process.Start(start) ?? throw new IOException("P7 measurement child did not start.");
        Task<string> stdout = child.StandardOutput.ReadToEndAsync(), stderr = child.StandardError.ReadToEndAsync();
        bool completed = child.WaitForExit(deadlineSeconds * 1_000);
        if (!completed)
        {
            try { child.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (child.HasExited) { }
            if (!child.WaitForExit(5_000)) { throw new IOException("The owned P7 child did not terminate; stop before starting another workload."); }
        }
        double childWallSeconds = Stopwatch.GetElapsedTime(began).TotalSeconds;
        P7Inputs.WriteTextNew(stdoutPath, stdout.GetAwaiter().GetResult());
        P7Inputs.WriteTextNew(stderrPath, stderr.GetAwaiter().GetResult());
        string? afterHash;
        try { afterHash = P1Inputs.HashFile(inputPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { afterHash = null; inputHashReadFailure = error.Message; }
        bool intact = beforeHash == afterHash;
        string status = inputHashReadFailure is not null ? "failed-input-unreadable" : !intact ? "failed-input-changed" : !completed ? "timed-out"
            : child.ExitCode != 0 ? "failed" : File.Exists(metricsPath) ? "completed" : "failed-missing-measurement";
        JsonElement? measurement = status == "completed" ? ReadJson(metricsPath) : null;
        JsonElement? progress = File.Exists(progressPath) ? ReadJson(progressPath) : null;
        WriteEnvelope(status, child.ExitCode, afterHash, measurement, progress, childWallSeconds);
        Console.WriteLine($"P7 CPU {status}: {reportPath}");
        return status == "completed" ? 0 : status == "timed-out" ? 3 : 2;

        void WriteEnvelope(string reportStatus, int? reportedExitCode, string? reportedAfterHash,
            JsonElement? reportedMeasurement, JsonElement? reportedProgress, double reportedWallSeconds) =>
            P7Inputs.WriteJsonAtomic(reportPath, new
            {
                schemaVersion = 1,
                status = reportStatus,
                updatedAtUtc = DateTimeOffset.UtcNow,
                inputFile = inputPath,
                inputSha256Before = beforeHash,
                inputSha256After = reportedAfterHash,
                inputUnchanged = reportStatus == "running" ? (bool?)null : beforeHash == reportedAfterHash,
                inputHashReadFailure,
                workers,
                requestedGlobalEvolutionSteps = evolutionSteps,
                requestedSamples = samples,
                deadlineSeconds,
                childWallSeconds = reportedWallSeconds,
                childExitCode = reportedExitCode,
                snapshotFile = snapshotPath,
                measurement = reportedMeasurement,
                progress = reportedProgress,
                deadlinePolicy = "One owned child PID and its descendants only. Deadline includes load/hash/environment/warmup/evolution/capture/sampling/reporting, followed by at most five seconds termination grace. Input hash checks by the supervisor are outside this deadline. Exit 0 means completed, 3 means timed out, 2 means failed.",
                partialPolicy = "Progress records only completed warmup/evolution/sample boundaries. An in-flight step is not counted. A timeout never yields a successful timing result; a completed premeasurement capture can remain as an independently hashed artifact. Late child metrics are ignored unless the child exited successfully within its budget.",
                presentation = "Not measured: rendering, input responsiveness and GPU transfer/device timings require separate evidence. Presentation target remains 60 FPS without changing saved rules or population."
            }, overwrite: reportStatus != "running");
    }

    /// <summary>Runs only one child workload; this entry never spawns another supervisor.</summary>
    internal static void MeasureCase(string inputPath, string metricsPath, string progressPath, int workers,
        int evolutionSteps, int samples, string expectedHash, string? snapshotPath)
    {
        Validate(workers, evolutionSteps, samples);
        inputPath = Path.GetFullPath(inputPath);
        metricsPath = Path.GetFullPath(metricsPath);
        progressPath = Path.GetFullPath(progressPath);
        snapshotPath = snapshotPath is null ? null : Path.GetFullPath(snapshotPath);
        string[] outputPaths = snapshotPath is null ? [metricsPath, progressPath] : [metricsPath, progressPath, snapshotPath];
        for (int index = 0; index < outputPaths.Length; index++)
        {
            string path = outputPaths[index];
            if (File.Exists(path) || Directory.Exists(path) || SamePath(path, inputPath)
                || outputPaths.Take(index).Any(previous => SamePath(previous, path)))
            { throw new IOException("The P7 child requires distinct fresh outputs and read-only input: " + path); }
        }
        int warmupCompleted = 0, evolutionCompleted = 0, measuredCompleted = 0;
        object? capture = null;
        Simulation? simulation = null;
        Progress("verifying-input");
        if (P1Inputs.HashFile(inputPath) != expectedHash) { throw new IOException("Input changed before child loading."); }
        object environment = LatencyRunner.CaptureEnvironment("P7 full-world CPU binary64 uniform-grid evolution and continuation", workers);
        object baselineMemory = Memory();
        Progress("loading-input");
        ExperimentDefinition experiment = ExperimentFile.Load(inputPath);
        try
        {
            simulation = experiment.CreateSimulation(ForceBackend.UniformGrid, workers);
            object initialState = P7Inputs.DescribeState(simulation.Observe());
            Progress("warming-up");
            double warmupCallSeconds = 0;
            long warmupBegan = Stopwatch.GetTimestamp();
            do
            {
                long callBegan = Stopwatch.GetTimestamp();
                simulation.Reset(experiment.InitialState);
                simulation.Step();
                warmupCallSeconds += Stopwatch.GetElapsedTime(callBegan).TotalSeconds;
                warmupCompleted++;
                Progress("warming-up");
            }
            while (warmupCompleted < MinimumWarmupCalls || warmupCallSeconds < MinimumWarmupCallSeconds);
            double warmupWallSeconds = Stopwatch.GetElapsedTime(warmupBegan).TotalSeconds;
            // This is the final reset. Every requested global evolution and measured step continues from here.
            simulation.Reset(experiment.InitialState);
            Progress("evolving-globally");
            long evolutionBegan = Stopwatch.GetTimestamp();
            for (int index = 0; index < evolutionSteps; index++)
            {
                simulation.Step();
                evolutionCompleted++;
                Progress("evolving-globally");
            }
            double evolutionWallSeconds = Stopwatch.GetElapsedTime(evolutionBegan).TotalSeconds;
            object measurementStartState = P7Inputs.DescribeState(simulation.Observe());
            long measurementStartStep = simulation.Observe().Step;
            double measurementStartTime = simulation.Observe().Time;
            if (snapshotPath is not null)
            {
                Progress("saving-premeasurement-snapshot");
                capture = SavePremeasurementSnapshot(simulation, experiment, snapshotPath, evolutionCompleted);
            }
            P2Runner.Series series = new(samples);
            Action step = simulation.Step;
            object warmedMemory = Memory();
            long candidates = 0, accepted = 0;
            int fallbackSamples = 0;
            double progressIoSeconds = 0;
            Progress("measuring");
            long began = Stopwatch.GetTimestamp();
            for (int index = 0; index < samples; index++)
            {
                series.Measure(index, step);
                measuredCompleted++;
                candidates += simulation.LastStatistics.CandidatePairs;
                accepted += simulation.LastStatistics.AcceptedPairs;
                if (simulation.LastExecution?.ActualBackend == ForceBackend.Direct) { fallbackSamples++; }
                long progressBegan = Stopwatch.GetTimestamp();
                Progress("measuring");
                progressIoSeconds += Stopwatch.GetElapsedTime(progressBegan).TotalSeconds;
            }
            double samplingWallSeconds = Stopwatch.GetElapsedTime(began).TotalSeconds;
            double completedSimulationTime = simulation.Observe().Time - measurementStartTime;
            long completedStep = simulation.Observe().Step;
            object finalState = P7Inputs.DescribeState(simulation.Observe());
            object finalMemory = Memory();
            if (simulation.Observe().Count != experiment.InitialState.Count) { throw new InvalidOperationException("CPU stepping changed population."); }
            if (P1Inputs.HashFile(inputPath) != expectedHash) { throw new IOException("Input changed during child measurement."); }
            JsonElement stepSeries = JsonSerializer.SerializeToElement(series.Summarize(), P1Inputs.JsonOptions);
            double computeSeconds = stepSeries.GetProperty("milliseconds").EnumerateArray().Sum(value => value.GetDouble()) / 1_000;
            double samplingSecondsExcludingProgressIo = Math.Max(computeSeconds, samplingWallSeconds - progressIoSeconds);
            P7Inputs.WriteJsonAtomic(metricsPath, new
            {
                schemaVersion = 1,
                status = "completed-sampling",
                environment,
                inputFile = Path.GetFullPath(inputPath),
                inputSha256 = expectedHash,
                experiment.Name,
                experiment.Parameters,
                experiment.TimeStep,
                experiment.Generator,
                experiment.Provenance,
                effectiveConfiguration = ConfigurationFile.Encode(simulation.Configuration),
                precision = "IEEE 754 binary64 CPU state/force/integration",
                requestedBackend = ForceBackend.UniformGrid,
                workers,
                actualLastExecution = simulation.LastExecution,
                gridEstimatedRetainedBytes = simulation.LastExecution?.Index.EstimatedRetainedBytes,
                warmupSteps = warmupCompleted,
                minimumWarmupCalls = MinimumWarmupCalls,
                minimumWarmupCallSeconds = MinimumWarmupCallSeconds,
                warmupCallSecondsExcludingProgress = warmupCallSeconds,
                warmupWallSecondsIncludingProgress = warmupWallSeconds,
                warmupPolicy = "At least eight identical-input calls and 0.5 seconds of reset/step work, each reset beforehand, followed by one final reset BEFORE global evolution. The child deadline also bounds warmup. No reset occurs after global evolution, capture or between measured samples. Zero evolution explicitly measures continued steps from the saved input for matched CPU/GPU comparisons.",
                evolutionSteps = evolutionCompleted,
                evolutionWallSecondsIncludingProgress = evolutionWallSeconds,
                initialState,
                measurementStartState,
                finalState,
                capture,
                samples = measuredCompleted,
                measurementStartStep,
                measurementStartTime,
                measurementEndStep = completedStep,
                measuredSimulationTime = completedSimulationTime,
                steps = stepSeries,
                candidates,
                accepted,
                fallbackSamples,
                samplingWallSecondsIncludingProgressIo = samplingWallSeconds,
                progressIoSeconds,
                samplingWallSecondsExcludingProgressIo = samplingSecondsExcludingProgressIo,
                achievedSimulationTimePerWallSecond = completedSimulationTime / samplingSecondsExcludingProgressIo,
                achievedIncludingProgressIo = completedSimulationTime / samplingWallSeconds,
                computeEquivalentSimulationTimePerSecond = measuredCompleted * experiment.TimeStep / computeSeconds,
                completedStepsPerWallSecond = measuredCompleted / samplingSecondsExcludingProgressIo,
                memory = new { baseline = baselineMemory, warmed = warmedMemory, final = finalMemory },
                measurementPolicy = "Nearest-rank step p50/p95 and process-wide precise step allocations reuse P2 Series; parallel workers included. Input/reset, evolution, capture, hash, memory/GC and all progress/report I/O remain outside timed steps. Sampling wall includes bookkeeping; achieved speed excludes the separately measured progress I/O. Short screens are exploratory, not a precise capacity or tail-latency guarantee.",
                comparisonPolicy = "The optional capture is the actual full-population state immediately before measured continuation. For a common CPU/GPU start, reuse that exact file with zero extra evolution on both backends. Its particle values and settings match this CPU boundary, but the experiment clock is rebased to zero with original clock in provenance. GPU float conversion/order may diverge; report that separately."
            }, overwrite: false);
            Progress("completed");
            Console.WriteLine($"P7 measured {measuredCompleted} steps after {evolutionCompleted} actual global steps; p50 {series.Median:F4} ms.");
            GC.KeepAlive(experiment);
        }
        finally { simulation?.Dispose(); }

        void Progress(string stage) => P7Inputs.WriteJsonAtomic(progressPath, new
        {
            stage,
            updatedAtUtc = DateTimeOffset.UtcNow,
            completedWarmupSteps = warmupCompleted,
            completedGlobalEvolutionSteps = evolutionCompleted,
            completedMeasuredSamples = measuredCompleted,
            completedStep = simulation is null ? (long?)null : simulation.Observe().Step,
            completedTime = simulation is null ? (double?)null : simulation.Observe().Time,
            capture
        }, overwrite: true);
    }

    private static object SavePremeasurementSnapshot(Simulation simulation, ExperimentDefinition source, string path, int evolutionSteps)
    {
        long began = Stopwatch.GetTimestamp();
        ParticleStateView state = simulation.Observe();
        ExperimentDefinition snapshot = new(source.Name + " / globally evolved " + evolutionSteps, simulation.Parameters,
            simulation.Matrix, new ParticleSnapshot(state.Ids, state.Positions, state.Velocities, state.Species), simulation.TimeStep,
            new GeneratorMetadata("benchmark/p7/cpu-global-evolution", "1", source.Generator.Seed, RuntimeInformation.FrameworkDescription),
            ForceBackend.UniformGrid, simulation.WorkerCount,
            new ExperimentProvenance(state.Time, state.Step, "Actual full-population CPU evolution before measurement, rebased to zero. Warmup steps were discarded before evolution.",
                source.Generator, source.Name, ExecutionProvenance.Cpu(simulation.Configuration)));
        P7Inputs.SaveExperimentNew(path, snapshot);
        return new
        {
            file = Path.GetFullPath(path),
            sha256 = P1Inputs.HashFile(path),
            sourceStep = state.Step,
            sourceTime = state.Time,
            rebasedStep = 0,
            rebasedTime = 0,
            actualGlobalEvolutionSteps = evolutionSteps,
            snapshotState = P7Inputs.DescribeState(snapshot.InitialState.Observe()),
            snapshot.Provenance,
            captureWallSeconds = Stopwatch.GetElapsedTime(began).TotalSeconds
        };
    }

    private static ProcessStartInfo ChildStart(string input, string metrics, string progress, int workers, int evolution,
        int samples, string hash, string? snapshot)
    {
        string executable = Environment.ProcessPath ?? throw new IOException("The benchmark executable path is unavailable.");
        ProcessStartInfo start = new(executable)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        { start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location); }
        foreach (string argument in new[] { "_measure-p7-cpu", input, metrics, progress, workers.ToString(CultureInfo.InvariantCulture),
            evolution.ToString(CultureInfo.InvariantCulture), samples.ToString(CultureInfo.InvariantCulture), hash, snapshot ?? "" })
        { start.ArgumentList.Add(argument); }
        return start;
    }

    private static void Validate(int workers, int evolutionSteps, int samples)
    {
        ForceWorkerConfiguration.Validate(ForceBackend.UniformGrid, workers);
        if (evolutionSteps is < 0 or > 120 || samples is < 5 or > 1_000)
        { throw new ArgumentException("P7 accepts 0..120 full-population evolution steps and 5..1000 measured continuation samples."); }
    }

    private static bool SamePath(string left, string right) => string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static JsonElement ReadJson(string path)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.Clone();
    }

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
            processPeakWorkingSetBytes = process.PeakWorkingSet64,
            meaning = "Whole process and live managed bytes after full GC; includes saved input, simulation buffers, workers and benchmark tooling, not isolated solver-only memory."
        };
    }
}
