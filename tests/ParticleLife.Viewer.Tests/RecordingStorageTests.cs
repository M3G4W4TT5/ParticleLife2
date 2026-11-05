using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Viewer.Tests;

public sealed class RecordingStorageTests
{
    [Fact]
    public void LongLocalStrokeRetainsCompactActualEditsAndExactUndoReplay()
    {
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 1000, 2, 47), diagnosticEverySteps: 0);
        ExperimentEditor editor = new(playback);
        Vector2d initial = playback.Observe().Positions[0];
        editor.BeginBrushStroke(new() { Kind = BrushKind.Move, Radius = 1e-8 }, initial);
        for (int sample = 1; sample <= 20; sample++) { editor.ApplyBrushSample(new(initial.X + sample * 0.001, initial.Y)); }
        editor.EndBrushStroke();
        Vector2d edited = playback.Observe().Positions[0];
        SessionDefinition session = editor.CaptureSession("Local stroke");
        Assert.Equal(20, session.Events.Count);
        Assert.All(session.Events, change =>
        {
            Assert.Null(change.ReplacementState);
            Assert.Equal(1, change.StateDelta!.Updates.Count);
            Assert.Equal(0, change.StateDelta.Additions.Count);
        });
        Assert.InRange(editor.Recording.ActionBytes, 1, 10_000);
        Assert.InRange(editor.Recording.UndoBytes, 1, 88_256);
        using Simulation replay = SessionReplay.Replay(SessionFile.Deserialize(SessionFile.Serialize(session)));
        Assert.Equal(edited, replay.Observe().Positions[0]);
        Assert.Equal(playback.Observe().Ids.ToArray(), replay.Observe().Ids.ToArray());
        editor.Undo();
        Assert.Equal(initial, playback.Observe().Positions[0]);
        editor.Redo();
        Assert.Equal(edited, playback.Observe().Positions[0]);
        using Simulation afterRedo = SessionReplay.Replay(editor.CaptureSession("Undo and redo"));
        Assert.Equal(playback.Observe().Positions.ToArray(), afterRedo.Observe().Positions.ToArray());
    }

    [Fact]
    public void RecordingLimitRejectsBeforeMutationAndNeverTruncatesAppliedActions()
    {
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 10, 2, 47), diagnosticEverySteps: 0);
        ExperimentEditor editor = new(playback);
        editor.SetRecordingBudgets(1024, 2048);
        for (int force = 2; force <= 4; force++) { SetForce(force); }
        long revision = editor.Revision;
        Assert.Throws<InvalidOperationException>(() => SetForce(5));
        Assert.Equal(4, editor.Configuration.Parameters.ForceScale);
        Assert.Equal(revision, editor.Revision);
        Assert.Equal(3, editor.SessionEventCount);
        Assert.True(editor.CanUndo);
        Assert.Throws<InvalidOperationException>(editor.Undo);
        Assert.Equal(4, editor.Configuration.Parameters.ForceScale);
        Assert.Equal(3, editor.SessionEventCount);
        editor.SetRecordingBudgets(4096, 2048);
        editor.Undo();
        Assert.Equal(3, editor.Configuration.Parameters.ForceScale);
        Assert.Equal(4, editor.SessionEventCount);
        using Simulation replay = SessionReplay.Replay(editor.CaptureSession("Complete"));
        Assert.Equal(3, replay.Configuration.Parameters.ForceScale);

        void SetForce(double force)
        {
            SimulationConfiguration current = editor.Configuration;
            editor.ApplyConfiguration(new(current.Parameters with { ForceScale = force }, current.Matrix,
                current.TimeStep, current.Backend, current.WorkerCount));
        }
    }

    [Fact]
    public void UndoEvictionRetainsRecordingAndExplicitNewRecordingKeepsActualPattern()
    {
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 10, 2, 47), diagnosticEverySteps: 0);
        ExperimentEditor editor = new(playback);
        editor.SetRecordingBudgets(4096, 1024);
        for (int i = 0; i < 6; i++)
        {
            SimulationConfiguration current = editor.Configuration;
            editor.ApplyConfiguration(new(current.Parameters with { ForceScale = 2 + i }, current.Matrix,
                current.TimeStep, current.Backend, current.WorkerCount));
        }
        Assert.Equal(6, editor.SessionEventCount);
        Assert.Equal(2, editor.Recording.EvictedUndoActions);
        Assert.Equal(1024, editor.Recording.UndoBytes);
        playback.SingleStep();
        Vector2d[] actual = playback.Observe().Positions.ToArray();
        editor.StartRecordingFromHere("New discovery");
        Assert.Equal(actual, playback.Observe().Positions.ToArray());
        Assert.Equal(0, playback.Observe().Step);
        Assert.Equal(0, editor.SessionEventCount);
        Assert.Equal(0, editor.Recording.ActionBytes);
        Assert.Equal(0, editor.Recording.UndoBytes);
        Assert.True(playback.IsPaused);
        Assert.False(editor.CanUndo);
        Assert.Equal(1, playback.Experiment.Provenance!.SourceStep);
    }
}
