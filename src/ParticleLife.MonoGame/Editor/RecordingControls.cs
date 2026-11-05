using ParticleLife.Viewer;

namespace ParticleLife.MonoGame.Editor;

/// <summary>Shows recording limits separately from playback and never disguises discarded undo as lost recording.</summary>
internal static class RecordingControls
{
    private const double MiB = 1024 * 1024;

    internal static void AddTo(UiSection section, EditorSession editor, Action<string> report)
    {
        bool newRecordingConfirmed = false;
        UiSection storage = new("Recording and undo storage", false);
        storage.Children.Add(new UiLabel(() => $"Recording: {editor.SessionEventCount} applied edits, {editor.Recording.ActionBytes / MiB:F2} MiB estimated payload / {editor.Recording.ActionLimitBytes / MiB:G4} MiB limit. Complete history retained.", 58));
        storage.Children.Add(new UiNumber("Recording limit [MiB]", () => editor.Recording.ActionLimitBytes / MiB,
            value => editor.Run("Recording limit", owner => owner.SetRecordingBudgets(Bytes(value), owner.Recording.UndoBudgetBytes), requiresCapture: false),
            256, 1, 8192, "Numerical action-payload estimate, excluding initial state and object overhead. Reaching this limit rejects later edits before applying them. Existing actions are never truncated.", logarithmic: true));
        storage.Children.Add(new UiNumber("Undo budget [MiB]", () => editor.Recording.UndoBudgetBytes / MiB,
            value => editor.Run("Undo budget", owner => owner.SetRecordingBudgets(owner.Recording.ActionLimitBytes, Bytes(value)), requiresCapture: false),
            128, 1, 8192, "Older compressed undo checkpoints may be evicted. The newest action and pending redo remain available even if they exceed this soft budget. The session recording remains complete.", logarithmic: true));
        storage.Children.Add(new UiLabel(() => $"Undo payload: {editor.Recording.UndoBytes / MiB:F2} MiB; {editor.Recording.EvictedUndoActions} older undo actions evicted. File size depends on compression.", 42));
        storage.Children.Add(new UiToggle("I saved the previous recording", () => newRecordingConfirmed,
            value => newRecordingConfirmed = value, "Starting a new recording clears the in-memory timeline and undo. Save a session first if you want to keep it."));
        storage.Children.Add(new UiButton(() => "New recording from here", () =>
        {
            editor.Run("New recording from here", owner => owner.StartRecordingFromHere("Discovery from here"), () =>
            {
                newRecordingConfirmed = false;
                report("New recording started from actual particles and rules. Time reset to zero; paused. Previous history cleared.");
            });
        }, "Preserve current particles and rules, establish a new initial baseline, reset time to zero and pause. Clears previous undo and recording.", () => newRecordingConfirmed));
        section.Children.Add(storage);
    }

    private static long Bytes(double value) => checked((long)Math.Round(value * MiB));
}
