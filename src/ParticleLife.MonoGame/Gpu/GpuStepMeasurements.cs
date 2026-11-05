using System.Diagnostics;
using ParticleLife.MonoGame.Rendering;

namespace ParticleLife.MonoGame.Gpu;

/// <summary>Describes one opt-in fixed-step measurement without folding caller scheduling gaps into GPU command time.</summary>
/// <remarks>Retained bytes count requested device-buffer payload, excluding programs, query objects and
/// driver allocations. Wall completion includes caller/polling gaps. CPU times include native submission,
/// fence/status/query polling and state restoration. GPU intervals are supplied only for complete query
/// coverage, exclude inter-group gaps and rendering, and are not shader-only or present timings.
/// Query-result delivery can lag numerical completion. Its later CPU/wall collection costs are
/// separate from the frozen step costs; pending or missing results never become invented durations.</remarks>
internal readonly record struct GpuStepMeasurement(long Step, int Count, bool Published, long RetainedDeviceBufferBytes,
    int GridDispatches, int IntegrationDispatches, int CommandGroups, int TimedCommandGroups,
    double CpuSubmissionMilliseconds, double CpuPollingMilliseconds, double WallCompletionMilliseconds,
    double? GridGpuMilliseconds, double? IntegrationGpuMilliseconds, bool CompleteGpuTiming,
    int TimestampGroupsIssued, bool TimestampResultsPending, string? IncompleteGpuTimingReason,
    double TimestampCollectionCpuMilliseconds, double TimestampCollectionWallMilliseconds,
    uint MaximumBucketOccupancy, int ReceiversPerIntegrationGroup, int PollCalls,
    double MaximumCpuOwnerCallMilliseconds, double? MaximumIntegrationGroupGpuMilliseconds);

/// <summary>Collects reusable counters/timestamp pairs only after explicit measurement activation.</summary>
internal sealed class GpuStepMeasurements : IDisposable
{
    private readonly IGpuTimestampSource? timer;
    private long tag, started, step, retainedBytes, completedAt;
    private int count, gridDispatches, integrationDispatches, groups, timedGroups, issuedGroups;
    private ulong gridNanoseconds, integrationNanoseconds;
    private ulong maximumIntegrationGroupNanoseconds;
    private uint maximumBucketOccupancy;
    private int receiverChunk, pollCalls;
    private double maximumOwnerCallMilliseconds;
    private double submissionMilliseconds, pollingMilliseconds, wallMilliseconds, collectionCpuMilliseconds, collectionWallMilliseconds;
    private bool active, finishing, published, validTiming, inGroup, completed;
    private GpuCommandStage currentStage;
    internal GpuStepMeasurement? Latest { get; private set; }
    internal bool HasGpuTimestamps => timer is not null;
    internal string? TimestampUnavailableReason { get; }
    internal bool IsPending => completed && !active && timedGroups < issuedGroups;

    internal GpuStepMeasurements(OpenGlDrawApi gl, bool gpuTimestamps)
    {
        if (!gpuTimestamps) { TimestampUnavailableReason = "GPU timestamps were not requested."; return; }
        try { timer = new GpuTimestampPool(gl); }
        catch (Exception failure) when (failure is NotSupportedException or InvalidOperationException)
        { TimestampUnavailableReason = failure.Message; }
    }

    /// <summary>Owns an explicit timestamp source for deterministic asynchronous-lifetime checks.</summary>
    internal GpuStepMeasurements(IGpuTimestampSource source) => timer = source;

    internal void Begin(long targetStep, int particles)
    {
        Poll();
        if (IsPending && Latest is { } previous)
        {
            Latest = previous with { TimestampResultsPending = false, IncompleteGpuTimingReason = "A later measured step superseded pending query results." };
        }
        Collect();
        tag++;
        started = Stopwatch.GetTimestamp();
        step = targetStep;
        count = particles;
        gridDispatches = integrationDispatches = groups = timedGroups = issuedGroups = 0;
        gridNanoseconds = integrationNanoseconds = 0;
        maximumIntegrationGroupNanoseconds = 0;
        maximumBucketOccupancy = 0;
        receiverChunk = pollCalls = 0;
        maximumOwnerCallMilliseconds = 0;
        submissionMilliseconds = pollingMilliseconds = wallMilliseconds = 0;
        collectionCpuMilliseconds = collectionWallMilliseconds = 0;
        active = true;
        finishing = inGroup = completed = false;
        validTiming = true;
    }

    internal GpuMeasurementInterval Interval(bool polling) => active ? new(this, polling) : default;

    /// <summary>Records the conservative occupancy bound and chosen submission size without changing neighbour traversal.</summary>
    internal void RecordGrid(uint occupancy, int receivers)
    {
        maximumBucketOccupancy = occupancy;
        receiverChunk = receivers;
    }

    internal int BeginGroup(GpuCommandStage stage)
    {
        Collect();
        currentStage = stage;
        inGroup = true;
        groups++;
        int token = timer?.Begin(tag, stage) ?? -1;
        if (token >= 0) { issuedGroups++; }
        return token;
    }
    internal void EndGroup(int token) { timer?.End(token); inGroup = false; }
    internal void Dispatch()
    {
        if (!active || !inGroup) { return; }
        if (currentStage == GpuCommandStage.Grid) { gridDispatches++; }
        else if (currentStage == GpuCommandStage.Integration) { integrationDispatches++; }
    }

    internal void Complete(bool wasPublished, long bytes)
    {
        Collect();
        published = wasPublished;
        retainedBytes = bytes;
        finishing = true;
    }

    internal void EndInterval(long intervalStart, bool polling)
    {
        double elapsed = Stopwatch.GetElapsedTime(intervalStart).TotalMilliseconds;
        if (polling) { pollingMilliseconds += elapsed; pollCalls++; }
        else { submissionMilliseconds += elapsed; }
        maximumOwnerCallMilliseconds = Math.Max(maximumOwnerCallMilliseconds, elapsed);
        if (!finishing) { return; }
        completedAt = Stopwatch.GetTimestamp();
        wallMilliseconds = Stopwatch.GetElapsedTime(started, completedAt).TotalMilliseconds;
        active = finishing = false;
        completed = true;
        Publish();
    }

    /// <summary>Collects only available results after completion without advancing or delaying simulation.</summary>
    /// <remarks>A numerical fence is not used as a substitute for QUERY_RESULT_AVAILABLE. The caller
    /// may poll at later updates, impose its own deadline, or start another step and retain an honestly
    /// incomplete prior record. Query slots own no workspace resources.</remarks>
    internal void Poll()
    {
        if (!IsPending) { return; }
        long collectionStarted = Stopwatch.GetTimestamp();
        Collect();
        collectionCpuMilliseconds += Stopwatch.GetElapsedTime(collectionStarted).TotalMilliseconds;
        collectionWallMilliseconds = Stopwatch.GetElapsedTime(completedAt).TotalMilliseconds;
        Publish();
    }

    private void Publish()
    {
        bool completeTiming = timer is not null && validTiming && timedGroups == groups && issuedGroups == groups;
        string? reason = completeTiming ? null : timer is null ? TimestampUnavailableReason
            : !validTiming ? "A timestamp counter regressed or wrapped."
            : issuedGroups != groups ? "The bounded query pool could not cover every command group."
            : IsPending ? "Timestamp results are still pending." : "Timestamp coverage is incomplete.";
        Latest = new(step, count, published, retainedBytes, gridDispatches, integrationDispatches, groups, timedGroups,
            submissionMilliseconds, pollingMilliseconds, wallMilliseconds,
            completeTiming ? gridNanoseconds / 1_000_000.0 : null,
            completeTiming ? integrationNanoseconds / 1_000_000.0 : null, completeTiming,
            issuedGroups, IsPending, reason, collectionCpuMilliseconds, collectionWallMilliseconds,
            maximumBucketOccupancy, receiverChunk, pollCalls, maximumOwnerCallMilliseconds,
            completeTiming ? maximumIntegrationGroupNanoseconds / 1_000_000.0 : null);
    }

    private void Collect()
    {
        if (timer is null) { return; }
        while (timer.TryTake(out GpuTimestampInterval interval))
        {
            if (interval.Tag != tag) { continue; }
            timedGroups++;
            validTiming &= interval.Valid;
            if (interval.Stage == GpuCommandStage.Grid) { gridNanoseconds += interval.Nanoseconds; }
            else if (interval.Stage == GpuCommandStage.Integration)
            {
                integrationNanoseconds += interval.Nanoseconds;
                maximumIntegrationGroupNanoseconds = Math.Max(maximumIntegrationGroupNanoseconds, interval.Nanoseconds);
            }
        }
    }
    public void Dispose() => timer?.Dispose();
}

/// <summary>Accounts one owner call only when enabled; the default value performs no timing or allocation.</summary>
internal readonly struct GpuMeasurementInterval : IDisposable
{
    private readonly GpuStepMeasurements? owner;
    private readonly bool polling;
    private readonly long started;
    internal GpuMeasurementInterval(GpuStepMeasurements owner, bool polling)
    { this.owner = owner; this.polling = polling; started = Stopwatch.GetTimestamp(); }
    public void Dispose() => owner?.EndInterval(started, polling);
}
