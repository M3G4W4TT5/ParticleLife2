namespace ParticleLife.MonoGame.Gpu;

internal sealed unsafe partial class GlGpuSimulation
{
    private GpuStepMeasurements? measurements;

    /// <summary>Gets retained buffer payload bytes; excludes driver/program/query-object storage.</summary>
    internal long RetainedDeviceBufferBytes => workspace.RetainedBytes + (long)model.Rules.Length * sizeof(GpuPairRule)
        + (spatialSampler?.RetainedBytes ?? 0);
    /// <summary>Gets the most recent explicitly measured completed/cancelled/rejected candidate.</summary>
    internal GpuStepMeasurement? LastStepMeasurement => measurements?.Latest;
    internal bool HasGpuTimestamps => measurements?.HasGpuTimestamps == true;
    internal string? TimestampUnavailableReason => measurements?.TimestampUnavailableReason;
    /// <summary>Gets whether a completed step still awaits asynchronous timestamp results, independent of its numerical fence.</summary>
    internal bool IsMeasurementPending => measurements?.IsPending == true;
    /// <summary>Copies diagnostic submission progress only when explicitly requested outside numerical hot loops.</summary>
    internal object DescribeSubmissionProgress() => new
    {
        completedStep = Boundary.Step,
        completedTime = Boundary.Time,
        count = Boundary.Count,
        stage = stage.ToString(),
        submittedReceivers = nextReceiver,
        receiversPerGroup = stage == StepStage.Grid ? 0 : receiverChunk,
        IsStepPending,
        IsMeasurementPending
    };

    /// <summary>Collects available timestamp results without waiting; never changes the completed simulation boundary.</summary>
    internal void PollMeasurements()
    {
        EnsureOwner();
        measurements?.Poll();
    }

    /// <summary>Enables or releases measurement resources only at an idle boundary.</summary>
    /// <remarks>Default simulation constructs no query objects, timer buffers or per-step measurement
    /// records. An enabled collector reuses a fixed eight-pair timestamp pool regardless of density;
    /// incomplete timing coverage is reported rather than blocking or truncating numerical work.</remarks>
    internal void ConfigureMeasurements(bool enabled, bool gpuTimestamps = true)
    {
        EnsureIdle();
        GpuStepMeasurements? candidate = enabled ? new(api.Gl, gpuTimestamps) : null;
        measurements?.Dispose();
        measurements = candidate;
    }
}
