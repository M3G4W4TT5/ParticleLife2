using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ParticleLife.Core.Execution;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using ParticleLife.MonoGame.Rendering;
using ParticleLife.Viewer;

namespace ParticleLife.MonoGame.Gpu;

/// <summary>Measures separate resident simulation, draw, optional observation and explicit capture phases.</summary>
/// <remarks>The caller owns the current context and advances the iterator under a watchdog. Warmup
/// repeats the identical converted input for at least 128 completed steps and half a second, followed
/// by one final upload/reset outside measurement. Seven samples then continue without resetting.
/// All polling has a zero native timeout. Render-only stages use the unchanged final resident state;
/// histogram readback and full capture are explicit subsequent phases, not hidden normal-frame work.</remarks>
internal sealed class GpuCapacityRun(GraphicsDevice device, ExperimentDefinition input, string inputHash,
    string outputDirectory, Action<string> progress, bool profileFirstStep = false)
{
    private const int Samples = 7, MinimumWarmupSteps = 128;
    private const double MinimumWarmupSeconds = 0.5;
    internal List<object> Evidence { get; } = [];
    internal string Phase { get; private set; } = "created";
    internal int WarmupSteps { get; private set; }
    internal int MeasuredSteps { get; private set; }
    private GlGpuSimulation? activeSimulation;
    internal GpuStepMeasurement? LastCompletedMeasurement { get; private set; }
    internal object? SubmissionProgress => activeSimulation?.DescribeSubmissionProgress();

    internal IEnumerable<bool> Execute()
    {
        SetPhase("converting-explicit-float32-input");
        object baselineMemory = Memory();
        long began = Stopwatch.GetTimestamp();
        ParticleSnapshot converted = CaptureTransfer.Convert(input.InitialState, StatePrecision.Float32, allowPrecisionConversion: true);
        double conversionMilliseconds = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
        Evidence.Add(DescribeConversion(input.InitialState, converted, conversionMilliseconds));
        SimulationConfiguration configuration = new(input.Parameters, input.Matrix, input.TimeStep, input.Backend, input.WorkerCount);
        GpuModelData model = new(configuration);
        Evidence.Add(new
        {
            Case = "actual-float-model",
            requestedConfiguration = ConfigurationFile.Encode(configuration),
            model.Width,
            model.Height,
            model.Cutoff,
            model.CellWidth,
            model.CellHeight,
            model.CellsX,
            model.CellsY,
            model.TimeStep,
            model.Damping,
            model.ForceScale,
            model.WallWidth,
            model.WallStrength,
            rules = model.Rules.Select(rule => new { rule.Coefficient, rule.Core, rule.Cutoff }).ToArray()
        });

        SetPhase("initial-upload-and-program-construction");
        began = Stopwatch.GetTimestamp();
        using GlGpuSimulation simulation = new(configuration, converted);
        activeSimulation = simulation;
        double constructorCpuMilliseconds = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
        foreach (bool pending in Fence(simulation.Api)) { yield return pending; }
        Evidence.Add(new
        {
            Case = "initial-upload",
            constructorCpuMilliseconds,
            completionWallMilliseconds = Stopwatch.GetElapsedTime(began).TotalMilliseconds,
            particlePayloadBytes = (long)converted.Count * 32,
            simulation.RetainedDeviceBufferBytes,
            simulation.Execution,
            includes = "Native program compilation, workspace/rule allocation, float record packing, upload submission and initial fence completion; explicit CPU precision conversion was measured separately."
        });

        simulation.ConfigureMeasurements(enabled: true);
        if (profileFirstStep)
        {
            SetPhase("profiling-one-cold-step-without-warmup");
            simulation.SubmitStep();
            while (simulation.IsStepPending) { simulation.PollStep(); yield return false; }
            foreach (bool pending in CollectMeasurement(simulation)) { yield return pending; }
            LastCompletedMeasurement = simulation.LastStepMeasurement;
            MeasuredSteps = 1;
            Require(simulation.Boundary.Step == converted.Step + 1 && simulation.FullStateReadbacks == 0,
                "The diagnostic did not complete exactly one capture-free step.");
            Evidence.Add(new
            {
                Case = "one-cold-step-diagnostic",
                measurement = LastCompletedMeasurement,
                simulation.FullStateReadbacks,
                simulation.MetadataReadbackBytes,
                simulation.TimestampUnavailableReason,
                performanceClaim = false,
                warmedCapacityClaim = false
            });
            SetPhase("completed-cold-diagnostic");
            yield break;
        }
        SetPhase("warming-identical-input");
        long warmupBegan = Stopwatch.GetTimestamp();
        double warmupWorkSeconds = 0;
        do
        {
            long callBegan = Stopwatch.GetTimestamp();
            simulation.Replace(configuration, converted);
            foreach (bool pending in Fence(simulation.Api)) { yield return pending; }
            simulation.SubmitStep();
            while (simulation.IsStepPending) { simulation.PollStep(); yield return false; }
            foreach (bool pending in CollectMeasurement(simulation)) { yield return pending; }
            WarmupSteps++;
            LastCompletedMeasurement = simulation.LastStepMeasurement;
            warmupWorkSeconds += Stopwatch.GetElapsedTime(callBegan).TotalSeconds;
            if (WarmupSteps == 1 || WarmupSteps % 16 == 0) { progress(Phase); }
        }
        while (WarmupSteps < MinimumWarmupSteps || warmupWorkSeconds < MinimumWarmupSeconds);
        double warmupSeconds = Stopwatch.GetElapsedTime(warmupBegan).TotalSeconds;
        SetPhase("final-reset-before-measurement");
        began = Stopwatch.GetTimestamp();
        simulation.Replace(configuration, converted);
        foreach (bool pending in Fence(simulation.Api)) { yield return pending; }
        Evidence.Add(new
        {
            Case = "warmup-and-final-reset",
            WarmupSteps,
            minimumWarmupSteps = MinimumWarmupSteps,
            minimumWarmupSeconds = MinimumWarmupSeconds,
            warmupSeconds,
            warmupWorkSecondsExcludingProgress = warmupWorkSeconds,
            finalResetUploadMilliseconds = Stopwatch.GetElapsedTime(began).TotalMilliseconds,
            policy = "Identical-input warm steps use production Replace, including its fresh native buffers. One last completed upload precedes all seven measured continuing steps; no resets, captures or observations occur between samples."
        });
        object warmedMemory = Memory();
        GpuStepMeasurement[] steps = new GpuStepMeasurement[Samples];
        long[] allocations = new long[Samples];
        SetPhase("measuring-seven-continuing-steps");
        long startStep = simulation.Boundary.Step;
        double startTime = simulation.Boundary.Time;
        long samplingBegan = Stopwatch.GetTimestamp();
        for (int index = 0; index < Samples; index++)
        {
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            simulation.SubmitStep();
            while (simulation.IsStepPending) { simulation.PollStep(); yield return false; }
            allocations[index] = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            foreach (bool pending in CollectMeasurement(simulation)) { yield return pending; }
            steps[index] = simulation.LastStepMeasurement ?? throw new InvalidOperationException("Measured completion did not publish a record.");
            LastCompletedMeasurement = steps[index];
            MeasuredSteps++;
        }
        double samplingSeconds = Stopwatch.GetElapsedTime(samplingBegan).TotalSeconds;
        Require(simulation.Boundary.Step == startStep + Samples && simulation.Boundary.Count == converted.Count,
            "Measured steps changed membership or failed to advance exactly seven boundaries.");
        Require(simulation.FullStateReadbacks == 0, "Ordinary measured steps read particle state.");
        double simulationTime = simulation.Boundary.Time - startTime;
        Evidence.Add(new
        {
            Case = "continuing-simulation",
            raw = steps,
            processAllocatedBytesAcrossStepPolling = allocations,
            allocationScope = "Whole process across SubmitStep/PollStep and the unpaced host loop; includes any host/runtime allocations, not an isolated numerical allocation claim. Late query collection and report objects are outside these deltas.",
            startStep,
            startTime,
            endStep = simulation.Boundary.Step,
            endTime = simulation.Boundary.Time,
            completedSimulationTime = simulationTime,
            samplingSecondsIncludingQueryCollection = samplingSeconds,
            unpacedCompletedStepsPerWallSecond = Samples / samplingSeconds,
            unpacedSimulationTimePerWallSecond = simulationTime / samplingSeconds,
            gridGpuMilliseconds = Summary(steps.Select(step => step.GridGpuMilliseconds).ToArray()),
            integrationGpuMilliseconds = Summary(steps.Select(step => step.IntegrationGpuMilliseconds).ToArray()),
            cpuSubmissionMilliseconds = Summary(steps.Select(step => (double?)step.CpuSubmissionMilliseconds).ToArray()),
            cpuPollingMilliseconds = Summary(steps.Select(step => (double?)step.CpuPollingMilliseconds).ToArray()),
            wallCompletionMilliseconds = Summary(steps.Select(step => (double?)step.WallCompletionMilliseconds).ToArray()),
            simulation.FullStateReadbacks,
            simulation.MetadataReadbackBytes,
            simulation.TimestampUnavailableReason,
            baselineMemory,
            warmedMemory,
            afterStepsMemory = Memory()
        });
        simulation.ConfigureMeasurements(enabled: false);

        SetPhase("rendering-fixed-viewport-and-radii");
        foreach (bool pending in Render(simulation)) { yield return pending; }
        Require(simulation.FullStateReadbacks == 0, "Resident drawing performed a particle capture.");
        SetPhase("observing-128-by-128-display-bins");
        foreach (bool pending in Observe(simulation)) { yield return pending; }
        Require(simulation.FullStateReadbacks == 0, "Optional histogram work performed a particle capture.");

        SetPhase("explicit-terminal-capture");
        began = Stopwatch.GetTimestamp();
        simulation.BeginCapture();
        double captureSubmissionMilliseconds = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
        ParticleSnapshot? actual = null;
        double capturePollingMilliseconds = 0;
        while (actual is null)
        {
            long pollBegan = Stopwatch.GetTimestamp();
            actual = simulation.PollCapture();
            capturePollingMilliseconds += Stopwatch.GetElapsedTime(pollBegan).TotalMilliseconds;
            if (actual is null) { yield return false; }
        }
        double captureWallMilliseconds = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
        Require(actual.Count == converted.Count && actual.Step == startStep + Samples
            && actual.Ids.SequenceEqual(converted.Ids) && actual.Species.SequenceEqual(converted.Species),
            "Explicit terminal capture changed identity, species, count or the completed clock.");
        Require(simulation.FullStateReadbacks == 1 && CaptureTransfer.IsExactFloat32(actual), "Capture is not one actual float32 readback.");
        Evidence.Add(new
        {
            Case = "explicit-terminal-capture",
            captureSubmissionMilliseconds,
            capturePollingMilliseconds,
            captureWallMilliseconds,
            particleReadbackBytes = (long)actual.Count * 32,
            actual.Step,
            actual.Time,
            simulation.FullStateReadbacks,
            simulation.RetainedDeviceBufferBytes,
            memoryBeforeSerialization = Memory()
        });

        SetPhase("serializing-actual-capture");
        WriteCapture(configuration, converted, actual, simulation.Execution);
        Evidence.Add(new { Case = "final-memory", memory = Memory(), simulation.RetainedDeviceBufferBytes });
        SetPhase("completed");
    }

    private IEnumerable<bool> Render(GlGpuSimulation simulation)
    {
        using GlResidentRenderer renderer = new(device);
        using RenderTarget2D target = new(device, 1280, 720, false, SurfaceFormat.Color, DepthFormat.None);
        using GpuTimestampPool? timer = CreateTimer(simulation.Api, out string? timestampUnavailableReason);
        Rectangle clip = new(0, 0, target.Width, target.Height);
        WorldTransform transform = new(input.Parameters.WorldWidth, input.Parameters.WorldHeight, 0, 0, target.Width, target.Height);
        long tag = 0;
        foreach (int radius in new[] { 1, 4, 12 })
        {
            AppearanceSettings appearance = new() { ParticleRadius = radius };
            device.SetRenderTarget(target);
            device.Clear(Color.Black);
            try { renderer.Draw(simulation, transform, clip, appearance); }
            finally { device.SetRenderTarget(null); }
            foreach (bool pending in Fence(simulation.Api)) { yield return pending; }
            ParticleRenderMetrics[] raw = new ParticleRenderMetrics[Samples];
            double?[] gpu = new double?[Samples];
            double[] completionWall = new double[Samples], queryCollectionWall = new double[Samples];
            for (int index = 0; index < Samples; index++)
            {
                // The clear precedes the opening timestamp, so GPU intervals cover draw commands only.
                device.SetRenderTarget(target);
                device.Clear(Color.Black);
                long began = Stopwatch.GetTimestamp();
                int token = timer?.Begin(++tag, GpuCommandStage.Render) ?? -1;
                nint fence;
                try
                {
                    raw[index] = renderer.Draw(simulation, transform, clip, appearance);
                    timer?.End(token);
                    fence = simulation.Api.Fence();
                }
                finally { device.SetRenderTarget(null); }
                try { while (!simulation.Api.IsComplete(fence)) { yield return false; } }
                finally { simulation.Api.DeleteSync(fence); }
                completionWall[index] = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
                if (timer is not null && token >= 0)
                {
                    long queryBegan = Stopwatch.GetTimestamp();
                    GpuTimestampInterval interval;
                    while (!timer.TryTake(out interval))
                    {
                        if (Stopwatch.GetElapsedTime(queryBegan).TotalSeconds > 5) { throw new TimeoutException("Render timestamp delivery exceeded five seconds."); }
                        yield return false;
                    }
                    queryCollectionWall[index] = Stopwatch.GetElapsedTime(queryBegan).TotalMilliseconds;
                    if (interval.Valid && interval.Tag == tag) { gpu[index] = interval.Nanoseconds / 1_000_000.0; }
                }
                Require(raw[index].UploadedBytes == 0 && raw[index].InputCount == input.InitialState.Count
                    && raw[index].SubmittedParticles == input.InitialState.Count, "A retained draw uploaded particles or silently reduced submitted membership.");
            }
            Evidence.Add(new
            {
                Case = "fixed-resident-render",
                viewportWidth = target.Width,
                viewportHeight = target.Height,
                radiusPixels = radius,
                raw,
                gpuCommandMilliseconds = gpu,
                gpuSummary = Summary(gpu),
                completionWall,
                queryCollectionWall,
                timestampUnavailableReason,
                renderer.RetainedDeviceBufferBytes,
                targetColorPayloadBytes = (long)target.Width * target.Height * 4,
                inputCount = input.InitialState.Count,
                particleUploads = 0,
                simulation.FullStateReadbacks,
                policy = "Same completed resident buffer, fitted isotropic camera, normal blending, opaque species colours, no trails/glow/heatmap. All instances submitted; native viewport clipping is presentation-only. Draw timestamps exclude prior clear, later unbind, readback and present; wall completion includes host polling."
            });
        }
    }

    private IEnumerable<bool> Observe(GlGpuSimulation simulation)
    {
        SpatialObservationRequest request = new(default, input.Parameters.WorldWidth, input.Parameters.WorldHeight, 128, 128);
        object[] raw = new object[Samples];
        for (int index = -1; index < Samples; index++)
        {
            long before = simulation.SpatialObservationReadbackBytes;
            long began = Stopwatch.GetTimestamp();
            bool accepted = simulation.TryRequestSpatialObservation(request);
            double submissionMilliseconds = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            double pollingMilliseconds = 0;
            while (simulation.IsSpatialObservationPending)
            {
                long pollBegan = Stopwatch.GetTimestamp();
                simulation.PollSpatialObservation();
                pollingMilliseconds += Stopwatch.GetElapsedTime(pollBegan).TotalMilliseconds;
                yield return false;
            }
            double wallMilliseconds = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            SpatialObservation? observation = simulation.LatestSpatialObservation;
            if (!accepted || simulation.SpatialObservationError is not null)
            {
                Evidence.Add(new
                {
                    Case = "optional-spatial-observation-unavailable",
                    accepted,
                    simulation.SpatialObservationError,
                    submissionMilliseconds,
                    pollingMilliseconds,
                    wallMilliseconds
                });
                yield break;
            }
            Require(observation is not null && observation.Count == input.InitialState.Count && observation.Step == simulation.Boundary.Step,
                "The sampled observation lost membership or source clock.");
            Require(simulation.SpatialObservationReadbackBytes - before == 65_564, "The histogram readback is not the bounded expected payload.");
            object sample = new
            {
                submissionMilliseconds,
                pollingMilliseconds,
                wallMilliseconds,
                readbackBytes = simulation.SpatialObservationReadbackBytes - before,
                sourceStep = observation!.Step,
                sourceTime = observation.Time,
                observation.Count,
                observation.Outside,
                observation.MeanSpeed,
                observation.MaximumSpeed
            };
            if (index < 0)
            {
                Evidence.Add(new
                {
                    Case = "optional-spatial-observation-cold",
                    sample,
                    policy = "First optional observation includes lazy shader construction and observation-buffer allocation."
                });
            }
            else { raw[index] = sample; }
        }
        Evidence.Add(new
        {
            Case = "optional-spatial-observation-retained",
            raw,
            simulation.RetainedDeviceBufferBytes,
            simulation.FullStateReadbacks,
            cadencePolicy = "Seven explicitly requested standalone samples reuse initialized observation resources and the same final state. Excluded from simulation/render timings; application cadence is a separate presentation choice."
        });
    }

    private void WriteCapture(SimulationConfiguration configuration, ParticleSnapshot converted, ParticleSnapshot actual, ExecutionProvenance execution)
    {
        long began = Stopwatch.GetTimestamp();
        ExperimentDefinition initial = new(input.Name + " / explicit GPU float32 start", input.Parameters, input.Matrix, converted,
            input.TimeStep, new("benchmark/p7/explicit-float32-conversion", "1", input.Generator.Seed, Environment.Version.ToString()),
            input.Backend, input.WorkerCount,
            new(0, 0, "Explicit float32 conversion of immutable common input SHA-256 " + inputHash, input.Generator, input.Name,
                ExecutionProvenance.Cpu(configuration)));
        CapturedSessionDefinition captured = new(new(initial, [], actual.Step),
            [new(configuration, actual, execution, 0, "terminal capacity sample")], []);
        string json = ExecutionCaptureFile.Serialize(captured);
        double serializationMilliseconds = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
        string path = Path.Combine(outputDirectory, "terminal.capture.json");
        began = Stopwatch.GetTimestamp();
        using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (StreamWriter writer = new(stream)) { writer.Write(json); }
        double writeMilliseconds = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
        began = Stopwatch.GetTimestamp();
        using FileStream saved = File.OpenRead(path);
        string sha256 = Convert.ToHexString(SHA256.HashData(saved));
        Evidence.Add(new
        {
            Case = "canonical-actual-capture-file",
            file = path,
            sha256,
            bytes = saved.Length,
            serializationMilliseconds,
            writeMilliseconds,
            hashMilliseconds = Stopwatch.GetElapsedTime(began).TotalMilliseconds,
            initialCpuInputSha256 = inputHash,
            policy = "Canonical capture embeds the actual converted GPU initial values and actual terminal values/provenance. The unchanged authoritative CPU input remains in the separately hashed common-input file. Serialization/storage are outside every simulation, render, observation and device-capture timing."
        });
    }

    private static IEnumerable<bool> Fence(GpuComputeApi api)
    {
        nint fence = api.Fence();
        try { while (!api.IsComplete(fence)) { yield return false; } }
        finally { api.DeleteSync(fence); }
    }

    private static IEnumerable<bool> CollectMeasurement(GlGpuSimulation simulation)
    {
        long began = Stopwatch.GetTimestamp();
        while (simulation.IsMeasurementPending)
        {
            simulation.PollMeasurements();
            if (Stopwatch.GetElapsedTime(began).TotalSeconds > 5) { throw new TimeoutException("Step timestamp delivery exceeded five seconds."); }
            yield return false;
        }
    }

    private static GpuTimestampPool? CreateTimer(GpuComputeApi api, out string? unavailableReason)
    {
        try { unavailableReason = null; return new(api.Gl); }
        catch (Exception failure) when (failure is NotSupportedException or InvalidOperationException)
        { unavailableReason = failure.Message; return null; }
    }

    private static object DescribeConversion(ParticleSnapshot source, ParticleSnapshot converted, double milliseconds)
    {
        long changedComponents = 0;
        double maximumPositionError = 0, maximumVelocityError = 0;
        for (int index = 0; index < source.Count; index++)
        {
            Compare(source.Positions[index].X, converted.Positions[index].X, ref maximumPositionError);
            Compare(source.Positions[index].Y, converted.Positions[index].Y, ref maximumPositionError);
            Compare(source.Velocities[index].X, converted.Velocities[index].X, ref maximumVelocityError);
            Compare(source.Velocities[index].Y, converted.Velocities[index].Y, ref maximumVelocityError);
        }
        return new
        {
            Case = "explicit-precision-conversion",
            from = "binary64",
            to = "binary32",
            changedComponents,
            maximumPositionError,
            maximumVelocityError,
            milliseconds,
            identitiesAndSpeciesRetained = source.Ids.SequenceEqual(converted.Ids) && source.Species.SequenceEqual(converted.Species),
            converted.Count,
            converted.Step,
            converted.Time,
            hostClockPrecision = "binary64"
        };
        void Compare(double before, double after, ref double maximum)
        {
            if (BitConverter.DoubleToInt64Bits(before) != BitConverter.DoubleToInt64Bits(after)) { changedComponents++; }
            maximum = Math.Max(maximum, Math.Abs(before - after));
        }
    }

    private static object Summary(double?[] values)
    {
        double[] valid = values.Where(value => value.HasValue).Select(value => value!.Value).Order().ToArray();
        return new
        {
            samples = values.Length,
            availableSamples = valid.Length,
            complete = valid.Length == values.Length,
            p50Milliseconds = valid.Length == values.Length ? (double?)valid[(int)Math.Ceiling(valid.Length * 0.5) - 1] : null,
            p95Milliseconds = valid.Length == values.Length ? (double?)valid[^1] : null
        };
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
            processPeakWorkingSetBytes = process.PeakWorkingSet64
        };
    }
    private void SetPhase(string phase) { Phase = phase; progress(phase); }
    private static void Require(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }
}
