using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;

namespace ParticleLife.Viewer;

/// <summary>Connects UI drafts and ordered commands to a sequential or background CPU editor.</summary>
/// <remarks>Use on one UI thread. The background constructor transfers editor ownership; getters
/// read completed publications. Requested settings are shown immediately while pending, while
/// <see cref="Publication"/> always reports actual applied settings. Operations and their failures
/// complete through <see cref="Pump"/> on the UI thread. The sequential adapter preserves graphics-free
/// widget checks and does not take ownership of the supplied playback controller.</remarks>
public sealed class EditorSession : IDisposable
{
    private readonly ExperimentEditor? sequential;
    private readonly BackgroundExperiment? background;
    private readonly List<PendingOperation> pending = [];
    private SimulationConfiguration? requestedConfiguration;
    private GenerationSettings? requestedGeneration;
    private AppearanceSettings? requestedAppearance;
    private PlaybackSettings? requestedPlayback;
    private int configurationFields, generationFields, appearanceFields, playbackFields;
    private bool[]? matrixFields;
    private Task? configurationTask, generationTask, appearanceTask, playbackTask;
    private bool? requestedPause;
    private Task? pauseTask;
    private long requestedRevision;
    private string? lastPreviewKey;

    /// <summary>Creates an adapter around an editor, optionally transferring it to a background owner.</summary>
    /// <param name="editor">Existing complete editor.</param>
    /// <param name="runInBackground">True transfers exclusive ownership; false retains caller-owned sequential use.</param>
    public EditorSession(ExperimentEditor editor, bool runInBackground = false)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (runInBackground) { background = new BackgroundExperiment(editor); }
        else { sequential = editor; }
        Playback = new SessionPlayback(this);
    }

    /// <summary>Gets nonblocking playback commands and completed scalar status.</summary>
    public SessionPlayback Playback { get; }
    /// <summary>Gets actual completed settings and metadata, without reading mutable simulation arrays.</summary>
    public EditorPublication Publication => background?.Publication ?? CaptureSequential();
    /// <summary>Gets whether numerical work is scheduled off the UI thread.</summary>
    public bool IsBackground => background is not null;
    /// <summary>Gets queued or executing work, including a dense numerical step.</summary>
    public bool IsBusy => background?.IsBusy ?? false;
    /// <summary>Gets the number of unapplied queued commands.</summary>
    public int PendingCommands => background?.PendingCommands ?? 0;
    /// <summary>Gets whether any UI command has not yet completed.</summary>
    public bool HasPendingCommands => pending.Any(operation => !operation.Task.IsCompleted);
    /// <summary>Gets the latest requested configuration while pending, otherwise the applied configuration.</summary>
    public SimulationConfiguration Configuration => IsPending(configurationTask) ? requestedConfiguration! : Publication.Configuration;
    /// <summary>Gets the latest requested future generation recipe.</summary>
    public GenerationSettings Generation => IsPending(generationTask) ? requestedGeneration! : Publication.Generation;
    /// <summary>Gets immediately responsive presentation drafts while their ordered command is pending.</summary>
    public AppearanceSettings Appearance => IsPending(appearanceTask) ? requestedAppearance! : Publication.Appearance;
    /// <summary>Gets applied future generation counts by species.</summary>
    public ReadOnlySpan<int> Populations => Publication.Populations.Span;
    /// <summary>Gets a UI cache counter including requested drafts and completed edits.</summary>
    public long Revision => Publication.Revision + requestedRevision;
    /// <summary>Gets whether the completed editor has an undo action.</summary>
    public bool CanUndo => Publication.CanUndo;
    /// <summary>Gets whether the completed editor has a redo action.</summary>
    public bool CanRedo => Publication.CanRedo;
    /// <summary>Gets applied trajectory event count, excluding unapplied previews.</summary>
    public int SessionEventCount => Publication.SessionEventCount;
    /// <summary>Gets whether the worker is replaying an isolated temporary session.</summary>
    public bool IsReplaying => Publication.IsReplaying;
    /// <summary>Gets completed temporary replay steps.</summary>
    public long ReplayCompletedStep => Publication.ReplayCompletedStep;
    /// <summary>Gets the temporary replay terminal boundary.</summary>
    public long ReplayTerminalStep => Publication.ReplayTerminalStep;
    /// <summary>Reports real operation or step failures on the UI thread when pumped.</summary>
    public event Action<string>? Failed;

    /// <summary>Queues a concrete editor operation and invokes its callback only after successful completion.</summary>
    /// <param name="label">Human-readable action description.</param>
    /// <param name="operation">Operation executed only by the editor owner at a completed boundary.</param>
    /// <param name="completed">Optional UI-thread callback, never invoked for rejected or cancelled actions.</param>
    /// <param name="previewKey">Optional consecutive unapplied preview coalescing key.</param>
    /// <returns>Actual completion, cancellation or failure.</returns>
    public Task Run(string label, Action<ExperimentEditor> operation, Action? completed = null, string? previewKey = null)
    {
        if (sequential is not null)
        {
            operation(sequential);
            lastPreviewKey = previewKey;
            completed?.Invoke();
            return Task.CompletedTask;
        }
        Task task = background!.Submit(label, operation, previewKey);
        lastPreviewKey = previewKey;
        pending.Add(new PendingOperation(task, completed));
        requestedRevision++;
        return task;
    }

    /// <summary>Delivers completed callbacks and failures without waiting for any pending operation.</summary>
    public void Pump()
    {
        for (int i = 0; i < pending.Count;)
        {
            PendingOperation operation = pending[i];
            if (!operation.Task.IsCompleted) { i++; continue; }
            pending.RemoveAt(i);
            if (operation.Task.IsFaulted)
            {
                Failed?.Invoke(operation.Task.Exception!.GetBaseException().Message);
            }
            else if (!operation.Task.IsCanceled) { operation.Completed?.Invoke(); }
        }
        if (background is not null)
        {
            while (background.TryTakeNumericalFailure(out string? failure)) { Failed?.Invoke(failure!); }
        }
    }

    /// <summary>Begins an undo gesture; its queue position is a preview-coalescing barrier.</summary>
    /// <param name="label">Human-readable gesture description.</param>
    public void BeginGesture(string label) => Run("Begin gesture", editor => editor.BeginGesture(label));
    /// <summary>Ends an undo gesture after all prior previews have actually been applied.</summary>
    public void EndGesture() => Run("End gesture", editor => editor.EndGesture());
    /// <summary>Queues a rule edit, preserving unrelated settings resolved by earlier owner commands.</summary>
    /// <param name="configuration">Desired values relative to the currently displayed configuration.</param>
    /// <param name="label">Undo and replay description.</param>
    public void ApplyConfiguration(SimulationConfiguration configuration, string label = "Edit settings")
    {
        SimulationConfiguration before = Configuration;
        if (!ContinuesPreview("configuration", configurationTask)) { configurationFields = 0; matrixFields = null; }
        ModelParameters previous = before.Parameters;
        ModelParameters desired = configuration.Parameters;
        configurationFields |= Changed(previous.WorldWidth != desired.WorldWidth, 0)
            | Changed(previous.WorldHeight != desired.WorldHeight, 1) | Changed(previous.Cutoff != desired.Cutoff, 2)
            | Changed(previous.ForceScale != desired.ForceScale, 3) | Changed(previous.RepulsionFraction != desired.RepulsionFraction, 4)
            | Changed(previous.DampingHalfLife != desired.DampingHalfLife, 5) | Changed(previous.WallWidth != desired.WallWidth, 6)
            | Changed(previous.WallStrength != desired.WallStrength, 7) | Changed(before.TimeStep != configuration.TimeStep, 8)
            | Changed(before.Backend != configuration.Backend, 9) | Changed(before.WorkerCount != configuration.WorkerCount, 10);
        if (!ReferenceEquals(before.Matrix, configuration.Matrix))
        {
            if (before.Matrix.SpeciesCount != configuration.Matrix.SpeciesCount)
            {
                throw new ArgumentException("Use Apply population to change matrix species dimensions.", nameof(configuration));
            }
            matrixFields ??= new bool[before.Matrix.Values.Length];
            for (int i = 0; i < matrixFields.Length; i++)
            {
                matrixFields[i] |= before.Matrix.Values[i] != configuration.Matrix.Values[i];
            }
        }
        requestedConfiguration = configuration;
        int fields = configurationFields;
        bool[]? matrixChanges = matrixFields is null ? null : (bool[])matrixFields.Clone();
        configurationTask = Run(label, editor => editor.ApplyConfiguration(
            MergeConfiguration(editor.Configuration, configuration, fields, matrixChanges), label), previewKey: "configuration");
    }
    /// <summary>Queues one directed coefficient and optionally its reverse, preserving unrelated matrix edits.</summary>
    /// <param name="receiver">Receiver row.</param><param name="source">Source column.</param>
    /// <param name="value">Coefficient in [-1,1].</param><param name="linkSymmetry">Whether to change the reverse.</param>
    public void SetMatrixCoefficient(int receiver, int source, double value, bool linkSymmetry = false)
    {
        SimulationConfiguration current = Configuration;
        double[] values = current.Matrix.Values.ToArray();
        values[receiver * current.Matrix.SpeciesCount + source] = value;
        if (linkSymmetry) { values[source * current.Matrix.SpeciesCount + receiver] = value; }
        ApplyConfiguration(new SimulationConfiguration(current.Parameters, new InteractionMatrix(current.Matrix.SpeciesCount, values),
            current.TimeStep, current.Backend, current.WorkerCount), "Edit directed coefficient");
    }
    /// <summary>Queues an explicit matrix template barrier.</summary>
    /// <param name="kind">Template kind.</param><param name="seed">Recorded generation seed.</param>
    public void ApplyMatrixTemplate(PresetKind kind, int seed) => Run("Apply matrix", editor => editor.ApplyMatrixTemplate(kind, seed));
    /// <summary>Queues an explicit zero-matrix operation.</summary>
    public void ResetMatrix() => Run("Zero matrix", editor => editor.ResetMatrix());
    /// <summary>Queues a validated generation draft.</summary><param name="settings">Complete recipe.</param>
    public void SetGeneration(GenerationSettings settings)
    {
        settings.Validate();
        GenerationSettings before = Generation;
        if (!ContinuesPreview("generation", generationTask)) { generationFields = 0; }
        generationFields |= Changed(settings.Seed != before.Seed, 0) | Changed(settings.Placement != before.Placement, 1)
            | Changed(settings.Velocity != before.Velocity, 2) | Changed(settings.Speed != before.Speed, 3)
            | Changed(settings.DirectionRadians != before.DirectionRadians, 4);
        int fields = generationFields;
        requestedGeneration = settings;
        generationTask = Run("Generation recipe", editor => editor.SetGeneration(editor.Generation with
        {
            Seed = Has(fields, 0) ? settings.Seed : editor.Generation.Seed,
            Placement = Has(fields, 1) ? settings.Placement : editor.Generation.Placement,
            Velocity = Has(fields, 2) ? settings.Velocity : editor.Generation.Velocity,
            Speed = Has(fields, 3) ? settings.Speed : editor.Generation.Speed,
            DirectionRadians = Has(fields, 4) ? settings.DirectionRadians : editor.Generation.DirectionRadians
        }), previewKey: "generation");
    }
    /// <summary>Queues appearance while displaying it immediately.</summary><param name="settings">Complete appearance.</param>
    public void SetAppearance(AppearanceSettings settings)
    {
        settings.Validate();
        AppearanceSettings before = Appearance;
        if (!ContinuesPreview("appearance", appearanceTask)) { appearanceFields = 0; }
        appearanceFields |= Changed(settings.ParticleRadius != before.ParticleRadius, 0)
            | Changed(settings.ShowDiagnostics != before.ShowDiagnostics, 1) | Changed(settings.ShowHelp != before.ShowHelp, 2)
            | Changed(settings.ShowOverlay != before.ShowOverlay, 3) | Changed(settings.ShowEditor != before.ShowEditor, 4)
            | Changed(settings.UiScale != before.UiScale, 5) | Changed(!settings.SpeciesColors.SequenceEqual(before.SpeciesColors), 6)
            | Changed(settings.Camera != before.Camera, 7) | Changed(settings.ParticleSizeMode != before.ParticleSizeMode, 8)
            | Changed(settings.WorldParticleRadius != before.WorldParticleRadius, 9);
        int fields = appearanceFields;
        requestedAppearance = settings;
        appearanceTask = Run("Appearance", editor =>
        {
            AppearanceSettings current = editor.Appearance;
            AppearanceSettings target = Has(fields, 6)
                ? new AppearanceSettings(settings.SpeciesColors.ToArray()) : current;
            editor.SetAppearance(target with
            {
                ParticleRadius = Has(fields, 0) ? settings.ParticleRadius : current.ParticleRadius,
                ShowDiagnostics = Has(fields, 1) ? settings.ShowDiagnostics : current.ShowDiagnostics,
                ShowHelp = Has(fields, 2) ? settings.ShowHelp : current.ShowHelp,
                ShowOverlay = Has(fields, 3) ? settings.ShowOverlay : current.ShowOverlay,
                ShowEditor = Has(fields, 4) ? settings.ShowEditor : current.ShowEditor,
                UiScale = Has(fields, 5) ? settings.UiScale : current.UiScale,
                Camera = Has(fields, 7) ? settings.Camera : current.Camera,
                ParticleSizeMode = Has(fields, 8) ? settings.ParticleSizeMode : current.ParticleSizeMode,
                WorldParticleRadius = Has(fields, 9) ? settings.WorldParticleRadius : current.WorldParticleRadius
            });
        }, previewKey: "appearance");
    }
    /// <summary>Queues playback controls, independent of numerical timestep.</summary><param name="settings">Complete playback policy.</param>
    public void SetPlayback(PlaybackSettings settings)
    {
        settings.Validate();
        PlaybackSettings before = PlaybackDraft;
        if (!ContinuesPreview("playback", playbackTask)) { playbackFields = 0; }
        playbackFields |= Changed(settings.Speed != before.Speed, 0) | Changed(settings.MaxStepsPerFrame != before.MaxStepsPerFrame, 1)
            | Changed(settings.DiagnosticEverySteps != before.DiagnosticEverySteps, 2)
            | Changed(settings.DiagnosticRadius != before.DiagnosticRadius, 3);
        int fields = playbackFields;
        requestedPlayback = settings;
        playbackTask = Run("Playback settings", editor =>
        {
            PlaybackSettings current = PlaybackSettings.Capture(editor.Playback);
            editor.SetPlayback(current with
            {
                Speed = Has(fields, 0) ? settings.Speed : current.Speed,
                MaxStepsPerFrame = Has(fields, 1) ? settings.MaxStepsPerFrame : current.MaxStepsPerFrame,
                DiagnosticEverySteps = Has(fields, 2) ? settings.DiagnosticEverySteps : current.DiagnosticEverySteps,
                DiagnosticRadius = Has(fields, 3) ? settings.DiagnosticRadius : current.DiagnosticRadius
            });
        }, previewKey: "playback");
    }
    /// <summary>Queues future population counts without generating particles.</summary><param name="counts">Species counts.</param>
    public void SetPopulationRecipe(ReadOnlySpan<int> counts)
    {
        int[] copy = counts.ToArray();
        Run("Population recipe", editor => editor.SetPopulationRecipe(copy));
    }
    /// <summary>Queues a structural population operation with owned count data.</summary>
    /// <param name="speciesCount">Positive species count.</param><param name="counts">Counts per species.</param>
    public void ApplyPopulation(int speciesCount, ReadOnlySpan<int> counts)
    {
        int[] copy = counts.ToArray();
        Run("Apply population", editor => editor.ApplyPopulation(speciesCount, copy));
    }
    /// <summary>Gets independently owned applied population counts for UI drafts.</summary>
    /// <returns>One count per active species.</returns>
    public int[] GetCurrentPopulations() => Publication.ActualPopulations.ToArray();
    /// <summary>Queues an exact-baseline reset.</summary>
    public void Reset() => Run("Reset experiment", editor => { editor.Reset(); background?.DiscardElapsedDemand(); });
    /// <summary>Queues undo after all previously submitted edits.</summary>
    public void Undo() => Run("Undo", editor => editor.Undo());
    /// <summary>Queues redo after all previously submitted edits.</summary>
    public void Redo() => Run("Redo", editor => editor.Redo());
    /// <summary>Queues explicit resampling at the current completed boundary.</summary>
    public void ResampleParticles() => Run("Resample particles", editor => editor.ResampleParticles());
    /// <summary>Begins an ordered direct-tool gesture at an actual world position.</summary>
    /// <param name="settings">Settings captured for the whole stroke.</param><param name="point">World-space start.</param>
    public void BeginBrushStroke(BrushSettings settings, Core.State.Vector2d point) => Run("Begin brush stroke", editor => editor.BeginBrushStroke(settings, point));
    /// <summary>Submits a coalescible unapplied pointer sample; each executed sample retains an actual replay event.</summary>
    /// <param name="point">World-space pointer position.</param>
    public void ApplyBrushSample(Core.State.Vector2d point) => Run("Brush sample", editor => editor.ApplyBrushSample(point), previewKey: "brush");
    /// <summary>Ends the brush undo group after all prior actual samples.</summary>
    public void EndBrushStroke() => Run("End brush stroke", editor => editor.EndBrushStroke());
    /// <summary>Queues an explicitly new generated experiment.</summary>
    /// <param name="kind">Matrix template.</param><param name="name">Experiment name.</param>
    public void NewExperiment(PresetKind kind, string name) => Run("New experiment", editor =>
    {
        editor.NewExperiment(kind, name);
        background?.DiscardElapsedDemand();
    });
    /// <summary>Queues an explicitly new built-in recipe baseline.</summary><param name="kind">Recipe kind.</param>
    public void NewRecipe(PresetKind kind) => Run("New recipe", editor =>
    {
        editor.NewRecipe(kind);
        background?.DiscardElapsedDemand();
    });
    /// <summary>Queues validated experiment loading; failures retain the valid completed experiment.</summary>
    /// <param name="path">Input file.</param><param name="backendOverride">Optional backend override.</param>
    /// <param name="workerOverride">Optional worker override.</param><param name="diagnosticEveryStepsOverride">Optional cadence override.</param>
    public void LoadExperiment(string path, ForceBackend? backendOverride = null, int? workerOverride = null,
        int? diagnosticEveryStepsOverride = null) => Run("Load experiment", editor => editor.LoadExperiment(path,
            backendOverride, workerOverride, diagnosticEveryStepsOverride));
    /// <summary>Cancels temporary replay after the in-flight step without blocking input.</summary>
    public void CancelSessionReplay()
    {
        if (background is not null) { background.CancelReplay(); }
        else { sequential!.CancelSessionReplay(); }
        lastPreviewKey = null;
    }
    /// <summary>Advances the sequential compatibility replay; background replay progresses independently.</summary>
    /// <param name="maxSteps">Positive sequential batch limit.</param><returns>Whether replay has finished.</returns>
    public bool AdvanceSessionReplay(int maxSteps = 8) => sequential?.AdvanceSessionReplay(maxSteps) ?? !IsReplaying;
    /// <summary>Leases the newest CPU render copy without observing worker-owned state.</summary>
    /// <returns>A complete render lease, or null in sequential compatibility mode.</returns>
    public RenderFrameLease? AcquireRenderFrame() => background?.AcquireRenderFrame();
    /// <summary>Releases the background owner at shutdown; sequential controller ownership stays with its caller.</summary>
    public void Dispose() => background?.Dispose();

    internal PlaybackSettings PlaybackDraft => IsPending(playbackTask) ? requestedPlayback! : Publication.Playback;
    internal bool PausedDraft => requestedPause ?? Publication.IsPaused;
    internal void SetPaused(bool paused)
    {
        requestedPause = paused;
        if (paused && background is not null) { background.RequestPause(); }
        pauseTask = Run(paused ? "Pause" : "Resume", editor =>
        {
            background?.DiscardElapsedDemand();
            editor.Playback.SetPaused(paused);
        }, () =>
        {
            if (!IsPending(pauseTask)) { requestedPause = null; }
        });
    }
    internal void SingleStep()
    {
        requestedPause = true;
        background?.RequestPause();
        Run("Single step", editor =>
        {
            background?.DiscardElapsedDemand();
            editor.Playback.SingleStep();
        }, () => requestedPause = null);
    }
    internal void AdvanceFrame(double seconds)
    {
        if (background is not null) { background.AdvanceFrame(seconds); }
        else { sequential!.Playback.AdvanceFrame(seconds); }
    }
    private static bool IsPending(Task? task) => task is not null && !task.IsCompleted;

    /// <summary>Shares touched fields only across consecutive pending previews, never across an ordered command barrier.</summary>
    /// <remarks>Pending displayed values survive a barrier, but its earlier edits must not become part
    /// of a later relative patch: undo, load or reset may have deliberately changed them on the owner.</remarks>
    private bool ContinuesPreview(string key, Task? task) => lastPreviewKey == key && IsPending(task);
    private EditorPublication CaptureSequential()
    {
        ExperimentEditor editor = sequential!;
        return new EditorPublication(editor.Configuration, editor.Generation, editor.Appearance,
            PlaybackSettings.Capture(editor.Playback), editor.Populations.ToArray(), editor.GetCurrentPopulations(),
            editor.Revision, editor.CanUndo, editor.CanRedo, editor.SessionEventCount, editor.Playback.Experiment.Name,
            editor.Playback.Observe().Count, editor.Playback.Observe().Step, editor.Playback.Observe().Time,
            editor.Playback.IsPaused, editor.Playback.LastExecution, editor.Playback.DirectFallbackSteps,
            editor.Playback.AccumulationFallbackSteps, editor.Playback.LastStatistics, editor.Playback.LatestDiagnostic,
            editor.Playback.DroppedPlaybackSeconds, editor.IsReplaying, editor.ReplayCompletedStep,
            editor.ReplayTerminalStep, 0, null, 0, 0);
    }
    /// <summary>Retains fields touched by any unapplied preview, including a drag returning to its starting value.</summary>
    private static int Changed(bool changed, int bit) => changed ? 1 << bit : 0;
    private static bool Has(int fields, int bit) => (fields & (1 << bit)) != 0;

    private static SimulationConfiguration MergeConfiguration(SimulationConfiguration current,
        SimulationConfiguration requested, int fields, bool[]? matrixChanges)
    {
        ModelParameters target = requested.Parameters;
        ModelParameters merged = current.Parameters with
        {
            WorldWidth = Has(fields, 0) ? target.WorldWidth : current.Parameters.WorldWidth,
            WorldHeight = Has(fields, 1) ? target.WorldHeight : current.Parameters.WorldHeight,
            Cutoff = Has(fields, 2) ? target.Cutoff : current.Parameters.Cutoff,
            ForceScale = Has(fields, 3) ? target.ForceScale : current.Parameters.ForceScale,
            RepulsionFraction = Has(fields, 4) ? target.RepulsionFraction : current.Parameters.RepulsionFraction,
            DampingHalfLife = Has(fields, 5) ? target.DampingHalfLife : current.Parameters.DampingHalfLife,
            WallWidth = Has(fields, 6) ? target.WallWidth : current.Parameters.WallWidth,
            WallStrength = Has(fields, 7) ? target.WallStrength : current.Parameters.WallStrength
        };
        InteractionMatrix matrix = current.Matrix;
        if (matrixChanges is not null)
        {
            if (requested.Matrix.SpeciesCount != current.Matrix.SpeciesCount)
            {
                throw new InvalidOperationException("Species changed before this matrix draft could apply. Review the completed matrix and retry.");
            }
            double[] values = current.Matrix.Values.ToArray();
            for (int i = 0; i < values.Length; i++)
            {
                if (matrixChanges[i]) { values[i] = requested.Matrix.Values[i]; }
            }
            matrix = new InteractionMatrix(current.Matrix.SpeciesCount, values);
        }
        return new SimulationConfiguration(merged, matrix,
            Has(fields, 8) ? requested.TimeStep : current.TimeStep,
            Has(fields, 9) ? requested.Backend : current.Backend,
            Has(fields, 10) ? requested.WorkerCount : current.WorkerCount);
    }
    private sealed record PendingOperation(Task Task, Action? Completed);
}

/// <summary>Nonblocking playback controls paired with published scalar CPU status.</summary>
public sealed class SessionPlayback
{
    private readonly EditorSession session;
    internal SessionPlayback(EditorSession session) => this.session = session;
    /// <summary>Gets immediate requested pause status; actual completion is in the session publication.</summary>
    public bool IsPaused => session.PausedDraft;
    /// <summary>Gets requested playback speed.</summary>
    public double PlaybackSpeed => Settings.Speed;
    /// <summary>Gets requested catch-up scheduling cap.</summary>
    public int MaxStepsPerFrame => Settings.MaxStepsPerFrame;
    /// <summary>Gets requested completed-step diagnostic cadence.</summary>
    public int DiagnosticEverySteps => Settings.DiagnosticEverySteps;
    /// <summary>Gets requested diagnostic connectivity radius.</summary>
    public double? DiagnosticRadius => Settings.DiagnosticRadius;
    /// <summary>Gets requested playback settings, distinct from applied publication settings.</summary>
    public PlaybackSettings Settings => session.PlaybackDraft;
    /// <summary>Gets last completed actual force execution.</summary>
    public ForceExecution? LastExecution => session.Publication.LastExecution;
    /// <summary>Gets successful direct fallback steps since reset.</summary>
    public long DirectFallbackSteps => session.Publication.DirectFallbackSteps;
    /// <summary>Gets successful accumulation fallback steps since reset.</summary>
    public long AccumulationFallbackSteps => session.Publication.AccumulationFallbackSteps;
    /// <summary>Gets last completed force candidate and accepted-pair counts.</summary>
    public ForceEvaluationStatistics LastStatistics => session.Publication.LastStatistics;
    /// <summary>Gets latest completed diagnostic sample.</summary>
    public DiagnosticSample? LatestDiagnostic => session.Publication.LatestDiagnostic;
    /// <summary>Gets completed dropped simulation-time demand.</summary>
    public double DroppedPlaybackSeconds => session.Publication.DroppedPlaybackSeconds;
    /// <summary>Reads completed scalar metadata without accessing simulation arrays.</summary>
    /// <returns>The latest actual completed publication.</returns>
    public EditorPublication Observe() => session.Publication;
    /// <summary>Requests ordered playback status.</summary><param name="paused">Requested pause state.</param>
    public void SetPaused(bool paused) => session.SetPaused(paused);
    /// <summary>Queues exactly one step and eventual pause.</summary>
    public void SingleStep() => session.SingleStep();
    /// <summary>Submits wall-time demand without waiting for a numerical step.</summary>
    /// <param name="seconds">Finite nonnegative active wall time.</param>
    public void AdvanceFrame(double seconds) => session.AdvanceFrame(seconds);
}
