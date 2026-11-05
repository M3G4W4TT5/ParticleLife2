using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using ParticleLife.MonoGame.Rendering;
using ParticleLife.Viewer;

namespace ParticleLife.MonoGame.Gpu;

/// <summary>Provides bounded, explicitly invoked spatial/visual/measurement checks on the current graphics owner.</summary>
/// <remarks>The caller advances this iterator across update calls and supplies its watchdog. Every
/// device wait is a zero-timeout fence/query poll. Fixture uploads are explicit; ordinary sampling,
/// stepping and rendering assert zero particle-state readbacks. Framebuffer readback is test-only.
/// Small timing records establish instrumentation coverage, not capacity or performance conclusions.</remarks>
internal static class GpuPresentationChecks
{
    internal static IEnumerable<bool> Run(GraphicsDevice device, string outputDirectory, Action<object> record)
    {
        ModelParameters parameters = new()
        {
            Boundary = BoundaryKind.Open,
            Cutoff = 0.25,
            ForceScale = 0,
            WallStrength = 0,
            DampingHalfLife = null
        };
        SimulationConfiguration configuration = new(parameters, new(1, [0]), 1.0 / 1024);
        ParticleSnapshot initial = ObservationFixture();
        using GlGpuSimulation simulation = new(configuration, initial);
        record(new { Case = "presentation-context", simulation.Execution, InitialStep = initial.Step, InitialTime = initial.Time });
        foreach (bool pending in CheckObservations(simulation, configuration, initial, record)) { yield return pending; }
        foreach (bool pending in CheckMeasurements(simulation, configuration, initial, record)) { yield return pending; }
        using GlResidentRenderer renderer = new(device);
        foreach (bool pending in CheckPixels(device, renderer, simulation, configuration, outputDirectory, record)) { yield return pending; }
        Require(simulation.FullStateReadbacks == 0, "Presentation checks performed a particle-state capture.");
        record(new
        {
            Case = "presentation-zero-state-readbacks",
            simulation.FullStateReadbacks,
            simulation.MetadataReadbackBytes,
            simulation.SpatialObservationReadbackBytes,
            simulation.RetainedDeviceBufferBytes
        });
    }

    private static IEnumerable<bool> CheckObservations(GlGpuSimulation simulation, SimulationConfiguration configuration,
        ParticleSnapshot initial, Action<object> record)
    {
        Console.WriteLine("GPU_PRESENTATION_BEGIN observation-boundary-ownership");
        SpatialSampler reference = new();
        SpatialObservationRequest request = new(default, 1, 1, 128, 128);
        SpatialObservation expected = reference.Sample(initial.Positions, initial.Velocities, initial.Step, initial.Time, request);
        long before = simulation.SpatialObservationReadbackBytes;
        simulation.SubmitStep();
        Require(simulation.TryRequestSpatialObservation(request), simulation.SpatialObservationError ?? "Observation was not accepted.");
        Require(!simulation.TryRequestSpatialObservation(new(default, 2, 2, 4, 4)), "A pending observation was overwritten.");
        simulation.RequestSummary();
        while (simulation.IsStepPending) { simulation.PollStep(); yield return false; }
        Require(simulation.Boundary.Step == initial.Step + 1, "The concurrent step did not finish.");
        while (simulation.IsSpatialObservationPending) { simulation.PollSpatialObservation(); yield return false; }
        SpatialObservation observed = RequireObservation(simulation);
        Compare(expected, observed);
        Require(ReferenceEquals(observed.Request, request), "The observation lost ownership of its requested geometry.");
        Require(simulation.SpatialObservationReadbackBytes - before == 65_564, "The maximum histogram readback payload changed.");
        while (simulation.LatestSummary is null) { simulation.PollSummary(); yield return false; }
        Require(simulation.LatestSummary.Count == initial.Count && simulation.LatestSummary.Step == initial.Step,
            "The ordinary speed summary was contaminated by observation or step scratch.");
        record(new
        {
            Case = "observation-boundary-ownership",
            observed.Count,
            observed.Outside,
            observed.Step,
            observed.Time,
            observed.MeanSpeed,
            observed.MaximumSpeed,
            ReadbackBytes = simulation.SpatialObservationReadbackBytes - before,
            CompletedSimulationStep = simulation.Boundary.Step,
            ExactBins = true
        });

        Require(!simulation.TryRequestSpatialObservation(new(default, double.Epsilon, 1, 1, 1))
            && simulation.SpatialObservationError is not null, "An unsupported observation range was silently accepted.");
        long stepBeforeFailure = simulation.Boundary.Step;
        simulation.SubmitStep();
        while (simulation.IsStepPending) { simulation.PollStep(); yield return false; }
        Require(simulation.Boundary.Step == stepBeforeFailure + 1, "Optional observation rejection stopped numerical work.");
        Require(ReferenceEquals(observed, simulation.LatestSpatialObservation), "Rejected work erased the last owned observation.");

        Require(simulation.TryRequestSpatialObservation(request), "Cancellation fixture did not start.");
        before = simulation.SpatialObservationReadbackBytes;
        simulation.CancelSpatialObservation();
        simulation.PollSpatialObservation();
        Require(!simulation.IsSpatialObservationPending && ReferenceEquals(observed, simulation.LatestSpatialObservation)
            && before == simulation.SpatialObservationReadbackBytes, "Cancellation read or erased a completed sample.");
        Require(simulation.TryRequestSpatialObservation(request), "Replacement fixture did not start.");
        ParticleSnapshot replacement = GpuCorrectnessCase.State([new(0, 0), new(1, 1), new(-0.125, 0.5)],
            [new(0.125, 0), new(0, -0.25), default], 1, time: 12, step: 19);
        simulation.Replace(configuration, replacement);
        Require(!simulation.IsSpatialObservationPending && simulation.LatestSpatialObservation is null,
            "Replacement retained an observation from the previous state.");
        SpatialObservationRequest smaller = new(default, 1, 1, 2, 2);
        Require(simulation.TryRequestSpatialObservation(smaller), "Replacement observation was rejected.");
        while (simulation.IsSpatialObservationPending) { simulation.PollSpatialObservation(); yield return false; }
        Compare(reference.Sample(replacement.Positions, replacement.Velocities, replacement.Step, replacement.Time, smaller),
            RequireObservation(simulation));
        Require(simulation.SpatialObservationReadbackBytes - before == 44, "Abandoned work was read or a smaller request read retained capacity.");
        record(new
        {
            Case = "observation-cancel-replace-reject",
            OptionalFailureRetainedSimulation = true,
            CancelReadbackBytes = 0,
            ReplacementStep = replacement.Step,
            ReplacementReadbackBytes = 44
        });

        ParticleSnapshot extreme = GpuCorrectnessCase.State([new(0.25, 0.25), new(0.75, 0.75)],
            [new(float.MaxValue, float.MaxValue), new(Math.ScaleB(1, -120), -Math.ScaleB(1, -120))], 1);
        simulation.Replace(configuration, extreme);
        Require(simulation.TryRequestSpatialObservation(smaller), "Large finite speed observation was rejected.");
        while (simulation.IsSpatialObservationPending) { simulation.PollSpatialObservation(); yield return false; }
        Compare(reference.Sample(extreme.Positions, extreme.Velocities, extreme.Step, extreme.Time, smaller), RequireObservation(simulation));
        record(new { Case = "observation-robust-speed", RequireObservation(simulation).MeanSpeed, RequireObservation(simulation).MaximumSpeed });

        ParticleSnapshot empty = new([], [], [], []);
        simulation.Replace(configuration, empty);
        Require(simulation.TryRequestSpatialObservation(smaller), "Empty observation was rejected.");
        while (simulation.IsSpatialObservationPending) { simulation.PollSpatialObservation(); yield return false; }
        Compare(reference.Sample(empty.Positions, empty.Velocities, 0, 0, smaller), RequireObservation(simulation));
        record(new { Case = "observation-empty-membership", Count = 0, MeanSpeed = 0, MaximumSpeed = 0 });
        Console.WriteLine("GPU_PRESENTATION_PASS observations");
    }

    private static IEnumerable<bool> CheckMeasurements(GlGpuSimulation simulation, SimulationConfiguration configuration,
        ParticleSnapshot initial, Action<object> record)
    {
        Console.WriteLine("GPU_PRESENTATION_BEGIN opt-in-step-measurement");
        simulation.Replace(configuration, initial);
        Require(simulation.LastStepMeasurement is null, "Default simulation unexpectedly collected timing records.");
        simulation.ConfigureMeasurements(enabled: true);
        for (int index = 0; index < 2; index++)
        {
            simulation.SubmitStep();
            while (simulation.IsStepPending) { simulation.PollStep(); yield return false; }
            GpuStepMeasurement? atNumericalCompletion = simulation.LastStepMeasurement;
            long queryWait = Stopwatch.GetTimestamp();
            while (simulation.IsMeasurementPending)
            {
                simulation.PollMeasurements();
                if (Stopwatch.GetElapsedTime(queryWait).TotalSeconds > 5)
                {
                    record(new { Case = "timestamp-collection-timeout", Measurement = simulation.LastStepMeasurement });
                    throw new TimeoutException("Completed-step timestamps did not become available within five seconds.");
                }
                yield return false;
            }
            GpuStepMeasurement measured = simulation.LastStepMeasurement ?? throw new InvalidOperationException("No completed measurement was published.");
            Require(measured.Published && measured.Step == simulation.Boundary.Step && measured.Count == initial.Count,
                "Measurement has the wrong completed boundary.");
            Require(measured.GridDispatches >= 5 && measured.IntegrationDispatches > 0 && measured.CommandGroups == measured.IntegrationDispatches + 1,
                "Measurement did not account for every command group.");
            Require(measured.CpuSubmissionMilliseconds >= 0 && measured.CpuPollingMilliseconds >= 0 && measured.WallCompletionMilliseconds >= 0,
                "CPU/wall measurement contains an invalid duration.");
            Require(measured.RetainedDeviceBufferBytes == simulation.RetainedDeviceBufferBytes
                && measured.RetainedDeviceBufferBytes >= (long)initial.Count * 32 * 3, "Retained buffer accounting is incomplete.");
            record(new { Case = "opt-in-step-measurement", AtNumericalCompletion = atNumericalCompletion,
                Measurement = measured, simulation.TimestampUnavailableReason });
            Require(!simulation.HasGpuTimestamps || measured.CompleteGpuTiming,
                $"Collected step timestamps cover {measured.TimedCommandGroups}/{measured.CommandGroups} groups "
                + $"({measured.TimestampGroupsIssued} issued): {measured.IncompleteGpuTimingReason}");
            Require(!measured.CompleteGpuTiming || measured.GridGpuMilliseconds >= 0 && measured.IntegrationGpuMilliseconds >= 0,
                "Timestamp coverage was claimed without valid GPU intervals.");
        }
        simulation.ConfigureMeasurements(enabled: false);
        Require(simulation.LastStepMeasurement is null && !simulation.HasGpuTimestamps, "Disabling measurement retained active query resources.");
        Console.WriteLine("GPU_PRESENTATION_PASS step-measurement");
    }

    private static IEnumerable<bool> CheckPixels(GraphicsDevice device, GlResidentRenderer renderer, GlGpuSimulation simulation,
        SimulationConfiguration configuration, string outputDirectory, Action<object> record)
    {
        Console.WriteLine("GPU_PRESENTATION_BEGIN velocity-opacity-blend");
        Vector2d[] positions = [new(0.125, 0.5), new(0.25, 0.5), new(0.375, 0.5), new(0.5, 0.5), new(0.625, 0.5)];
        Vector2d[] velocities = [default, new(0.0625, 0), new(0.125, 0), new(0, 0.125), new(-0.125, 0)];
        ParticleSnapshot state = GpuCorrectnessCase.State(positions, velocities, 1);
        simulation.Replace(configuration, state);
        using RenderTarget2D target = new(device, 128, 128, false, SurfaceFormat.Color, DepthFormat.None);
        WorldTransform transform = new(1, 1, 0, 0, target.Width, target.Height);
        AppearanceSettings appearance = new([0x4080C0]) { ParticleRadius = 5 };
        Color background = new(20, 30, 40);
        Color[] pixels = new Color[target.Width * target.Height];
        using GpuTimestampPool? timer = CreateTimer(simulation.Api, out string? timestampUnavailableReason);
        bool first = true;
        int verified = 0;
        long tag = 0;
        foreach (VelocityShading mode in Enum.GetValues<VelocityShading>())
        {
            foreach (ParticleBlend blend in Enum.GetValues<ParticleBlend>())
            {
                foreach (double opacity in new[] { 1.0, 0.5, 0.0 })
                {
                    VisualSettings visuals = new() { Velocity = mode, Blend = blend, Opacity = opacity, SpeedScale = 0.125 };
                    device.SetRenderTarget(target);
                    device.Clear(background);
                    ParticleRenderMetrics metrics;
                    nint fence;
                    int token = timer?.Begin(++tag, GpuCommandStage.Render) ?? -1;
                    try
                    {
                        metrics = renderer.Draw(simulation, transform, new(0, 0, target.Width, target.Height), appearance with { Visuals = visuals });
                        timer?.End(token);
                        fence = simulation.Api.Fence();
                    }
                    finally { device.SetRenderTarget(null); }
                    try { while (!simulation.Api.IsComplete(fence)) { yield return false; } }
                    finally { simulation.Api.DeleteSync(fence); }
                    double? gpuMilliseconds = null;
                    if (timer is not null)
                    {
                        long queryWait = Stopwatch.GetTimestamp();
                        GpuTimestampInterval interval;
                        while (!timer.TryTake(out interval))
                        {
                            if (Stopwatch.GetElapsedTime(queryWait).TotalSeconds > 5)
                            { throw new TimeoutException("Completed-draw timestamps did not become available within five seconds."); }
                            yield return false;
                        }
                        Require(interval.Valid && interval.Tag == tag, "The completed resident draw has no valid timestamp interval.");
                        gpuMilliseconds = interval.Nanoseconds / 1_000_000.0;
                    }
                    target.GetData(pixels);
                    for (int index = 0; index < positions.Length; index++)
                    {
                        Vector2d centre = transform.ToScreen(positions[index]);
                        Color actual = pixels[(int)centre.Y * target.Width + (int)centre.X];
                        (float r, float g, float b) = ParticleColour.Evaluate(0x4080C0, velocities[index], visuals);
                        double destination = blend == ParticleBlend.Additive ? 1 : 1 - opacity;
                        MatchChannel(actual.R, 255 * r * opacity + background.R * destination, mode, blend, opacity, index);
                        MatchChannel(actual.G, 255 * g * opacity + background.G * destination, mode, blend, opacity, index);
                        MatchChannel(actual.B, 255 * b * opacity + background.B * destination, mode, blend, opacity, index);
                        verified++;
                    }
                    Require(metrics.UploadedBytes == (first ? sizeof(uint) : 0), "A visual-only edit uploaded resident particles or an unchanged palette.");
                    first = false;
                    if (blend == ParticleBlend.Normal && opacity == 1)
                    {
                        using FileStream file = File.Create(Path.Combine(outputDirectory, "resident-" + mode + ".png"));
                        target.SaveAsPng(file, target.Width, target.Height);
                    }
                    record(new
                    {
                        Case = "resident-visual-pixels",
                        Mode = mode.ToString(),
                        Blend = blend.ToString(),
                        Opacity = opacity,
                        VerifiedCentres = positions.Length,
                        Cpu = metrics,
                        GpuCommandMilliseconds = gpuMilliseconds,
                        TimestampUnavailableReason = timestampUnavailableReason
                    });
                }
            }
        }
        Require(simulation.Boundary.Step == state.Step && simulation.FullStateReadbacks == 0,
            "Appearance-only checks changed simulation or captured particle state.");
        record(new { Case = "resident-visual-pixel-total", VerifiedCentres = verified, ParticleUploadBytes = 0, simulation.FullStateReadbacks });
        Console.WriteLine("GPU_PRESENTATION_PASS velocity-opacity-blend");
    }

    private static ParticleSnapshot ObservationFixture()
    {
        Vector2d[] boundaryPoints = [new(0, 0), new(1, 1), new(0.5, 0.5), new(0.25, 0.75),
            new(-0.125, 0.5), new(0.5, 1.125), new(1, 0), new(0, 1)];
        Vector2d[] positions = new Vector2d[300], velocities = new Vector2d[300];
        for (int index = 0; index < positions.Length; index++)
        {
            positions[index] = boundaryPoints[index % boundaryPoints.Length];
            velocities[index] = new((index % 7 - 3) / 64.0, (index % 5 - 2) / 64.0);
        }
        return GpuCorrectnessCase.State(positions, velocities, 1, time: 3, step: 7);
    }

    private static SpatialObservation RequireObservation(GlGpuSimulation simulation) => simulation.LatestSpatialObservation
        ?? throw new InvalidOperationException(simulation.SpatialObservationError ?? "No completed observation was published.");

    private static GpuTimestampPool? CreateTimer(GpuComputeApi api, out string? unavailableReason)
    {
        try { unavailableReason = null; return new(api.Gl); }
        catch (Exception failure) when (failure is NotSupportedException or InvalidOperationException)
        { unavailableReason = failure.Message; return null; }
    }

    private static void Compare(SpatialObservation expected, SpatialObservation actual)
    {
        Require(expected.Request == actual.Request && expected.Step == actual.Step && expected.Time == actual.Time,
            "Observation geometry or source clock differs from the CPU reference.");
        Require(expected.Count == actual.Count && expected.Outside == actual.Outside && expected.Counts.SequenceEqual(actual.Counts),
            "The spatial histogram differs from the CPU sampler, including inclusive right/bottom edges.");
        Require(Math.Abs(expected.MeanSpeed - actual.MeanSpeed) <= 1e-12 * expected.MaximumSpeed + 1e-300
            && Math.Abs(expected.MaximumSpeed - actual.MaximumSpeed) <= 1e-12 * expected.MaximumSpeed + 1e-300,
            "The binary64 observation speed reduction differs from the CPU sampler.");
    }

    private static void MatchChannel(byte actual, double expected, VelocityShading mode, ParticleBlend blend, double opacity, int particle)
    {
        Require(Math.Abs(actual - Math.Clamp(expected, 0, 255)) <= 2,
            $"Resident {mode}/{blend}/opacity {opacity} particle {particle} colour differs: actual {actual}, expected {expected:G9}.");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
