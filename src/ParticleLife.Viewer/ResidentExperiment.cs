using System.Diagnostics;
using ParticleLife.Core.State;

namespace ParticleLife.Viewer;

/// <summary>Services one resident editor on its graphics owner without blocking waits or implicit observations.</summary>
/// <remarks>Commands are ordered and bounded. A command explicitly declares whether it needs an owned
/// state capture; scalar rules and presentation bypass capture. Structural commands wait for a completed
/// step, request one copy, then run with that exact observation. Each pump submits/polls for at most two
/// milliseconds of CPU time; one native submission is not preempted. No command is inferred from its label.</remarks>
internal sealed class ResidentExperiment : IDisposable
{
    private readonly LinkedList<Command> commands = new();
    private readonly Queue<string> failures = new();
    private readonly Stopwatch rateClock = Stopwatch.StartNew();
    private Command? active;
    private bool activeExecuted, periodicCapture, detached;
    private long submitted, applied, rateSteps;
    private double elapsed, rateTime, stepsPerSecond, simulationSpeed;
    private string? error;
    private long populationsRevision = -1, membershipRevision = -1;
    private int[] populations = [], actualPopulations = [];

    internal ResidentExperiment(ExperimentEditor editor)
    {
        Editor = editor;
        Publication = Publish();
    }

    internal ExperimentEditor Editor { get; }
    internal EditorPublication Publication { get; private set; }
    internal bool Faulted { get; private set; }
    internal bool IsBusy => active is not null || Editor.Playback.Resident?.IsStepPending == true
        || Editor.Playback.Resident?.IsCapturePending == true;
    internal int PendingCommands => commands.Count;

    internal Task Submit(string label, Action<ExperimentEditor> operation, bool requiresCapture, string? previewKey)
    {
        if (previewKey is not null && commands.Last?.Value is { } previous && previous.PreviewKey == previewKey)
        {
            commands.RemoveLast();
            previous.Completion.TrySetCanceled();
        }
        if (commands.Count >= 128) { throw new InvalidOperationException("The resident edit queue is full; wait for a completed boundary."); }
        Command command = new(++submitted, label, operation, requiresCapture, previewKey);
        commands.AddLast(command);
        return command.Completion.Task;
    }

    internal void RequestPause()
    {
        elapsed = 0;
        Editor.Playback.SetPaused(true);
    }

    internal void AdvanceFrame(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) { throw new ArgumentOutOfRangeException(nameof(seconds)); }
        double total = elapsed + seconds;
        elapsed = double.IsFinite(total) ? total : double.MaxValue;
    }

    internal void Pump()
    {
        long started = Stopwatch.GetTimestamp();
        int completed = 0;
        try
        {
            if (!Faulted) { Editor.Playback.AccumulatePlayback(elapsed); }
            Editor.Playback.LimitResidentPlaybackDemand();
            elapsed = 0;
            do
            {
                IResidentSimulation? owner = Editor.Playback.Resident;
                if (owner is null) { break; }
                if (!Faulted)
                {
                    owner.PollSummary();
                    if (owner.IsStepPending)
                    {
                        double before = Editor.Playback.Boundary.Time;
                        if (Editor.Playback.AdvancePendingStep())
                        {
                            completed++;
                            rateSteps++;
                            rateTime += Editor.Playback.Boundary.Time - before;
                        }
                        else { continue; }
                    }
                    if (owner.IsCapturePending)
                    {
                        ParticleSnapshot? state = owner.PollCapture();
                        if (state is null) { continue; }
                        if (periodicCapture)
                        {
                            Editor.RecordActualCapture(state, "periodic");
                            periodicCapture = false;
                        }
                    }
                }
                if (activeExecuted)
                {
                    CompleteCommand(null);
                    continue;
                }
                if (active is null && commands.First is { } first)
                {
                    active = first.Value;
                    commands.RemoveFirst();
                }
                if (active is { } command)
                {
                    if (command.RequiresCapture && !owner.HasCurrentCapture)
                    {
                        if (Faulted)
                        {
                            CompleteCommand(new InvalidOperationException("The device is unavailable. Recover CPU from the latest owned checkpoint explicitly."));
                            continue;
                        }
                        owner.BeginCapture();
                        continue;
                    }
                    try
                    {
                        command.Operation(Editor);
                        if (!Editor.Playback.IsPaused) { Faulted = false; }
                        activeExecuted = true;
                        // CPU action replay runs on the CPU worker after transfer, never in this pump.
                        if (Editor.IsReplaying && Editor.Playback.Resident is not null) { Editor.ReturnToCpu(); }
                        if (Editor.Playback.Resident?.IsStepPending != true) { CompleteCommand(null); }
                    }
                    catch (Exception failure) { CompleteCommand(failure); }
                    continue;
                }
                if (Faulted) { break; }
                if (Editor.IsPeriodicCaptureDue)
                {
                    periodicCapture = true;
                    owner.BeginCapture();
                    continue;
                }
                if (completed >= Editor.Playback.MaxStepsPerFrame)
                {
                    Editor.Playback.DiscardWholePlaybackSteps();
                    break;
                }
                Editor.Playback.AdvancePendingStep();
                if (!owner.IsStepPending) { break; }
            }
            while (Stopwatch.GetElapsedTime(started).TotalMilliseconds < 2);
        }
        catch (Exception failure) { ReportFailure(failure.Message); }
        if (rateClock.Elapsed.TotalSeconds >= 1)
        {
            stepsPerSecond = rateSteps / rateClock.Elapsed.TotalSeconds;
            simulationSpeed = rateTime / rateClock.Elapsed.TotalSeconds;
            rateSteps = 0;
            rateTime = 0;
            rateClock.Restart();
        }
        Publication = Publish();
    }

    internal void ReportFailure(string failure)
    {
        Faulted = true;
        RequestPause();
        error = failure;
        failures.Enqueue(failure);
        if (active is not null) { CompleteCommand(new InvalidOperationException(failure)); }
    }

    internal bool TryTakeFailure(out string? failure)
    {
        if (failures.TryDequeue(out string? value)) { failure = value; return true; }
        failure = null;
        return false;
    }

    internal BackgroundExperiment MoveToBackground()
    {
        if (IsBusy) { throw new InvalidOperationException("Complete the active command before detaching a resident owner."); }
        BackgroundExperiment destination = new(Editor);
        foreach (Command command in commands)
        {
            EditorSession.ForwardCompletion(destination.Submit(command.Label, command.Operation, command.PreviewKey), command.Completion);
        }
        commands.Clear();
        detached = true;
        return destination;
    }

    private void CompleteCommand(Exception? failure)
    {
        Command command = active!;
        applied = command.Id;
        active = null;
        activeExecuted = false;
        if (failure is null) { error = null; command.Completion.TrySetResult(); }
        else { error = command.Label + ": " + failure.Message; command.Completion.TrySetException(failure); }
    }

    private EditorPublication Publish()
    {
        if (populationsRevision != Editor.Revision)
        {
            populations = Editor.Populations.ToArray();
            populationsRevision = Editor.Revision;
        }
        if (membershipRevision != Editor.Playback.MembershipVersion)
        {
            actualPopulations = Editor.GetCurrentPopulations();
            membershipRevision = Editor.Playback.MembershipVersion;
        }
        SimulationBoundary state = Editor.Playback.Boundary;
        return new(Editor.Configuration, Editor.Generation, Editor.Appearance, PlaybackSettings.Capture(Editor.Playback),
            populations, actualPopulations, Editor.Revision, Editor.CanUndo, Editor.CanRedo, Editor.SessionEventCount,
            Editor.Playback.Experiment.Name, state.Count, state.Step, state.Time, Editor.Playback.IsPaused,
            Editor.Playback.LastExecution, Editor.Playback.DirectFallbackSteps, Editor.Playback.AccumulationFallbackSteps,
            Editor.Playback.LastStatistics, Editor.Playback.LatestDiagnostic, Editor.Playback.DroppedPlaybackSeconds,
            Editor.IsReplaying, Editor.ReplayCompletedStep, Editor.ReplayTerminalStep, applied, error, stepsPerSecond, simulationSpeed)
        {
            Execution = Editor.Playback.Execution,
            Recording = Editor.Recording,
            BaselineVersion = Editor.BaselineVersion,
            CaptureRecording = Editor.CaptureRecording,
            ResidentSummary = Editor.Playback.Resident?.LatestSummary,
            FullStateReadbacks = Editor.Playback.Resident?.FullStateReadbacks ?? 0,
            MetadataReadbackBytes = Editor.Playback.Resident?.MetadataReadbackBytes ?? 0,
            DeviceFaulted = Faulted
        };
    }

    public void Dispose()
    {
        foreach (Command command in commands) { command.Completion.TrySetCanceled(); }
        commands.Clear();
        active?.Completion.TrySetCanceled();
        if (!detached) { Editor.Playback.Dispose(); }
    }

    private sealed record Command(long Id, string Label, Action<ExperimentEditor> Operation, bool RequiresCapture, string? PreviewKey)
    {
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
