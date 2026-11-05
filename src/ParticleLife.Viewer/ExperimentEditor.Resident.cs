using ParticleLife.Core.Execution;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Viewer;

/// <summary>Describes explicit captured-state recording without conflating it with action re-simulation.</summary>
/// <param name="EverySteps">Periodic capture cadence; zero disables optional periodic captures.</param>
/// <param name="Count">Retained compressed checkpoints, including transfer boundaries.</param>
/// <param name="StorageBytes">Compressed payload bytes plus a conservative per-checkpoint metadata allowance.</param>
/// <param name="BudgetBytes">Retained checkpoint budget; terminal export is prepared separately.</param>
/// <param name="LatestStep">Newest owned checkpoint boundary, or null.</param>
/// <param name="Notice">Visible recording limitation, if any.</param>
public sealed record CaptureRecordingStatus(int EverySteps, int Count, long StorageBytes, long BudgetBytes, long? LatestStep, string? Notice);

public sealed partial class ExperimentEditor
{
    private const long CaptureBudgetBytes = 64L * 1024 * 1024;
    private readonly List<RecordedCapture> captures = [];
    private readonly List<ExecutionTransition> executionTransitions = [];
    private long captureBytes;
    private int captureEverySteps = 600;
    private string? captureNotice;

    /// <summary>Gets checkpoint cadence, retained bytes and latest recoverable boundary.</summary>
    public CaptureRecordingStatus CaptureRecording => new(captureEverySteps, captures.Count, captureBytes,
        CaptureBudgetBytes, captures.Count == 0 ? null : captures[^1].State.Step, captureNotice);

    /// <summary>Gets whether saving this timeline requires actual captured state and execution metadata.</summary>
    public bool HasCapturedExecution => Playback.Resident is not null || executionTransitions.Count != 0;

    /// <summary>Changes explicit periodic capture cadence without requesting a state download.</summary>
    /// <param name="everySteps">Completed steps between captures; zero disables optional captures.</param>
    public void SetCaptureCadence(int everySteps)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(everySteps);
        captureEverySteps = everySteps;
        captureNotice = null;
        Revision++;
    }

    /// <summary>Captures the mandatory terminal boundary after its owner has explicitly made state observable.</summary>
    /// <param name="name">Saved action-session name.</param>
    /// <returns>Actual values plus action history and precision/device provenance; no numerical replay occurs.</returns>
    public CapturedSessionDefinition CaptureRecordedSession(string name)
    {
        SimulationCapture terminal = new(Configuration, Snapshot(), Playback.Execution, events.Count, "terminal");
        List<SimulationCapture> recorded = captures.Select(capture => capture.Restore()).ToList();
        recorded.Add(terminal);
        return new(CaptureSession(name), recorded, executionTransitions);
    }

    /// <summary>Restores the exact terminal values on CPU, retaining the source action history and its clock.</summary>
    /// <param name="session">Already validated captured session; float32 widens exactly to CPU double storage.</param>
    /// <remarks>This action performs no re-simulation and makes no claim that subsequent CPU motion follows
    /// the recorded GPU trajectory. The original initial experiment remains the reset target.</remarks>
    public void RestoreCapturedSession(CapturedSessionDefinition session)
    {
        ArgumentNullException.ThrowIfNull(session);
        EnsureNotReplaying();
        SimulationCapture terminal = session.TerminalCapture;
        List<RecordedCapture> restoredCaptures = session.Captures.Select(capture => new RecordedCapture(capture.Configuration,
            CompressedParticleSnapshot.Pack(capture.State), capture.Execution, capture.AppliedEventCount, capture.Reason)).ToList();
        ExecutionProvenance destination = ExecutionProvenance.Cpu(terminal.Configuration);
        restoredCaptures.Add(new(terminal.Configuration, restoredCaptures[^1].State, destination, session.Actions.Events.Count, "transfer"));
        long restoredBytes = restoredCaptures.Sum(capture => capture.StorageBytes);
        if (restoredBytes > CaptureBudgetBytes)
        {
            throw new InvalidOperationException("The captured session exceeds the 64 MiB retained checkpoint budget; the active experiment is unchanged.");
        }
        Simulation candidate = new(terminal.State, terminal.Configuration.Parameters, terminal.Configuration.Matrix,
            terminal.Configuration.TimeStep, terminal.Configuration.Backend, terminal.Configuration.WorkerCount);
        Playback.AdoptReplay(candidate, session.Actions.InitialExperiment);
        ClearTimeline();
        events.AddRange(session.Actions.Events);
        recordedBytes = session.Actions.Events.Sum(EstimateActionBytes);
        AdvanceRecordedIdentities(session.Actions);
        captures.AddRange(restoredCaptures);
        captureBytes = restoredBytes;
        executionTransitions.AddRange(session.Transitions);
        executionTransitions.Add(new(terminal.State.Step, terminal.State.Time, events.Count, terminal.Execution, destination, false));
        Playback.SetPaused(true);
        Revision++;
    }

    /// <summary>Returns to the latest owned checkpoint after device loss, explicitly discarding uncaptured continuation.</summary>
    public void RecoverCpuFromLatestCapture()
    {
        if (captures.Count == 0) { throw new InvalidOperationException("No owned checkpoint is available for CPU recovery."); }
        SimulationCapture capture = captures[^1].Restore();
        RecordedCapture destination = PrepareCapture(new(capture.Configuration, capture.State,
            ExecutionProvenance.Cpu(capture.Configuration), capture.AppliedEventCount, "transfer"), required: true)!;
        Playback.ReturnToCpu(capture.State, capture.Configuration);
        if (events.Count > capture.AppliedEventCount) { events.RemoveRange(capture.AppliedEventCount, events.Count - capture.AppliedEventCount); }
        recordedBytes = events.Sum(EstimateActionBytes);
        history.Clear();
        retainedUndoBytes = 0;
        evictedUndoActions = 0;
        historyPosition = 0;
        gestureBefore = null;
        gestureChanged = false;
        brushStroke = null;
        BaselineVersion++;
        Playback.SetPaused(true);
        populations = GetCurrentPopulations();
        captureNotice = $"Recovered captured step {capture.State.Step}; uncaptured continuation was discarded.";
        AddCapture(destination);
        executionTransitions.Add(new(capture.State.Step, capture.State.Time, events.Count, capture.Execution, Playback.Execution, false));
        Revision++;
    }

    internal bool IsPeriodicCaptureDue => captureEverySteps > 0 && Playback.Boundary.Step > 0
        && Playback.Boundary.Step % captureEverySteps == 0
        && (captures.Count == 0 || captures[^1].State.Step != Playback.Boundary.Step);

    internal void RecordActualCapture(ParticleSnapshot state, string reason) =>
        StoreCapture(new(Configuration, state, Playback.Execution, events.Count, reason), required: reason != "periodic");

    internal void AdoptResident(IResidentSimulation candidate)
    {
        ExecutionProvenance source = Playback.Execution;
        ParticleSnapshot actual = SnapshotOf(candidate.ObserveCaptured());
        bool converted = !SameState(actual, Playback.Observe());
        SessionEvent? conversion = converted ? PrepareRecordedEvent(Configuration, actual, Snapshot(),
            "Convert particle state to GPU float32") : null;
        SimulationCapture destination = new(Configuration, actual, candidate.Execution, events.Count + (converted ? 1 : 0), "transfer");
        RecordedCapture packed = PrepareCapture(destination, required: true)!;
        Playback.AdoptResident(candidate);
        if (conversion is not null) { AppendRecordedEvent(conversion); }
        AddCapture(packed);
        executionTransitions.Add(new(actual.Step, actual.Time, events.Count, source, candidate.Execution, converted));
        Revision++;
    }

    internal void ReturnToCpu()
    {
        ParticleSnapshot actual = Snapshot();
        ExecutionProvenance source = Playback.Execution;
        SimulationCapture destination = new(Configuration, actual, ExecutionProvenance.Cpu(Configuration), events.Count, "transfer");
        RecordedCapture packed = PrepareCapture(destination, required: true)!;
        Playback.ReturnToCpu(actual);
        AddCapture(packed);
        executionTransitions.Add(new(actual.Step, actual.Time, events.Count, source, Playback.Execution, false));
        Revision++;
    }

    private static ParticleSnapshot SnapshotOf(ParticleStateView state) =>
        new(state.Ids, state.Positions, state.Velocities, state.Species, state.Time, state.Step);

    /// <summary>Reserves every recorded identity, including particles removed before the restored boundary.</summary>
    /// <remarks>Integration does not allocate identities. Loading only terminal membership would reuse
    /// previously allocated identities after a removal, making later edits ambiguous in the same timeline.</remarks>
    private void AdvanceRecordedIdentities(SessionDefinition session)
    {
        AdvanceIdentityHighWater(session.InitialExperiment.InitialState.Ids);
        foreach (SessionEvent change in session.Events)
        {
            if (change.ReplacementState is { } replacement) { AdvanceIdentityHighWater(replacement.Ids); }
            if (change.StateDelta is { } delta)
            {
                AdvanceIdentityHighWater(delta.RemovedIds);
                AdvanceIdentityHighWater(delta.Updates.Ids);
                AdvanceIdentityHighWater(delta.Additions.Ids);
            }
        }
    }

    private void ClearCapturedTimeline()
    {
        captures.Clear();
        executionTransitions.Clear();
        captureBytes = 0;
        captureNotice = null;
    }

    private void RecordInitialPrecisionConversion()
    {
        if (Playback.Resident is not { } resident) { return; }
        ParticleSnapshot actual = Snapshot();
        if (!SameState(actual, Playback.Experiment.InitialState.Observe()))
        {
            // A fresh baseline must retain its actual precision conversion even when a previously
            // selected recording limit is smaller. Its accounted payload blocks later physical edits;
            // neither the baseline nor an already-applied conversion is silently omitted.
            AppendRecordedEvent(new(actual.Step, Configuration, actual, "Convert initial particle state to GPU float32"));
        }
        StoreCapture(new(Configuration, actual, resident.Execution, events.Count, "transfer"), required: true);
    }

    private void StoreCapture(SimulationCapture capture, bool required)
    {
        RecordedCapture? packed = PrepareCapture(capture, required);
        if (packed is not null) { AddCapture(packed); }
    }

    private RecordedCapture? PrepareCapture(SimulationCapture capture, bool required)
    {
        RecordedCapture packed = new(capture.Configuration, CompressedParticleSnapshot.Pack(capture.State),
            capture.Execution, capture.AppliedEventCount, capture.Reason);
        while (captureBytes + packed.StorageBytes > CaptureBudgetBytes)
        {
            int removable = captures.FindIndex(item => item.Reason == "periodic");
            if (removable < 0)
            {
                captureNotice = "Checkpoint storage reached its 64 MiB budget; save and start a new recording to retain more transfers.";
                if (required) { throw new InvalidOperationException(captureNotice); }
                captureEverySteps = 0;
                return null;
            }
            captureBytes -= captures[removable].StorageBytes;
            captures.RemoveAt(removable);
            captureNotice = "Older optional periodic checkpoints were evicted from the 64 MiB checkpoint budget.";
        }
        return packed;
    }

    private void AddCapture(RecordedCapture capture)
    {
        captures.Add(capture);
        captureBytes += capture.StorageBytes;
    }

    private sealed record RecordedCapture(SimulationConfiguration Configuration, CompressedParticleSnapshot State,
        ExecutionProvenance Execution, int AppliedEvents, string Reason)
    {
        internal long StorageBytes => State.StorageBytes + 256;
        internal SimulationCapture Restore() => new(Configuration, State.Restore(), Execution, AppliedEvents, Reason);
    }
}
