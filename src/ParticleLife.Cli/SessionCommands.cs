using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

internal static partial class CommandLine
{
    /// <summary>Runs the shared boundary replay engine, then optionally continues from its terminal state.</summary>
    /// <remarks>Reports retain the original bytes' digest, complete session, explicit overrides and
    /// actual execution segments. Sampling observes a boundary after all events at that boundary;
    /// its radius is fixed for this invocation, independent of subsequent force-cutoff edits.</remarks>
    private static void ReplaySession(string path, Dictionary<string, string> options)
    {
        int continueSteps = Integer(options, "continue-steps", 0);
        int sampleEvery = Integer(options, "sample-every", 100);
        ArgumentOutOfRangeException.ThrowIfNegative(continueSteps);
        ArgumentOutOfRangeException.ThrowIfNegative(sampleEvery);
        byte[] inputBytes = File.ReadAllBytes(path);
        string inputText = ReadExperimentText(inputBytes);
        SessionDefinition session = SessionFile.Deserialize(inputText);
        ForceBackend? backendOverride = options.TryGetValue("backend", out string? backendText) ? ParseBackend(backendText) : null;
        int? workerOverride = options.ContainsKey("workers") ? Integer(options, "workers", 1) : null;
        ReplayOverrides overrides = new(backendOverride, workerOverride);
        double clusterDistance = Number(options, "cluster-distance", session.InitialExperiment.Parameters.Cutoff);
        if (!double.IsFinite(clusterDistance) || clusterDistance <= 0)
        {
            throw new ArgumentException("Cluster distance must be finite and positive.");
        }

        if (options.TryGetValue("output", out string? output))
        {
            PrepareOutput(output);
        }

        using SessionReplayRunner replay = new(session, overrides);
        Simulation simulation = replay.Simulation;
        DiagnosticSampler diagnostics = new();
        List<DiagnosticSample> samples = [];
        List<object> diagnosticFailures = [];
        List<ReplayExecutionSegment> segments = [];
        long diagnosticFallbackSamples = 0;
        DateTimeOffset started = DateTimeOffset.UtcNow;
        if (sampleEvery > 0)
        {
            SampleBoundary();
        }

        Stopwatch elapsed = Stopwatch.StartNew();
        while (!replay.IsComplete)
        {
            replay.AdvanceOneStep();
            RecordExecution(replay.LastCompletedConfiguration!, replay.LastCompletedExecution!.Value);
            SampleIfScheduled();
        }

        ParticleSnapshot replayTerminal = simulation.CaptureSnapshot();
        for (int i = 0; i < continueSteps; i++)
        {
            SimulationConfiguration configuration = simulation.Configuration;
            simulation.Step();
            RecordExecution(configuration, simulation.LastExecution!.Value);
            SampleIfScheduled();
        }

        if (sampleEvery > 0 && (samples.Count == 0 || samples[^1].Step != simulation.Observe().Step))
        {
            SampleBoundary();
        }

        elapsed.Stop();
        ParticleSnapshot final = simulation.CaptureSnapshot();
        object report = new
        {
            sessionReportSchemaVersion = 1,
            startedAtUtc = started,
            completedAtUtc = DateTimeOffset.UtcNow,
            sourceSessionSha256 = Convert.ToHexString(SHA256.HashData(inputBytes)),
            session = JsonSerializer.Deserialize<JsonElement>(inputText),
            replay = new
            {
                recordedTerminalStep = session.TerminalStep,
                appliedEventCount = replay.AppliedEventCount,
                terminalTime = replayTerminal.Time,
                continueSteps,
                backendOverride = backendOverride is ForceBackend backend ? ExperimentFile.GetNeighbourSearchVersion(backend) : null,
                workerCountOverride = workerOverride,
                overridePolicy = "Explicit choices override every recorded configuration. A changed backend without a worker override selects one worker; a retained backend keeps its recorded workers. No override honours all recorded choices.",
                structuralPayloadPolicy = "Structural events restore their full recorded actual state, including surviving particles. Execution overrides do not reinterpret these payloads as an uninterrupted alternate trajectory."
            },
            execution = new
            {
                modelVersion = ExperimentFile.ModelVersion,
                integratorVersion = ExperimentFile.IntegratorVersion,
                currentNeighbourSearchVersion = ExperimentFile.GetNeighbourSearchVersion(simulation.Backend),
                currentWorkerCount = simulation.WorkerCount,
                currentTimeStep = simulation.TimeStep,
                directFallbackSteps = simulation.DirectFallbackSteps,
                coordinateRangeFallbackSteps = simulation.DirectFallbackSteps - simulation.AccumulationFallbackSteps,
                accumulationRangeFallbackSteps = simulation.AccumulationFallbackSteps,
                segments
            },
            diagnostics = new
            {
                algorithmVersion = DiagnosticSampler.AlgorithmVersion,
                directFallbackSamples = diagnosticFallbackSamples,
                boundaryPolicy = "Samples follow all recorded events at the completed boundary.",
                failures = diagnosticFailures
            },
            environment = CaptureEnvironment(simulation.WorkerCount),
            sampleEvery,
            clusterDistance,
            elapsedMillisecondsIncludingReplayAndScheduledDiagnostics = elapsed.Elapsed.TotalMilliseconds,
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

        Console.WriteLine(FormattableString.Invariant($"Replayed {replay.AppliedEventCount} events through boundary {session.TerminalStep}; continued {continueSteps} steps; final step={final.Step}, t={final.Time:G17}, particles={final.Count}."));
        Console.WriteLine($"Execution segments: {segments.Count}; direct fallback steps: {simulation.DirectFallbackSteps}; explicit backend override: {backendOverride?.ToString() ?? "none"}; worker override: {workerOverride?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}.");
        if (output is not null)
        {
            Console.WriteLine($"Saved session replay report: {Path.GetFullPath(output)}");
        }

        void RecordExecution(SimulationConfiguration configuration, ForceExecution execution)
        {
            long step = simulation.Observe().Step;
            if (segments.Count > 0 && segments[^1].Matches(configuration, execution))
            {
                segments[^1].ThroughStepInclusive = step;
                return;
            }

            segments.Add(new ReplayExecutionSegment(step - 1, step, configuration, execution));
        }

        void SampleIfScheduled()
        {
            if (sampleEvery > 0 && simulation.Observe().Step % sampleEvery == 0)
            {
                SampleBoundary();
            }
        }

        void SampleBoundary()
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
                diagnosticFailures.Add(new { simulation.Observe().Time, simulation.Observe().Step, error.Message });
            }
        }
    }

    /// <summary>Compacts adjacent successful steps with identical requested and actual execution.</summary>
    private sealed class ReplayExecutionSegment(long afterStepExclusive, long throughStepInclusive,
        SimulationConfiguration configuration, ForceExecution execution)
    {
        public long AfterStepExclusive { get; } = afterStepExclusive;
        public long ThroughStepInclusive { get; set; } = throughStepInclusive;
        public double TimeStep { get; } = configuration.TimeStep;
        public string RequestedNeighbourSearchVersion { get; } = ExperimentFile.GetNeighbourSearchVersion(configuration.Backend);
        public int RequestedWorkerCount { get; } = configuration.WorkerCount;
        public string ActualNeighbourSearchVersion { get; } = ExperimentFile.GetNeighbourSearchVersion(execution.ActualBackend);
        public string ActualForceAlgorithmVersion { get; } = ExperimentFile.GetForceAlgorithmVersion(execution.ActualBackend);
        public int ActualWorkerCount { get; } = execution.ActualWorkerCount;
        public string FallbackReason { get; } = execution.FallbackReason.ToString();

        internal bool Matches(SimulationConfiguration candidate, ForceExecution actual) =>
            TimeStep == candidate.TimeStep && RequestedWorkerCount == candidate.WorkerCount
            && ActualWorkerCount == actual.ActualWorkerCount
            && RequestedNeighbourSearchVersion == ExperimentFile.GetNeighbourSearchVersion(candidate.Backend)
            && ActualNeighbourSearchVersion == ExperimentFile.GetNeighbourSearchVersion(actual.ActualBackend)
            && FallbackReason == actual.FallbackReason.ToString();
    }
}
