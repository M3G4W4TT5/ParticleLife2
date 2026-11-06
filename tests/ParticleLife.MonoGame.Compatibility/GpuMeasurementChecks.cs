using ParticleLife.MonoGame.Gpu;

/// <summary>Checks late-query ownership without a device, native library, fence wait or timing tolerance.</summary>
internal static class GpuMeasurementChecks
{
    internal static void Run()
    {
        FakeQueries source = new();
        using GpuStepMeasurements measurements = new(source);
        CompleteStep(measurements, 7);
        GpuStepMeasurement pending = measurements.Latest!.Value;
        Require(measurements.IsPending && pending.TimestampResultsPending && !pending.CompleteGpuTiming
            && pending.GridGpuMilliseconds is null, "Numerical completion fabricated a pending GPU duration.");
        measurements.Poll();
        Require(measurements.IsPending, "Unavailable timestamp polling incorrectly completed a record.");
        source.Ready = true;
        measurements.Poll();
        GpuStepMeasurement complete = measurements.Latest!.Value;
        Require(!measurements.IsPending && complete.CompleteGpuTiming && complete.Step == 7
            && complete.GridGpuMilliseconds == 0.001 && complete.TimedCommandGroups == 1,
            "A delayed query did not finish its original completed measurement.");
        Require(complete.WallCompletionMilliseconds == pending.WallCompletionMilliseconds
            && complete.CpuSubmissionMilliseconds == pending.CpuSubmissionMilliseconds
            && complete.CpuPollingMilliseconds == pending.CpuPollingMilliseconds,
            "Later query delivery changed the frozen numerical step costs.");

        source.Ready = false;
        CompleteStep(measurements, 8);
        CompleteStep(measurements, 9);
        source.Ready = true;
        measurements.Poll();
        Require(measurements.Latest is { Step: 9, TimedCommandGroups: 1, CompleteGpuTiming: true },
            "An old delayed query contaminated the following measurement's tag.");

        using GpuStepMeasurements exhausted = new(new FakeQueries { Exhausted = true });
        CompleteStep(exhausted, 1);
        Require(!exhausted.IsPending && exhausted.Latest is { CompleteGpuTiming: false, TimestampGroupsIssued: 0 },
            "Skipped timestamp coverage fabricated a duration or an endless pending result.");
        using GpuStepMeasurements invalid = new(new FakeQueries { Ready = true, Valid = false });
        CompleteStep(invalid, 1);
        Require(!invalid.IsPending && invalid.Latest is { CompleteGpuTiming: false, GridGpuMilliseconds: null },
            "Invalid/wrapped timestamp values were presented as valid GPU time.");
        Console.WriteLine("GPU measurement managed checks: delayed delivery, frozen step costs, tag isolation, exhausted pool and invalid values passed.");
    }

    private static void CompleteStep(GpuStepMeasurements measurements, long step)
    {
        measurements.Begin(step, 300);
        using GpuMeasurementInterval interval = measurements.Interval(polling: false);
        int token = measurements.BeginGroup(GpuCommandStage.Grid);
        measurements.Dispatch();
        measurements.EndGroup(token);
        measurements.Complete(wasPublished: true, 12345);
    }

    private sealed class FakeQueries : IGpuTimestampSource
    {
        private readonly Queue<GpuTimestampInterval> pending = [];
        private GpuTimestampInterval submitted;
        internal bool Ready, Exhausted;
        internal bool Valid = true;
        public int Begin(long tag, GpuCommandStage stage)
        {
            submitted = new(tag, stage, 1000, Valid);
            return Exhausted ? -1 : 0;
        }
        public void End(int token) { if (token >= 0) { pending.Enqueue(submitted); } }
        public bool TryTake(out GpuTimestampInterval interval)
        {
            if (Ready && pending.TryDequeue(out interval)) { return true; }
            interval = default;
            return false;
        }
        public void Dispose() { }
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
