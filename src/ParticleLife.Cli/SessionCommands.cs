using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ParticleLife.Core.Execution;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

internal static partial class CommandLine
{
    /// <summary>Restores an explicitly selected actual capture or runs shared action replay, then optionally continues on CPU.</summary>
    /// <remarks>Reports retain the original bytes' digest, complete session, explicit overrides and
    /// actual execution segments. Capture restoration preserves the exact recorded clock without
    /// applying actions; action re-simulation uses the shared runner, including identity deltas and
    /// terminal edits. Sampling observes the selected completed boundary; its radius is fixed for
    /// this invocation, independent of subsequent force-cutoff edits. No GPU adapter is loaded.</remarks>
    private static void ReplaySession(string path, Dictionary<string, string> options)
    {
        int continueSteps = Integer(options, "continue-steps", 0);
        int sampleEvery = Integer(options, "sample-every", 100);
        ArgumentOutOfRangeException.ThrowIfNegative(continueSteps);
        ArgumentOutOfRangeException.ThrowIfNegative(sampleEvery);
        byte[] inputBytes = File.ReadAllBytes(path);
        string inputText = ReadExperimentText(inputBytes);
        bool restoreCapture = options.ContainsKey("restore-capture");
        bool resimulateOnCpu = options.ContainsKey("resimulate-on-cpu");
        CapturedSessionDefinition? captured = null;
        SessionDefinition session;
        if (IsCapturedSessionText(inputText))
        {
            if (restoreCapture == resimulateOnCpu)
            {
                throw new ArgumentException("Captured sessions require exactly one explicit mode: --restore-capture or --resimulate-on-cpu. Restoring saved values and repeating actions are different operations.");
            }

            captured = ExecutionCaptureFile.Deserialize(inputText);
            session = captured.Actions;
        }
        else
        {
            if (restoreCapture || resimulateOnCpu)
            {
                throw new ArgumentException("--restore-capture and --resimulate-on-cpu require a particle-life-captured-session file. Ordinary action sessions use replay without a capture mode.");
            }

            session = SessionFile.Deserialize(inputText);
        }

        ForceBackend? backendOverride = options.TryGetValue("backend", out string? backendText) ? ParseBackend(backendText) : null;
        int? workerOverride = options.ContainsKey("workers") ? Integer(options, "workers", 1) : null;
        ReplayOverrides overrides = new(backendOverride, workerOverride);
        double clusterDistance = Number(options, "cluster-distance", restoreCapture
            ? captured!.TerminalCapture.Configuration.Parameters.Cutoff : session.InitialExperiment.Parameters.Cutoff);
        if (!double.IsFinite(clusterDistance) || clusterDistance <= 0)
        {
            throw new ArgumentException("Cluster distance must be finite and positive.");
        }

        if (options.TryGetValue("output", out string? output))
        {
            PrepareOutput(output);
        }

        using SessionReplayRunner? replay = restoreCapture ? null : new(session, overrides);
        using Simulation? restored = restoreCapture ? RestoreOnCpu(captured!.TerminalCapture, overrides) : null;
        Simulation simulation = restored ?? replay!.Simulation;
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
        while (replay is not null && !replay.IsComplete)
        {
            replay.AdvanceOneStep();
            RecordExecution(replay.LastCompletedConfiguration!, replay.LastCompletedExecution!.Value);
            SampleIfScheduled();
        }

        ParticleSnapshot replayTerminal = simulation.CaptureSnapshot();
        JsonElement terminalConfiguration = ConfigurationFile.Encode(simulation.Configuration);
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
            session = captured is null ? JsonSerializer.Deserialize<JsonElement>(inputText)
                : JsonSerializer.Deserialize<JsonElement>(SessionFile.Serialize(session)),
            capturedSession = captured is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(inputText),
            captureReplay = captured is null ? null : new
            {
                mode = restoreCapture ? "restore-capture" : "resimulate-on-cpu",
                sourceFormat = ExecutionCaptureFile.Format,
                sourceSchemaVersion = ExecutionCaptureFile.SchemaVersion,
                sourceTerminalStep = captured.TerminalCapture.State.Step,
                sourceTerminalTime = captured.TerminalCapture.State.Time,
                sourceAppliedEventCount = captured.TerminalCapture.AppliedEventCount,
                sourceCaptureReason = captured.TerminalCapture.Reason,
                sourceExecution = DescribeExecution(captured.TerminalCapture.Execution),
                selectedExecution = DescribeExecution(ExecutionProvenance.Cpu(simulation.Configuration)),
                sourceTerminalConfiguration = ConfigurationFile.Encode(captured.TerminalCapture.Configuration),
                effectiveTerminalConfiguration = terminalConfiguration,
                restoredValuesExactly = restoreCapture,
                actionsResimulated = resimulateOnCpu,
                policy = restoreCapture
                    ? "Restores authoritative terminal state, IDs, species and exact host clock; widened binary32 values retain their exact stored values. Optional continuation uses CPU binary64 arithmetic and does not reproduce the recorded GPU trajectory."
                    : "Explicit CPU binary64 re-simulation of embedded actions with the shared session replay engine. Full structural payloads and identity deltas retain their recorded meaning. Captures do not replace the resimulated trajectory; equality with the recorded GPU trajectory is not promised."
            },
            replay = new
            {
                recordedTerminalStep = session.TerminalStep,
                appliedEventCount = replay?.AppliedEventCount ?? 0,
                continuedFromStep = replayTerminal.Step,
                terminalTime = replayTerminal.Time,
                continueSteps,
                backendOverride = backendOverride is ForceBackend backend ? ExperimentFile.GetNeighbourSearchVersion(backend) : null,
                workerCountOverride = workerOverride,
                overridePolicy = "Explicit choices override restored terminal settings or every resimulated configuration. A changed backend without a worker override selects one worker; a retained backend keeps its recorded workers. No override honours recorded CPU search/worker choices.",
                structuralPayloadPolicy = "Action replay applies recorded full structural replacements or identity deltas through the shared engine. Execution overrides do not reinterpret full payloads as uninterrupted alternate trajectories. Capture restore applies no actions."
            },
            execution = new
            {
                modelVersion = ExperimentFile.ModelVersion,
                integratorVersion = ExperimentFile.IntegratorVersion,
                currentNeighbourSearchVersion = ExperimentFile.GetNeighbourSearchVersion(simulation.Backend),
                currentWorkerCount = simulation.WorkerCount,
                currentTimeStep = simulation.TimeStep,
                currentConfiguration = ConfigurationFile.Encode(simulation.Configuration),
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

        Console.WriteLine(restoreCapture
            ? FormattableString.Invariant($"Restored actual terminal capture on CPU through boundary {session.TerminalStep}; continued {continueSteps} steps; final step={final.Step}, t={final.Time:G17}, particles={final.Count}.")
            : FormattableString.Invariant($"Replayed {replay!.AppliedEventCount} events through boundary {session.TerminalStep}; continued {continueSteps} steps; final step={final.Step}, t={final.Time:G17}, particles={final.Count}."));
        if (captured is not null)
        {
            Console.WriteLine($"Capture mode: {(restoreCapture ? "restore-capture" : "resimulate-on-cpu")}; source {captured.TerminalCapture.Execution.Hardware}/{captured.TerminalCapture.Execution.Precision}; selected CPU/Float64. Recorded GPU trajectories are not promised by CPU continuation or re-simulation.");
        }
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

    /// <summary>Distinguishes the canonical capture container without executing actions or loading graphics.</summary>
    private static bool IsCapturedSessionText(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("format", out JsonElement format)
            && format.ValueKind == JsonValueKind.String && format.GetString() == ExecutionCaptureFile.Format;
    }

    /// <summary>Starts CPU ownership at the actual captured boundary without rebasing its clock or values.</summary>
    private static Simulation RestoreOnCpu(SimulationCapture capture, ReplayOverrides overrides)
    {
        SimulationConfiguration configuration = overrides.Apply(capture.Configuration);
        ParticleSnapshot state = CaptureTransfer.Convert(capture.State, StatePrecision.Float64);
        return new(state, configuration.Parameters, configuration.Matrix, configuration.TimeStep,
            configuration.Backend, configuration.WorkerCount);
    }

    private static object DescribeExecution(ExecutionProvenance execution) => new
    {
        hardware = execution.Hardware.ToString().ToLowerInvariant(),
        precision = execution.Precision.ToString().ToLowerInvariant(),
        execution.Implementation,
        execution.Runtime,
        execution.Device,
        execution.Api,
        execution.Driver,
        execution.RepeatableActions
    };

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
