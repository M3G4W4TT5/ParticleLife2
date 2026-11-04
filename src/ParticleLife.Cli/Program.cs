using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

return CommandLine.Run(args);

internal static class CommandLine
{
    private static readonly JsonSerializerOptions ReportOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    internal static int Run(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            {
                PrintHelp();
                return 0;
            }

            switch (args[0])
            {
                case "create":
                    Create(ParseOptions(args, 1, ["preset", "particles", "species", "seed", "dt", "backend", "workers", "output"]));
                    break;
                case "validate":
                    if (args.Length != 2)
                    {
                        throw new ArgumentException("Usage: validate <experiment.json>");
                    }

                    ExperimentDefinition experiment = ExperimentFile.Load(args[1]);
                    Console.WriteLine($"Valid experiment: {experiment.Name}, {experiment.InitialState.Count} particles, {experiment.Matrix.SpeciesCount} species; search {ExperimentFile.GetNeighbourSearchVersion(experiment.Backend)}.");
                    break;
                case "run":
                    if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException("Usage: run <experiment.json> --steps N [--sample-every N] [--cluster-distance R] [--output result.json]");
                    }

                    RunExperiment(args[1], ParseOptions(args, 2, ["steps", "sample-every", "cluster-distance", "backend", "workers", "output"]));
                    break;
                default:
                    throw new ArgumentException($"Unknown command '{args[0]}'. Use --help.");
            }

            return 0;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException
            or JsonException or NotSupportedException or InvalidOperationException or ArithmeticException)
        {
            Console.Error.WriteLine($"Error: {error.Message}");
            return 1;
        }
    }

    private static void Create(Dictionary<string, string> options)
    {
        string presetText = Get(options, "preset", "random");
        PresetKind preset = presetText switch
        {
            "random" => PresetKind.Random,
            "chains" => PresetKind.Chains,
            "snakes" => PresetKind.Snakes,
            _ => throw new ArgumentException("Preset must be random, chains or snakes.")
        };
        string output = Required(options, "output");
        ExperimentDefinition experiment = PresetGenerator.Create(preset,
            Integer(options, "particles", 500), Integer(options, "species", 7), Integer(options, "seed", 1),
            timeStep: Number(options, "dt", 1d / 144), backend: ParseBackend(Get(options, "backend", "grid")),
            workerCount: Integer(options, "workers", 1));
        PrepareOutput(output);
        WriteNewOutput(output, ExperimentFile.Serialize(experiment));
        Console.WriteLine($"Saved {experiment.Name}: {experiment.InitialState.Count} particles, {experiment.Matrix.SpeciesCount} species to {Path.GetFullPath(output)}");
    }

    private static void RunExperiment(string path, Dictionary<string, string> options)
    {
        _ = Required(options, "steps");
        int steps = Integer(options, "steps", 0);
        int sampleEvery = Integer(options, "sample-every", 100);
        ArgumentOutOfRangeException.ThrowIfNegative(steps);
        ArgumentOutOfRangeException.ThrowIfNegative(sampleEvery);
        byte[] inputBytes = File.ReadAllBytes(path);
        string inputText = ReadExperimentText(inputBytes);
        ExperimentDefinition experiment = ExperimentFile.Deserialize(inputText);
        ForceBackend? backendOverride = options.TryGetValue("backend", out string? backendText) ? ParseBackend(backendText) : null;
        int? workerOverride = options.ContainsKey("workers") ? Integer(options, "workers", 1) : null;
        double clusterDistance = Number(options, "cluster-distance", experiment.Parameters.Cutoff);
        if (!double.IsFinite(clusterDistance) || clusterDistance <= 0)
        {
            throw new ArgumentException("Cluster distance must be finite and positive.");
        }

        if (options.TryGetValue("output", out string? output))
        {
            PrepareOutput(output);
        }

        using Simulation simulation = experiment.CreateSimulation(backendOverride, workerOverride);
        DiagnosticSampler diagnostics = new();
        List<DiagnosticSample> samples = [];
        List<object> diagnosticFailures = [];
        long diagnosticFallbackSamples = 0;
        DateTimeOffset started = DateTimeOffset.UtcNow;
        if (sampleEvery > 0)
        {
            SampleCompletedState();
        }

        Stopwatch elapsed = Stopwatch.StartNew();
        for (int i = 0; i < steps; i++)
        {
            simulation.Step();
            // Sampling is outside Step; it must never change the equations or output state.
            if (sampleEvery > 0 && ((i + 1) % sampleEvery == 0 || i + 1 == steps))
            {
                SampleCompletedState();
            }
        }

        elapsed.Stop();
        DateTimeOffset completed = DateTimeOffset.UtcNow;
        ParticleSnapshot final = simulation.CaptureSnapshot();
        object report = new
        {
            reportSchemaVersion = 2,
            startedAtUtc = started,
            completedAtUtc = completed,
            sourceExperimentSha256 = Convert.ToHexString(SHA256.HashData(inputBytes)),
            experiment = JsonSerializer.Deserialize<JsonElement>(inputText),
            execution = new
            {
                modelVersion = ExperimentFile.ModelVersion,
                integratorVersion = ExperimentFile.IntegratorVersion,
                savedNeighbourSearchVersion = ExperimentFile.GetNeighbourSearchVersion(experiment.Backend),
                requestedNeighbourSearchVersion = ExperimentFile.GetNeighbourSearchVersion(simulation.Backend),
                requestedForceAlgorithmVersion = ExperimentFile.GetForceAlgorithmVersion(simulation.Backend),
                savedWorkerCount = experiment.WorkerCount,
                configuredWorkerCount = simulation.WorkerCount,
                workerCountOverride = workerOverride,
                backendOverride = backendOverride is ForceBackend selected ? ExperimentFile.GetNeighbourSearchVersion(selected) : null,
                directFallbackSteps = simulation.DirectFallbackSteps,
                directFallbackByReason = new
                {
                    coordinateRangeSteps = simulation.DirectFallbackSteps - simulation.AccumulationFallbackSteps,
                    accumulationRangeSteps = simulation.AccumulationFallbackSteps
                },
                lastCompletedStep = simulation.LastExecution is ForceExecution execution ? new
                {
                    actualNeighbourSearchVersion = ExperimentFile.GetNeighbourSearchVersion(execution.ActualBackend),
                    actualForceAlgorithmVersion = ExperimentFile.GetForceAlgorithmVersion(execution.ActualBackend),
                    execution.ActualWorkerCount,
                    fallbackReason = execution.FallbackReason.ToString(),
                    index = execution.Index
                } : null
            },
            diagnostics = new
            {
                algorithmVersion = DiagnosticSampler.AlgorithmVersion,
                directFallbackSamples = diagnosticFallbackSamples,
                lastSuccessfulExecution = samples.Count > 0 ? (DiagnosticExecution?)diagnostics.LastExecution : null,
                failures = diagnosticFailures
            },
            environment = CaptureEnvironment(simulation.WorkerCount),
            requestedSteps = steps,
            sampleEvery,
            clusterDistance,
            elapsedMillisecondsIncludingScheduledDiagnostics = elapsed.Elapsed.TotalMilliseconds,
            lastForceEvaluation = steps == 0 ? (ForceEvaluationStatistics?)null : simulation.LastStatistics,
            measurements = samples,
            finalState = new
            {
                final.Time,
                final.Step,
                ids = final.Ids.ToArray(),
                positions = final.Positions.ToArray().Select(v => new { v.X, v.Y }),
                velocities = final.Velocities.ToArray().Select(v => new { v.X, v.Y }),
                species = final.Species.ToArray()
            }
        };

        if (output is not null)
        {
            WriteNewOutput(output, JsonSerializer.Serialize(report, ReportOptions));
        }

        Console.WriteLine(FormattableString.Invariant($"Completed {steps} steps; t={final.Time:G17}; particles={final.Count}; elapsed={elapsed.Elapsed.TotalMilliseconds:F3} ms (including scheduled diagnostics)."));
        string actual = simulation.LastExecution is ForceExecution last
            ? $"{ExperimentFile.GetNeighbourSearchVersion(last.ActualBackend)}; fallback reason: {last.FallbackReason}"
            : "none (zero completed steps)";
        Console.WriteLine($"Search requested: {ExperimentFile.GetNeighbourSearchVersion(simulation.Backend)}; last actual: {actual}; " +
            $"direct fallback steps: {simulation.DirectFallbackSteps} (coordinate range: {simulation.DirectFallbackSteps - simulation.AccumulationFallbackSteps}; " +
            $"accumulation range: {simulation.AccumulationFallbackSteps}).");
        Console.WriteLine($"Model: {ExperimentFile.ModelVersion}; integrator: {ExperimentFile.IntegratorVersion}; runtime: {RuntimeInformation.FrameworkDescription}");
        if (samples.Count > 0)
        {
            Console.WriteLine(JsonSerializer.Serialize(samples[^1], ReportOptions));
        }

        if (output is not null)
        {
            Console.WriteLine($"Saved run report: {Path.GetFullPath(output)}");
        }

        void SampleCompletedState()
        {
            try
            {
                samples.Add(diagnostics.Sample(simulation.Observe(), clusterDistance));
                if (diagnostics.LastExecution.FallbackReason != DiagnosticFallbackReason.None)
                {
                    diagnosticFallbackSamples++;
                }
            }
            catch (Exception error) when (error is InvalidOperationException or ArithmeticException)
            {
                // Optional measurement failure must not suppress otherwise valid numerical steps.
                diagnosticFailures.Add(new { simulation.Observe().Time, simulation.Observe().Step, error.Message });
            }
        }
    }

    private static object CaptureEnvironment(int configuredWorkerCount)
    {
        string checkoutStatus = ReadCommand("git", "status", "--porcelain");
        return new
        {
            runtime = RuntimeInformation.FrameworkDescription,
            sdk = ReadCommand("dotnet", "--version"),
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            logicalProcessors = Environment.ProcessorCount,
            simulationThreads = configuredWorkerCount,
            coreVersion = typeof(Simulation).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            coreAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Simulation).Assembly.Location))),
            checkoutCommitAtRun = ReadCommand("git", "rev-parse", "HEAD"),
            workingDirectoryChanges = checkoutStatus == "unavailable" ? (bool?)null : checkoutStatus.Length != 0,
#if DEBUG
            buildConfiguration = "Debug"
#else
        buildConfiguration = "Release"
#endif
        };
    }

    // Process probing and metadata capture run after integration, never inside a step.
    private static string ReadCommand(string executable, params string[] arguments)
    {
        try
        {
            ProcessStartInfo info = new(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using Process? process = Process.Start(info);
            if (process is null)
            {
                return "unavailable";
            }

            string value = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? value.Trim() : "unavailable";
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "unavailable";
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args, int start, string[] allowed)
    {
        Dictionary<string, string> options = new(StringComparer.Ordinal);
        for (int i = start; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length
                || !allowed.Contains(args[i][2..], StringComparer.Ordinal))
            {
                throw new ArgumentException($"Unknown or incomplete option '{args[i]}'. Use --help.");
            }

            if (!options.TryAdd(args[i][2..], args[i + 1]))
            {
                throw new ArgumentException($"Duplicate option '{args[i]}'.");
            }
        }

        return options;
    }

    private static string Required(Dictionary<string, string> options, string key) =>
        options.TryGetValue(key, out string? value) ? value : throw new ArgumentException($"Missing --{key}.");

    private static string Get(Dictionary<string, string> options, string key, string fallback) => options.GetValueOrDefault(key, fallback);

    private static ForceBackend ParseBackend(string value) => value switch
    {
        "direct" => ForceBackend.Direct,
        "grid" => ForceBackend.UniformGrid,
        _ => throw new ArgumentException("Backend must be direct or grid.")
    };

    private static int Integer(Dictionary<string, string> options, string key, int fallback) =>
        !options.TryGetValue(key, out string? value) ? fallback : int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result : throw new ArgumentException($"--{key} requires an integer.");

    private static double Number(Dictionary<string, string> options, string key, double fallback) =>
        !options.TryGetValue(key, out string? value) ? fallback : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) && double.IsFinite(result)
            ? result : throw new ArgumentException($"--{key} requires a finite number in invariant notation.");

    private static void PrepareOutput(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
        {
            throw new IOException($"Output already exists: {fullPath}. Choose a new path or explicitly remove it first.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
    }

    private static string ReadExperimentText(byte[] inputBytes)
    {
        // Match ExperimentFile.Load's BOM-aware File.ReadAllText policy without rereading the file;
        // the captured bytes remain authoritative for both decoding and the source digest.
        using MemoryStream stream = new(inputBytes, writable: false);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static void WriteNewOutput(string path, string text)
    {
        // Exclusive creation also protects a file created after the early PrepareOutput check.
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using StreamWriter writer = new(stream);
        writer.Write(text);
        writer.Write('\n');
    }

    private static void PrintHelp() => Console.WriteLine("""
        Particle Life 2 — headless, double-precision experiments

        create --output experiment.json [--preset random|chains|snakes]
               [--particles 500] [--species 7] [--seed 1] [--dt 0.006944444444444444]
               [--backend grid|direct] [--workers 1]
        validate experiment.json
        run experiment.json --steps N [--sample-every 100] [--cluster-distance 0.1]
                            [--backend direct|grid] [--workers 1] [--output result.json]

        JSON experiments contain actual initial state, directed matrix values, parameters and provenance.
        New CLI experiments default to grid search. Runs honour the saved backend unless overridden.
        Grid workers are explicit (1..8 including the caller); direct requires one. Default is serial.
        Legacy schema-1 experiments retain direct search. Reports retain source and effective settings,
        last actual search and direct fallback counts for coordinate or accumulation range limits.
        Edit validated JSON parameters for other cutoffs, soft walls or disabled damping (null half-life).
        Output files must not already exist. Reports include the original experiment and final state;
        reports are not checkpoint inputs. Set --sample-every 0 to disable exact spatial diagnostics.
        Seeds alone do not guarantee identical generation or trajectories across runtimes.
        """);
}
