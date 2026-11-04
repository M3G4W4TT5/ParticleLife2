using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using SimulationDiagnostics = ParticleLife.Core.Experiments.Diagnostics;

namespace ParticleLife.Benchmarks;

/// <summary>Samples isolated complete steps and separately timed numerical stages without reporting in the hot path.</summary>
internal static class LatencyRunner
{
    private const int WarmupSteps = 64;
    private const double MinimumWarmupSeconds = 1;

    internal static void Run(string outputDirectory, int samples, string selection)
    {
        if (samples is < 5 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(samples), "Use between 5 and 10000 samples.");
        }

        (string Key, ExperimentDefinition Experiment)[] workloads = File.Exists(selection)
            ? [(Path.GetFileNameWithoutExtension(selection), ExperimentFile.Load(selection))]
            : BenchmarkWorkload.Select(selection).Select(name => (name, BenchmarkWorkload.Create(name))).ToArray();
        Directory.CreateDirectory(outputDirectory);
        object environment = CaptureEnvironment();
        List<object> results = [];
        foreach ((string caseName, ExperimentDefinition experiment) in workloads)
        {
            Console.WriteLine($"Measuring {caseName}: at least {WarmupSteps} warm-up steps and {MinimumWarmupSeconds} second, {samples} samples.");
            string inputPath = Path.Combine(outputDirectory, caseName + ".experiment.json");
            ExperimentFile.Save(inputPath, experiment);
            object result = Measure(experiment, inputPath, samples);
            results.Add(result);
            Console.WriteLine(JsonSerializer.Serialize(result));
        }

        object report = new
        {
            schemaVersion = 1,
            measuredAtUtc = DateTimeOffset.UtcNow,
            environment,
            method = "Identical initial state restored outside each timed step; separate phase samples; nearest-rank percentiles.",
            minimumWarmupSteps = WarmupSteps,
            minimumWarmupSeconds = MinimumWarmupSeconds,
            sampleCount = samples,
            indexBuild = "Not applicable: direct all-pairs reference has no spatial index.",
            numericalComparison = "Split public stages versus Simulation.Step on identical input; not an independent force-law oracle or optimized backend comparison.",
            results
        };
        string reportPath = Path.Combine(outputDirectory, "latency.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Report: {Path.GetFullPath(reportPath)}");
    }

    /// <remarks>
    /// Every step starts from the same state. Reset, allocation counters, percentile calculation,
    /// serialization and diagnostics are outside complete-step timing. Diagnostics get five separate
    /// samples at most, because their pairwise connected-component calculation is intentionally expensive.
    /// Stage timings include their public API validation and need not sum exactly to whole-step timings.
    /// </remarks>
    private static object Measure(ExperimentDefinition experiment, string inputPath, int samples)
    {
        ParticleSnapshot initial = experiment.InitialState;
        using Simulation simulation = new(initial, experiment.Parameters, experiment.Matrix, experiment.TimeStep);
        DirectForceEvaluator evaluator = new(experiment.Parameters, experiment.Matrix);
        Vector2d[] forces = new Vector2d[initial.Count];
        Vector2d[] outputPositions = new Vector2d[initial.Count];
        Vector2d[] outputVelocities = new Vector2d[initial.Count];
        double damping = SemiImplicitEuler.DampingFactor(experiment.TimeStep, experiment.Parameters.DampingHalfLife);
        int warmupCount = 0;
        long warmupStarted = Stopwatch.GetTimestamp();
        do
        {
            simulation.Reset(initial);
            simulation.Step();
            evaluator.Evaluate(initial.Observe(), forces);
            SemiImplicitEuler.Integrate(initial.Observe(), forces, experiment.TimeStep, damping, outputPositions, outputVelocities);
            warmupCount++;
        }
        while (warmupCount < WarmupSteps || Stopwatch.GetElapsedTime(warmupStarted).TotalSeconds < MinimumWarmupSeconds);

        long[] stepTicks = new long[samples];
        long[] forceTicks = new long[samples];
        long[] integrationTicks = new long[samples];
        long stepBytes = 0;
        long forceBytes = 0;
        long integrationBytes = 0;
        for (int i = 0; i < samples; i++)
        {
            simulation.Reset(initial);
            long before = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            simulation.Step();
            stepTicks[i] = Stopwatch.GetTimestamp() - start;
            stepBytes += GC.GetAllocatedBytesForCurrentThread() - before;

            before = GC.GetAllocatedBytesForCurrentThread();
            start = Stopwatch.GetTimestamp();
            evaluator.Evaluate(initial.Observe(), forces);
            forceTicks[i] = Stopwatch.GetTimestamp() - start;
            forceBytes += GC.GetAllocatedBytesForCurrentThread() - before;

            before = GC.GetAllocatedBytesForCurrentThread();
            start = Stopwatch.GetTimestamp();
            SemiImplicitEuler.Integrate(initial.Observe(), forces, experiment.TimeStep, damping, outputPositions, outputVelocities);
            integrationTicks[i] = Stopwatch.GetTimestamp() - start;
            integrationBytes += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        int diagnosticSamples = Math.Min(samples, 5);
        long[] diagnosticTicks = new long[diagnosticSamples];
        double clusterDistance = experiment.Parameters.Cutoff;
        _ = SimulationDiagnostics.Sample(initial.Observe(), clusterDistance);
        long diagnosticBytes = 0;
        for (int i = 0; i < diagnosticSamples; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            _ = SimulationDiagnostics.Sample(initial.Observe(), clusterDistance);
            diagnosticTicks[i] = Stopwatch.GetTimestamp() - start;
            diagnosticBytes += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        ParticleStateView completed = simulation.Observe();
        double positionDifference = 0;
        double velocityDifference = 0;
        for (int i = 0; i < initial.Count; i++)
        {
            positionDifference = Math.Max(positionDifference, (completed.Positions[i] - outputPositions[i]).Length);
            velocityDifference = Math.Max(velocityDifference, (completed.Velocities[i] - outputVelocities[i]).Length);
        }

        return new
        {
            name = experiment.Name,
            particleCount = initial.Count,
            speciesCount = experiment.Matrix.SpeciesCount,
            parameters = experiment.Parameters,
            timeStep = experiment.TimeStep,
            generator = experiment.Generator,
            inputFile = Path.GetFileName(inputPath),
            inputSha256 = HashFile(inputPath),
            warmupCount,
            step = Summarize(stepTicks, stepBytes),
            forceIncludingWalls = Summarize(forceTicks, forceBytes),
            integration = Summarize(integrationTicks, integrationBytes),
            diagnostics = Summarize(diagnosticTicks, diagnosticBytes),
            diagnosticClusterDistance = clusterDistance,
            candidateDirectedPairs = simulation.LastStatistics.CandidatePairs,
            acceptedDirectedPairs = simulation.LastStatistics.AcceptedPairs,
            coincidentUnorderedPairs = simulation.LastStatistics.OverlapPairs,
            maxPositionDifferenceFromSplitStages = positionDifference,
            maxVelocityDifferenceFromSplitStages = velocityDifference
        };
    }

    private static object Summarize(long[] ticks, long allocatedBytes)
    {
        double[] milliseconds = ticks.Select(tick => 1000d * tick / Stopwatch.Frequency).ToArray();
        double[] sorted = (double[])milliseconds.Clone();
        Array.Sort(sorted);
        return new
        {
            samples = ticks.Length,
            p50Milliseconds = sorted[(int)Math.Ceiling(0.5 * sorted.Length) - 1],
            p95Milliseconds = sorted[(int)Math.Ceiling(0.95 * sorted.Length) - 1],
            meanMilliseconds = sorted.Average(),
            allocatedBytes,
            allocatedBytesPerSample = (double)allocatedBytes / ticks.Length,
            milliseconds
        };
    }

    internal static object CaptureEnvironment(string backend = "direct-serial-double/semi-implicit-euler-v1", int? simulationThreads = 1)
    {
        string status = ReadCommand("git", "status", "--porcelain");
        return new
        {
            commit = ReadCommand("git", "rev-parse", "HEAD"),
            workingTreeDirty = status == "unavailable" ? (bool?)null : status.Length != 0,
            coreAssemblySha256 = HashFile(typeof(Simulation).Assembly.Location),
            benchmarkAssemblySha256 = HashFile(Assembly.GetExecutingAssembly().Location),
            sdk = ReadCommand("dotnet", "--version"),
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            cpu = CpuName(),
            logicalProcessorCount = Environment.ProcessorCount,
            simulationThreads,
#if DEBUG
            configuration = "Debug",
#else
            configuration = "Release",
#endif
            serverGc = GCSettings.IsServerGC,
            stopwatchFrequency = Stopwatch.Frequency,
            tieredCompilationOverride = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            runtimeOverrides = new[]
            {
                "DOTNET_TieredPGO", "DOTNET_TC_QuickJit", "DOTNET_TC_QuickJitForLoops", "DOTNET_ReadyToRun",
                "COMPlus_TieredCompilation", "COMPlus_TieredPGO", "COMPlus_ReadyToRun", "DOTNET_gcServer"
            }.ToDictionary(name => name, Environment.GetEnvironmentVariable),
            powerPlan = OperatingSystem.IsWindows() ? ReadCommand("powercfg", "/getactivescheme") : "not captured on this OS",
            processPriority = Process.GetCurrentProcess().PriorityClass.ToString(),
            backend
        };
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string CpuName()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null)?.ToString()?.Trim()
                    ?? Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unavailable";
            }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException)
            {
                return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unavailable";
            }
        }

        if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
        {
            string? line = File.ReadLines("/proc/cpuinfo").FirstOrDefault(value => value.StartsWith("model name", StringComparison.Ordinal));
            return line is null ? "unavailable" : line[(line.IndexOf(':') + 1)..].Trim();
        }

        return OperatingSystem.IsMacOS() ? ReadCommand("sysctl", "-n", "machdep.cpu.brand_string") : "unavailable";
    }

    private static string ReadCommand(string fileName, params string[] arguments)
    {
        try
        {
            ProcessStartInfo startInfo = new(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return "unavailable";
            }

            if (!process.WaitForExit(10_000))
            {
                process.Kill();
                return "unavailable";
            }

            return process.ExitCode == 0 ? process.StandardOutput.ReadToEnd().Trim() : "unavailable";
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "unavailable";
        }
    }
}
