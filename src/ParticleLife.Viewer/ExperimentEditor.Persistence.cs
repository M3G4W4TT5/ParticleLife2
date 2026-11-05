using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;

namespace ParticleLife.Viewer;

public sealed partial class ExperimentEditor
{
    private SessionReplayRunner? pendingReplay;
    private ViewerSettings? pendingReplaySettings;

    /// <summary>Gets whether a temporary session is being replayed before atomic replacement.</summary>
    public bool IsReplaying => pendingReplay is not null;

    /// <summary>Gets completed steps of the pending replay, or zero when no replay is active.</summary>
    public long ReplayCompletedStep => pendingReplay?.Simulation.Observe().Step ?? 0;

    /// <summary>Gets the terminal boundary of the pending replay, or zero when none is active.</summary>
    public long ReplayTerminalStep => pendingReplay?.Session.TerminalStep ?? 0;

    /// <summary>Saves the exact baseline initial experiment, without changing the active state or clock.</summary>
    /// <param name="path">New destination; a separate .viewer.json companion is also exclusively created.</param>
    /// <param name="name">Nonempty saved experiment name; the active baseline name is unchanged.</param>
    public void SaveInitial(string path, string name)
    {
        ExperimentDefinition initial = Playback.Experiment;
        ExperimentDefinition named = new(name, initial.Parameters, initial.Matrix, initial.InitialState, initial.TimeStep,
            initial.Generator, initial.Backend, initial.WorkerCount, initial.Provenance);
        int[] initialPopulations = new int[initial.Matrix.SpeciesCount];
        foreach (int species in initial.InitialState.Species)
        {
            initialPopulations[species]++;
        }

        SaveScientific(path, ExperimentFile.Serialize(named), CaptureViewerSettings(initialPopulations));
    }

    /// <summary>Saves the exact current state as a rebased headless experiment with source-clock metadata.</summary>
    /// <param name="path">New output path; existing main or companion files are protected.</param>
    /// <param name="name">Nonempty experiment name.</param>
    public void SaveCurrent(string path, string name) =>
        SaveScientific(path, ExperimentFile.Serialize(CaptureCurrentExperiment(name)), CaptureViewerSettings(populations));

    /// <summary>Saves a reusable preset containing exact rules, recipe and presentation/playback choices.</summary>
    /// <param name="path">New output path, never overwritten.</param>
    /// <param name="name">Nonempty preset name.</param>
    public void SavePreset(string path, string name) => PresetFile.SaveNew(path, CapturePreset(name));

    /// <summary>Saves exact initial conditions and the complete applied scientific timeline for CLI/viewer replay.</summary>
    /// <param name="path">New scientific-session path; presentation is stored in a separate bound companion.</param>
    /// <param name="name">Nonempty session label applied to the stored initial experiment.</param>
    public void SaveSession(string path, string name) =>
        SaveScientific(path, SessionFile.Serialize(CaptureSession(name)), CaptureViewerSettings(populations));

    /// <summary>Creates an editor from a complete experiment and its optional validated viewer companion.</summary>
    /// <param name="path">Existing legacy/current experiment file.</param>
    /// <param name="backendOverride">Optional explicit execution override included in the reset baseline.</param>
    /// <param name="workerCountOverride">Optional explicit worker override; a changed backend otherwise uses one worker.</param>
    /// <param name="diagnosticEveryStepsOverride">Optional nonnegative cadence overriding the saved viewer setting.</param>
    /// <returns>An editor paused at the saved initial state, with no undo history.</returns>
    /// <remarks>The caller owns the returned editor's <see cref="Playback"/> and must dispose it.
    /// Parsing and companion validation precede controller construction; setup failure disposes any
    /// newly created controller. No generated fallback population is needed for a valid saved file.</remarks>
    public static ExperimentEditor CreateFromFile(string path, ForceBackend? backendOverride = null,
        int? workerCountOverride = null, int? diagnosticEveryStepsOverride = null)
    {
        (ExperimentDefinition experiment, ViewerSettings? settings) = PrepareExperiment(path, backendOverride,
            workerCountOverride, diagnosticEveryStepsOverride);
        PlaybackController playback = new(experiment);
        try
        {
            ExperimentEditor editor = new(playback);
            editor.PublishLoadedSettings(experiment, settings, diagnosticEveryStepsOverride);
            playback.SetPaused(true);
            return editor;
        }
        catch
        {
            playback.Dispose();
            throw;
        }
    }

    /// <summary>Loads a complete experiment paused, establishing a new exact reset baseline.</summary>
    /// <param name="path">Existing legacy/current experiment, optionally with a validated viewer companion.</param>
    /// <param name="backendOverride">Optional explicit execution override included in the new effective reset baseline.</param>
    /// <param name="workerCountOverride">Optional explicit worker override; a changed backend without this value uses one worker.</param>
    /// <param name="diagnosticEveryStepsOverride">Optional nonnegative cadence overriding the saved viewer setting.</param>
    /// <remarks>Parsing, sidecar validation and replacement construction finish before publication. Any
    /// failure retains the old state, clock, baseline and histories. Reports/presets/sessions are rejected.</remarks>
    public void LoadExperiment(string path, ForceBackend? backendOverride = null, int? workerCountOverride = null,
        int? diagnosticEveryStepsOverride = null)
    {
        EnsureNotReplaying();
        (ExperimentDefinition experiment, ViewerSettings? settings) = PrepareExperiment(path, backendOverride,
            workerCountOverride, diagnosticEveryStepsOverride);
        Playback.ReplaceExperiment(experiment);
        Playback.SetPaused(true);
        ClearTimeline();
        PublishLoadedSettings(experiment, settings, diagnosticEveryStepsOverride);
    }

    private static (ExperimentDefinition Experiment, ViewerSettings? Settings) PrepareExperiment(string path,
        ForceBackend? backendOverride, int? workerCountOverride, int? diagnosticEveryStepsOverride)
    {
        if (diagnosticEveryStepsOverride is int cadence)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(cadence);
        }
        string content = File.ReadAllText(path);
        ExperimentDefinition experiment = ExperimentFile.Deserialize(content);
        ViewerSettings? settings = LoadViewerSettings(path, content, experiment.Matrix.SpeciesCount);
        if (backendOverride is not null || workerCountOverride is not null)
        {
            experiment = experiment.WithBackend(backendOverride ?? experiment.Backend, workerCountOverride);
        }

        return (experiment, settings);
    }

    private void PublishLoadedSettings(ExperimentDefinition experiment, ViewerSettings? settings,
        int? diagnosticEveryStepsOverride)
    {
        if (settings is not null)
        {
            PublishViewerSettings(settings);
        }
        else
        {
            Generation = new GenerationSettings { Seed = experiment.Generator.Seed ?? 1 };
        }
        if (diagnosticEveryStepsOverride is int cadence)
        {
            Playback.DiagnosticEverySteps = cadence;
        }
    }

    /// <summary>Loads a reusable preset and explicitly selects live compatible settings or a new population.</summary>
    /// <param name="path">Existing strict preset file.</param>
    /// <param name="generatePopulation">True creates a new baseline; false changes compatible live settings only.</param>
    public void ApplyPreset(string path, bool generatePopulation)
    {
        EnsureNotReplaying();
        ApplyPreset(PresetFile.Load(path), generatePopulation);
    }

    /// <summary>Replays a session completely using the shared headless engine, then publishes it paused.</summary>
    /// <param name="path">Existing pure session, optionally with a separately validated viewer companion.</param>
    /// <remarks>For native responsiveness use <see cref="BeginSessionReplay"/> and bounded
    /// <see cref="AdvanceSessionReplay"/> calls. Continued editing appends to the loaded scientific
    /// timeline; incompatible undo history is cleared. Reset starts fresh at the loaded initial baseline.</remarks>
    public void LoadSession(string path)
    {
        BeginSessionReplay(path);
        while (!AdvanceSessionReplay(256))
        {
        }
    }

    /// <summary>Validates a session and begins an isolated incremental replay, retaining the active state paused.</summary>
    /// <param name="path">Existing scientific session path.</param>
    /// <remarks>Only successful completion replaces the active experiment. Cancel or failure keeps its
    /// exact state and history; the caller must explicitly resume it. Boundary-zero events are applied by
    /// the shared runner before the first progress call and never cause a hidden numerical step.</remarks>
    public void BeginSessionReplay(string path)
    {
        EnsureNotReplaying();
        string content = File.ReadAllText(path);
        SessionDefinition session = SessionFile.Deserialize(content);
        int finalSpecies = session.Events.Count == 0 ? session.InitialExperiment.Matrix.SpeciesCount
            : session.Events[^1].Configuration.Matrix.SpeciesCount;
        ViewerSettings? settings = LoadViewerSettings(path, content, finalSpecies);
        SessionReplayRunner prepared = new(session);
        EndGesture();
        EndBrushStroke();
        pendingReplay = prepared;
        pendingReplaySettings = settings;
        Playback.SetPaused(true);
        Revision++;
    }

    /// <summary>Completes a bounded number of pending replay steps, publishing only after all terminal edits.</summary>
    /// <param name="maxSteps">Positive cap on completed replay steps in this call; one step may still be expensive.</param>
    /// <returns>True when replay has completed and transferred, or when no replay is pending.</returns>
    /// <remarks>Numerical/event failure disposes temporary workers, retains the previous valid active state
    /// paused, and rethrows a useful error. No events are applied by a second viewer-specific engine.</remarks>
    public bool AdvanceSessionReplay(int maxSteps = 8)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSteps);
        if (pendingReplay is not SessionReplayRunner runner)
        {
            return true;
        }

        try
        {
            for (int i = 0; i < maxSteps && !runner.IsComplete; i++)
            {
                runner.AdvanceOneStep();
            }

            if (!runner.IsComplete)
            {
                return false;
            }

            SessionDefinition session = runner.Session;
            Simulation completed = runner.DetachSimulation();
            Playback.AdoptReplay(completed, session.InitialExperiment);
            ClearTimeline();
            events.AddRange(session.Events);
            AdvanceIdentityHighWater(session.InitialExperiment.InitialState.Ids);
            foreach (SessionEvent change in session.Events)
            {
                if (change.ReplacementState is not null)
                {
                    AdvanceIdentityHighWater(change.ReplacementState.Ids);
                }
            }

            if (pendingReplaySettings is ViewerSettings settings)
            {
                PublishViewerSettings(settings);
            }
            else
            {
                Generation = new GenerationSettings { Seed = session.InitialExperiment.Generator.Seed ?? 1 };
            }

            runner.Dispose();
            pendingReplay = null;
            pendingReplaySettings = null;
            return true;
        }
        catch
        {
            CancelSessionReplay();
            throw;
        }
    }

    /// <summary>Cancels temporary replay and joins its workers, retaining the unchanged active experiment paused.</summary>
    /// <remarks>Call on host shutdown if replay is pending; the editor does not own the active playback controller.</remarks>
    public void CancelSessionReplay()
    {
        pendingReplay?.Dispose();
        pendingReplay = null;
        pendingReplaySettings = null;
    }

    private void EnsureNotReplaying()
    {
        Playback.EnsureActive();
        if (IsReplaying)
        {
            throw new InvalidOperationException("Finish or cancel session replay before editing the active experiment.");
        }
    }

    private ViewerSettings CaptureViewerSettings(int[] counts) => new((int[])counts.Clone(), Generation, Appearance,
        PlaybackSettings.Capture(Playback));

    private void PublishViewerSettings(ViewerSettings settings)
    {
        populations = settings.Populations;
        Generation = settings.Generation;
        Appearance = settings.Appearance;
        ApplyPlayback(settings.Playback);
    }

    private static void SaveScientific(string path, string content, ViewerSettings settings)
    {
        content += "\n";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        string companion = JsonSerializer.Serialize(new ViewerSettingsData("particle-life-viewer-settings", 1, hash,
            settings.Populations, GenerationData.From(settings.Generation), AppearanceData.From(settings.Appearance),
            PlaybackData.From(settings.Playback)), EditorJson.Options) + "\n";
        ExclusiveFileStorage.Save(path, content, companion);
    }

    private static ViewerSettings? LoadViewerSettings(string path, string content, int speciesCount)
    {
        string companionPath = Path.GetFullPath(path) + ".viewer.json";
        if (!File.Exists(companionPath))
        {
            return null;
        }

        ViewerSettingsData data = JsonSerializer.Deserialize<ViewerSettingsData>(File.ReadAllText(companionPath), EditorJson.Options)
            ?? throw new JsonException("Viewer settings must be an object.");
        if (data.DocumentKind != "particle-life-viewer-settings" || data.SchemaVersion != 1)
        {
            throw new NotSupportedException("Unsupported viewer companion kind or schema version.");
        }

        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        if (!string.Equals(hash, data.ScientificSha256, StringComparison.Ordinal))
        {
            throw new ArgumentException("Viewer settings belong to different scientific file contents; move or remove the mismatched .viewer.json companion.");
        }

        PresetDefinition.ValidatePopulations(data.Populations, speciesCount);
        return new ViewerSettings(data.Populations, data.Generation.ToSettings(), data.Appearance.ToSettings(), data.Playback.ToSettings());
    }

    private sealed record ViewerSettings(int[] Populations, GenerationSettings Generation, AppearanceSettings Appearance, PlaybackSettings Playback);

    private sealed record ViewerSettingsData(string DocumentKind, int SchemaVersion, string ScientificSha256, int[] Populations,
        GenerationData Generation, AppearanceData Appearance, PlaybackData Playback);
}
