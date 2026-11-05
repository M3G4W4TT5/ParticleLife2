using System.Diagnostics;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Viewer;

/// <summary>Owns a CPU editor and simulation on one dedicated completed-boundary worker.</summary>
/// <remarks>
/// Construction transfers exclusive access to the supplied editor and its playback controller.
/// Commands, diagnostics, replay and file operations run only on the worker. Submission and render
/// acquisition never wait for a numerical step. Shutdown requests cancellation at the next completed
/// boundary and joins the worker; an in-flight CPU step is deliberately not interrupted halfway.
/// CPU render copies are demand-driven, independently of numerical steps. This is a CPU owner,
/// not a requirement that future device-resident execution expose CPU state every frame.
/// </remarks>
public sealed class BackgroundExperiment : IDisposable
{
    private const int MaximumCommands = 128;
    private readonly ExperimentEditor editor;
    private readonly object gate = new();
    private readonly LinkedList<Command> commands = new();
    private readonly Queue<string> numericalFailures = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly Thread worker;
    private readonly RenderFrameStorage[] frames = [new(), new(), new()];
    private EditorPublication publication;
    private RenderFrameStorage? publishedFrame;
    private long submitted, applied, frameSequence;
    private double elapsed;
    private int renderRequested = 1;
    private int pauseRequested;
    private int cancelReplayRequested;
    private bool stopping;
    private bool disposed;
    private int busy;
    private long countsRevision = -1;
    private long membershipRevision = -1;
    private int[] populations = [];
    private int[] actualPopulations = [];
    private string? error;
    private readonly Stopwatch rateClock = Stopwatch.StartNew();
    private long rateSteps;
    private double rateSimulationTime;
    private double stepsPerSecond, simulationSpeed;

    /// <summary>Takes ownership of a sequential editor and starts its background worker.</summary>
    /// <param name="editor">Editor whose playback must no longer be accessed by the caller.</param>
    public BackgroundExperiment(ExperimentEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        this.editor = editor;
        publication = CapturePublication();
        PublishFrame();
        worker = new Thread(Run) { IsBackground = true, Name = "Particle Life CPU simulation" };
        worker.Start();
    }

    /// <summary>Gets immutable settings and scalar metadata from the latest completed boundary.</summary>
    public EditorPublication Publication => Volatile.Read(ref publication);

    /// <summary>Gets whether numerical, replay or command work currently occupies the owner.</summary>
    public bool IsBusy => Volatile.Read(ref busy) != 0;

    /// <summary>Gets queued commands, excluding the command or step already executing.</summary>
    public int PendingCommands { get { lock (gate) { return commands.Count; } } }

    /// <summary>Queues an edit or capture at a completed boundary, with bounded unapplied preview coalescing.</summary>
    /// <param name="label">Human-readable command description.</param>
    /// <param name="action">Operation using the exclusively owned editor only during this call.</param>
    /// <param name="previewKey">Optional key; consecutive unapplied previews with the same key replace one another.</param>
    /// <returns>Completion after the actual operation and metadata publication, or its real failure.</returns>
    /// <remarks>Barriers have no key. A superseded preview completes with cancellation and is never
    /// recorded as an applied session event. Every value actually executed uses the normal editor history.</remarks>
    public Task Submit(string label, Action<ExperimentEditor> action, string? previewKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(action);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            if (previewKey is not null && commands.Last?.Value is Command previous && previous.PreviewKey == previewKey)
            {
                commands.RemoveLast();
                previous.Completion.TrySetCanceled();
            }
            if (commands.Count >= MaximumCommands)
            {
                throw new InvalidOperationException("The edit queue is full. Wait for the current completed boundary before adding more actions.");
            }
            Command command = new(++submitted, label, action, previewKey);
            commands.AddLast(command);
            wake.Set();
            return command.Completion.Task;
        }
    }

    /// <summary>Requests immediate eventual pause after the in-flight step, independently of queued edits.</summary>
    /// <remarks>Elapsed demand already submitted is discarded. Resumption is a normal ordered command.</remarks>
    public void RequestPause()
    {
        Interlocked.Exchange(ref pauseRequested, 1);
        lock (gate) { elapsed = 0; }
        wake.Set();
    }

    /// <summary>Requests cancellation of temporary replay at the next completed replay boundary.</summary>
    public void CancelReplay()
    {
        Interlocked.Exchange(ref cancelReplayRequested, 1);
        RequestPause();
    }

    /// <summary>Submits wall-time demand without executing or waiting for a numerical step.</summary>
    /// <param name="elapsedSeconds">Finite nonnegative wall seconds, excluding splash, inactive and modal frames.</param>
    /// <remarks>Only one scalar demand is retained; there is no per-frame work queue.</remarks>
    public void AdvanceFrame(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        }
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            double combined = elapsed + elapsedSeconds;
            elapsed = double.IsFinite(combined) ? combined : double.MaxValue;
        }
        wake.Set();
    }

    /// <summary>Clears presentation demand while an ordered clock-changing command executes.</summary>
    internal void DiscardElapsedDemand()
    {
        lock (gate) { elapsed = 0; }
    }

    /// <summary>Drains one retained numerical failure for UI delivery even after newer metadata is published.</summary>
    internal bool TryTakeNumericalFailure(out string? failure)
    {
        lock (gate)
        {
            if (numericalFailures.Count != 0) { failure = numericalFailures.Dequeue(); return true; }
            failure = null;
            return false;
        }
    }

    /// <summary>Leases the newest completed render copy and requests a later copy when state changes.</summary>
    /// <returns>An independent lease, or null when no completed frame is available.</returns>
    /// <remarks>Dispose the lease after uploading or reading. The worker skips publication if all
    /// reusable buffers are leased; it never waits for rendering and never overwrites a leased buffer.</remarks>
    public RenderFrameLease? AcquireRenderFrame()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            Interlocked.Exchange(ref renderRequested, 1);
            wake.Set();
            if (publishedFrame is not RenderFrameStorage frame)
            {
                return null;
            }
            frame.Readers++;
            return new RenderFrameLease(frame, ReleaseFrame);
        }
    }

    private void ReleaseFrame(RenderFrameStorage frame)
    {
        lock (gate) { frame.Readers--; }
    }

    private void Run()
    {
        try
        {
            while (true)
            {
                lock (gate) { if (stopping) { break; } }
                ServiceRequests();
                Command? command;
                double interval;
                lock (gate)
                {
                    command = commands.First?.Value;
                    if (command is not null) { commands.RemoveFirst(); }
                    interval = elapsed;
                    elapsed = 0;
                }
                if (command is not null)
                {
                    // Preserve presentation demand while draining commands; pause/resume clear it explicitly.
                    lock (gate)
                    {
                        double combined = elapsed + interval;
                        elapsed = double.IsFinite(combined) ? combined : double.MaxValue;
                    }
                    Execute(command);
                    continue;
                }
                bool progressed = false;
                try
                {
                    Volatile.Write(ref busy, 1);
                    if (editor.IsReplaying)
                    {
                        editor.AdvanceSessionReplay(1);
                        progressed = true;
                    }
                    else
                    {
                        editor.Playback.AccumulatePlayback(interval);
                        int completed = 0;
                        while (completed < editor.Playback.MaxStepsPerFrame)
                        {
                            ServiceRequests();
                            lock (gate) { if (stopping || commands.Count != 0) { break; } }
                            double previousTime = editor.Playback.Observe().Time;
                            if (!editor.Playback.AdvancePendingStep()) { break; }
                            rateSteps++;
                            rateSimulationTime += editor.Playback.Observe().Time - previousTime;
                            completed++;
                            progressed = true;
                            PublishRequestedFrame();
                        }
                        if (completed == editor.Playback.MaxStepsPerFrame)
                        {
                            editor.Playback.DiscardWholePlaybackSteps();
                        }
                    }
                    if (progressed) { error = null; }
                }
                catch (Exception failure)
                {
                    editor.Playback.SetPaused(true);
                    editor.CancelSessionReplay();
                    error = failure.Message;
                    lock (gate) { numericalFailures.Enqueue(error); }
                    progressed = true;
                }
                finally { Volatile.Write(ref busy, 0); }
                PublishRequestedFrame();
                if (progressed || interval > 0)
                {
                    UpdateRates();
                    Volatile.Write(ref publication, CapturePublication());
                }
                if (!editor.IsReplaying)
                {
                    lock (gate) { if (commands.Count != 0 || elapsed > 0) { continue; } }
                    wake.WaitOne();
                }
            }
        }
        finally
        {
            editor.CancelSessionReplay();
            editor.Playback.Dispose();
            lock (gate)
            {
                foreach (Command pending in commands) { pending.Completion.TrySetCanceled(); }
                commands.Clear();
            }
        }
    }

    private void ServiceRequests()
    {
        if (Interlocked.Exchange(ref pauseRequested, 0) != 0)
        {
            editor.Playback.SetPaused(true);
            Volatile.Write(ref publication, CapturePublication());
        }
        if (Interlocked.Exchange(ref cancelReplayRequested, 0) != 0)
        {
            editor.CancelSessionReplay();
            Volatile.Write(ref publication, CapturePublication());
        }
    }

    private void Execute(Command command)
    {
        Exception? failure = null;
        bool pausedBefore = editor.Playback.IsPaused;
        double speedBefore = editor.Playback.PlaybackSpeed;
        double timeStepBefore = editor.Configuration.TimeStep;
        ExperimentDefinition experimentBefore = editor.Playback.Experiment;
        long stepBefore = editor.Playback.Observe().Step;
        double timeBefore = editor.Playback.Observe().Time;
        try
        {
            Volatile.Write(ref busy, 1);
            command.Action(editor);
            if (experimentBefore == editor.Playback.Experiment && editor.Playback.Observe().Step > stepBefore)
            {
                rateSteps += editor.Playback.Observe().Step - stepBefore;
                rateSimulationTime += editor.Playback.Observe().Time - timeBefore;
                UpdateRates();
            }
            if (pausedBefore != editor.Playback.IsPaused || speedBefore != editor.Playback.PlaybackSpeed
                || timeStepBefore != editor.Configuration.TimeStep || experimentBefore != editor.Playback.Experiment
                || editor.Playback.Observe().Step < stepBefore)
            {
                DiscardElapsedDemand();
            }
            error = null;
        }
        catch (Exception exception)
        {
            error = $"{command.Label}: {exception.Message}";
            failure = exception;
        }
        finally
        {
            applied = command.Id;
            Volatile.Write(ref busy, 0);
            PublishRequestedFrame();
            Volatile.Write(ref publication, CapturePublication());
        }
        if (failure is null) { command.Completion.TrySetResult(); }
        else { command.Completion.TrySetException(failure); }
    }

    private void PublishRequestedFrame()
    {
        if (Volatile.Read(ref renderRequested) != 0) { PublishFrame(); }
    }

    private void PublishFrame()
    {
        ParticleStateView state = editor.Playback.Observe();
        if (publishedFrame is RenderFrameStorage previous && previous.Step == state.Step
            && previous.Version == editor.Playback.PublicationVersion && previous.Time == state.Time)
        {
            // Keep the request armed for the next changed boundary, without waking or spinning.
            return;
        }
        RenderFrameStorage? destination = null;
        lock (gate)
        {
            foreach (RenderFrameStorage candidate in frames)
            {
                if (candidate != publishedFrame && candidate.Readers == 0) { destination = candidate; break; }
            }
        }
        if (destination is null) { return; }
        destination.Copy(state, editor.Configuration, editor.Playback.PublicationVersion, ++frameSequence);
        lock (gate) { publishedFrame = destination; }
        Interlocked.Exchange(ref renderRequested, 0);
    }

    private void UpdateRates()
    {
        if (rateClock.Elapsed.TotalSeconds >= 1)
        {
            double seconds = rateClock.Elapsed.TotalSeconds;
            stepsPerSecond = rateSteps / seconds;
            simulationSpeed = rateSimulationTime / seconds;
            rateSteps = 0;
            rateSimulationTime = 0;
            rateClock.Restart();
        }
    }

    private EditorPublication CapturePublication()
    {
        if (countsRevision != editor.Revision)
        {
            populations = editor.Populations.ToArray();
            countsRevision = editor.Revision;
        }
        if (membershipRevision != editor.Playback.MembershipVersion)
        {
            actualPopulations = editor.GetCurrentPopulations();
            membershipRevision = editor.Playback.MembershipVersion;
        }
        ParticleStateView state = editor.Playback.Observe();
        return new EditorPublication(editor.Configuration, editor.Generation, editor.Appearance,
            PlaybackSettings.Capture(editor.Playback), populations, actualPopulations, editor.Revision,
            editor.CanUndo, editor.CanRedo, editor.SessionEventCount, editor.Playback.Experiment.Name,
            state.Count, state.Step, state.Time, editor.Playback.IsPaused, editor.Playback.LastExecution,
            editor.Playback.DirectFallbackSteps, editor.Playback.AccumulationFallbackSteps, editor.Playback.LastStatistics,
            editor.Playback.LatestDiagnostic, editor.Playback.DroppedPlaybackSeconds, editor.IsReplaying,
            editor.ReplayCompletedStep, editor.ReplayTerminalStep, applied, error, stepsPerSecond, simulationSpeed);
    }

    /// <summary>Cancels queued work and joins the worker after its current completed boundary.</summary>
    /// <remarks>Only shutdown waits. Input, pause, reset, file workflows and render acquisition never join.</remarks>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) { return; }
            disposed = true;
            stopping = true;
        }
        wake.Set();
        worker.Join();
        wake.Dispose();
    }

    private sealed record Command(long Id, string Label, Action<ExperimentEditor> Action, string? PreviewKey)
    {
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>Immutable UI metadata, containing completed actual settings and scalar state without particle readback.</summary>
/// <remarks>Arrays remain private and immutable after construction. Requested drafts belong to the UI session,
/// while these values describe applied commands and successful numerical boundaries only.</remarks>
public sealed record EditorPublication(
    SimulationConfiguration Configuration, GenerationSettings Generation, AppearanceSettings Appearance,
    PlaybackSettings Playback, ReadOnlyMemory<int> Populations, ReadOnlyMemory<int> ActualPopulations,
    long Revision, bool CanUndo, bool CanRedo, int SessionEventCount, string ExperimentName,
    int Count, long Step, double Time, bool IsPaused, ForceExecution? LastExecution,
    long DirectFallbackSteps, long AccumulationFallbackSteps, ForceEvaluationStatistics LastStatistics,
    DiagnosticSample? LatestDiagnostic, double DroppedPlaybackSeconds, bool IsReplaying,
    long ReplayCompletedStep, long ReplayTerminalStep, long AppliedCommand, string? Error,
    double StepsPerSecond, double SimulationSpeed);

/// <summary>One explicitly owned read lease over a reusable completed CPU render buffer.</summary>
/// <remarks>Spans may only be read until disposal. Holding a lease never prevents numerical advancement.
/// A sequence changes only when a newer complete particle copy is published.</remarks>
public sealed class RenderFrameLease : IDisposable
{
    private readonly RenderFrameStorage storage;
    private Action<RenderFrameStorage>? release;
    internal RenderFrameLease(RenderFrameStorage storage, Action<RenderFrameStorage> release)
    {
        this.storage = storage;
        this.release = release;
    }
    /// <summary>Gets stable particle identities in storage order.</summary>
    public ReadOnlySpan<long> Ids { get { EnsureActive(); return storage.Ids.AsSpan(0, storage.Count); } }
    /// <summary>Gets complete positions in reduced length units.</summary>
    public ReadOnlySpan<Vector2d> Positions { get { EnsureActive(); return storage.Positions.AsSpan(0, storage.Count); } }
    /// <summary>Gets complete velocities in reduced length per simulation time.</summary>
    public ReadOnlySpan<Vector2d> Velocities { get { EnsureActive(); return storage.Velocities.AsSpan(0, storage.Count); } }
    /// <summary>Gets species identifiers in storage order.</summary>
    public ReadOnlySpan<int> Species { get { EnsureActive(); return storage.Species.AsSpan(0, storage.Count); } }
    /// <summary>Gets the complete particle count.</summary>
    public int Count => storage.Count;
    /// <summary>Gets the completed simulation step.</summary>
    public long Step => storage.Step;
    /// <summary>Gets the completed simulation time.</summary>
    public double Time => storage.Time;
    /// <summary>Gets the monotonically increasing CPU publication sequence.</summary>
    public long Sequence => storage.Sequence;
    /// <summary>Gets the configuration belonging to this exact particle copy.</summary>
    public SimulationConfiguration Configuration => storage.Configuration!;
    /// <summary>Releases the storage for later reuse; repeated disposal is harmless.</summary>
    public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke(storage);
    private void EnsureActive() => ObjectDisposedException.ThrowIf(release is null, this);
}

internal sealed class RenderFrameStorage
{
    internal long[] Ids = [];
    internal Vector2d[] Positions = [];
    internal Vector2d[] Velocities = [];
    internal int[] Species = [];
    internal int Count, Readers;
    internal long Step = -1, Version, Sequence;
    internal double Time;
    internal SimulationConfiguration? Configuration;
    internal void Copy(ParticleStateView state, SimulationConfiguration configuration, long version, long sequence)
    {
        if (Positions.Length < state.Count)
        {
            Ids = new long[state.Count];
            Positions = new Vector2d[state.Count];
            Velocities = new Vector2d[state.Count];
            Species = new int[state.Count];
        }
        state.Ids.CopyTo(Ids);
        state.Positions.CopyTo(Positions);
        state.Velocities.CopyTo(Velocities);
        state.Species.CopyTo(Species);
        Count = state.Count;
        Time = state.Time;
        Step = state.Step;
        Version = version;
        Sequence = sequence;
        Configuration = configuration;
    }
}
