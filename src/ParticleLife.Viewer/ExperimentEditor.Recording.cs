using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Viewer;

/// <summary>Reports retained action payload and compressed undo budgets without particle observation.</summary>
/// <param name="ActionBytes">Conservative numerical action payload estimate, excluding the initial state and object overhead.</param>
/// <param name="ActionLimitBytes">Limit checked before accepting another physical edit.</param>
/// <param name="UndoBytes">Compressed undo payload plus a small per-action estimate.</param>
/// <param name="UndoBudgetBytes">Soft undo budget; the newest action is retained even when it alone exceeds this value.</param>
/// <param name="EvictedUndoActions">Old undo entries discarded to meet the undo budget; recorded actions are never discarded.</param>
public sealed record RecordingStatus(long ActionBytes, long ActionLimitBytes, long UndoBytes,
    long UndoBudgetBytes, long EvictedUndoActions)
{
    /// <summary>Gets immutable initial budgets for a newly created editor.</summary>
    public static RecordingStatus Empty { get; } = new(0, 256L * 1024 * 1024, 0, 128L * 1024 * 1024, 0);
}

public sealed partial class ExperimentEditor
{
    private long recordedBytes;
    private long actionLimitBytes = 256L * 1024 * 1024;
    private long undoBudgetBytes = 128L * 1024 * 1024;
    private long evictedUndoActions;
    private long retainedUndoBytes;

    /// <summary>Gets current recording and undo storage policy, independently of numerical advancement.</summary>
    /// <remarks>Action bytes estimate retained numeric payload; they are not an exact file size.
    /// Older undo checkpoints may be evicted, but the complete applied action timeline is retained.</remarks>
    public RecordingStatus Recording => new(recordedBytes, actionLimitBytes, UndoPayloadBytes, undoBudgetBytes, evictedUndoActions);

    /// <summary>Sets explicit storage budgets without changing or truncating any recorded action.</summary>
    /// <param name="actionBytes">Positive action limit from 1 KiB through 8 GiB; below-current values block later physical edits.</param>
    /// <param name="undoBytes">Soft undo budget in the same range; retains at least the newest undo entry.</param>
    public void SetRecordingBudgets(long actionBytes, long undoBytes)
    {
        const long maximum = 8L * 1024 * 1024 * 1024;
        if (actionBytes < 1024 || actionBytes > maximum || undoBytes < 1024 || undoBytes > maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(actionBytes), "Recording and undo budgets must be from 1 KiB through 8 GiB.");
        }
        actionLimitBytes = actionBytes;
        undoBudgetBytes = undoBytes;
        TrimUndoBudget();
        Revision++;
    }

    /// <summary>Explicitly starts a fresh recording from actual current values, rebasing its clock to zero.</summary>
    /// <param name="name">Name for the new initial-state baseline.</param>
    /// <remarks>Save the previous session first to retain it. This deliberate operation clears undo and
    /// action history, preserves actual particles/rules, records source-clock provenance and pauses.</remarks>
    public void StartRecordingFromHere(string name)
    {
        EnsureNotReplaying();
        ExperimentDefinition captured = CaptureCurrentExperiment(name);
        ReplaceBaseline(captured, pause: true);
    }

    private long UndoPayloadBytes => retainedUndoBytes;

    private void RemoveRedoHistory()
    {
        if (historyPosition >= history.Count) { return; }
        for (int i = historyPosition; i < history.Count; i++) { retainedUndoBytes -= history[i].StorageBytes; }
        history.RemoveRange(historyPosition, history.Count - historyPosition);
    }

    /// <summary>Prepares the exact action and checks capacity before numerical state can be published.</summary>
    private SessionEvent PrepareRecordedEvent(SimulationConfiguration configuration, ParticleSnapshot? state,
        ParticleSnapshot? beforeState, string label, GeneratorMetadata? generator = null)
    {
        ParticleStateDelta? delta = state is not null && beforeState is not null
            ? ParticleStateDelta.TryCreate(beforeState, state) : null;
        if (delta is not null && delta.PayloadBytes + 128 >= 44L * state!.Count) { delta = null; }
        SessionEvent change = new(Playback.Observe().Step, configuration, delta is null ? state : null, label, generator, delta);
        long required = EstimateActionBytes(change);
        if (required > actionLimitBytes - recordedBytes)
        {
            throw new InvalidOperationException("The recording limit is reached. Save this session, then start a new recording from here or increase its limit. This edit was not applied.");
        }
        events.EnsureCapacity(checked(events.Count + 1));
        return change;
    }

    private void AppendRecordedEvent(SessionEvent change)
    {
        events.Add(change);
        recordedBytes += EstimateActionBytes(change);
    }

    private static long EstimateActionBytes(SessionEvent change) => 256L + 8L * change.Configuration.Matrix.Values.Length
        * (change.Configuration.Matrix.Ranges is null ? 1 : 3)
        + (change.StateDelta?.PayloadBytes ?? 44L * (change.ReplacementState?.Count ?? 0));

    /// <summary>Bounds historical undo storage without ever deleting the action recording.</summary>
    private void TrimUndoBudget()
    {
        while (history.Count > 1 && retainedUndoBytes > undoBudgetBytes)
        {
            // Removing an older applied entry is safe: later undo snapshots are independently owned.
            // Keep redo entries when nothing has yet been applied; they remain accessible in order.
            if (historyPosition <= 1) { break; }
            retainedUndoBytes -= history[0].StorageBytes;
            history.RemoveAt(0);
            historyPosition--;
            evictedUndoActions++;
        }
    }
}
