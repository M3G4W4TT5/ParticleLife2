using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Execution;

namespace ParticleLife.Viewer;

public sealed partial class ExperimentEditor
{
    /// <summary>Captures immutable file values at the completed owner boundary, without serializing or writing files.</summary>
    /// <param name="kind">Preset, current pattern, initial baseline or recorded session.</param><param name="name">Saved display name.</param>
    /// <param name="description">Searchable library description.</param><param name="tags">Searchable tags copied into the prepared artifact.</param>
    /// <returns>A worker-safe artifact detached from subsequent editor changes.</returns>
    public PreparedDiscoverySave PrepareDiscoverySave(DiscoveryKind kind, string name, string description = "", string[]? tags = null)
    {
        EnsureNotReplaying();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (kind == DiscoveryKind.Preset)
        {
            PresetDefinition preset = CapturePreset(name);
            DiscoveryMetadata preview = DiscoveryLibrary.PreviewPreset(preset, description, tags);
            return new PreparedDiscoverySave(() => (PresetFile.Serialize(preset), null), preview);
        }
        ExperimentDefinition experiment;
        Func<string> scientific;
        ViewerSettings settings;
        DiscoveryMetadata? capturedPreview = null;
        if (kind == DiscoveryKind.Pattern)
        {
            experiment = CaptureCurrentExperiment(name);
            scientific = () => ExperimentFile.Serialize(experiment);
            settings = CaptureViewerSettings(populations);
        }
        else if (kind is DiscoveryKind.Initial or DiscoveryKind.Session or DiscoveryKind.CapturedSession)
        {
            experiment = Playback.Experiment;
            if (kind != DiscoveryKind.Initial && (kind == DiscoveryKind.CapturedSession || HasCapturedExecution))
            {
                FrozenCapturedSession captured = FreezeCapturedSession(name);
                scientific = () => ExecutionCaptureFile.Serialize(captured.Build());
                capturedPreview = DiscoveryLibrary.PreviewCapturedBoundary(name, captured.TerminalState, captured.Configuration,
                    captured.Execution, Appearance, description, tags);
                settings = CaptureViewerSettings(populations);
            }
            else if (kind == DiscoveryKind.Session)
            {
                FrozenActionSession session = FreezeActionSession(name);
                scientific = () => SessionFile.Serialize(session.Build());
                settings = CaptureViewerSettings(populations);
            }
            else
            {
                scientific = () => ExperimentFile.Serialize(new ExperimentDefinition(name, experiment.Parameters, experiment.Matrix,
                    experiment.InitialState, experiment.TimeStep, experiment.Generator, experiment.Backend, experiment.WorkerCount, experiment.Provenance));
                settings = CaptureViewerSettings(populations);
            }
        }
        else { throw new ArgumentOutOfRangeException(nameof(kind)); }
        DiscoveryMetadata metadata = capturedPreview ?? DiscoveryLibrary.PreviewExperiment(experiment, Appearance, kind, description, tags) with { Name = name };
        // Only immutable settings/experiment/session values enter the worker closure.
        return new PreparedDiscoverySave(() =>
        {
            string content = scientific() + "\n";
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
            int[] savedPopulations = settings.Populations;
            if (kind == DiscoveryKind.Initial)
            {
                // Initial-state recipes describe the immutable reset population, not a later live recipe.
                // Count it on this worker along with full experiment validation and serialization.
                savedPopulations = new int[experiment.Matrix.SpeciesCount];
                foreach (int species in experiment.InitialState.Species) { savedPopulations[species]++; }
            }
            string companion = JsonSerializer.Serialize(new ViewerSettingsData("particle-life-viewer-settings", 1, hash,
                savedPopulations, GenerationData.From(settings.Generation), AppearanceData.From(settings.Appearance),
                PlaybackData.From(settings.Playback)), EditorJson.Options) + "\n";
            return (content, companion);
        }, metadata);
    }

    /// <summary>Reads and validates a complete file on a worker, without accessing or modifying an active editor.</summary>
    /// <param name="path">Existing scientific file.</param><param name="action">Explicit intended action.</param>
    /// <returns>Immutable prepared data for owner-only application.</returns>
    public static PreparedDiscoveryLoad PrepareDiscoveryLoad(string path, DiscoveryLoadAction action)
    {
        if (action is DiscoveryLoadAction.RestoreCapturedState or DiscoveryLoadAction.ResimulateCapturedOnCpu)
        {
            string content = File.ReadAllText(path);
            CapturedSessionDefinition captured = ExecutionCaptureFile.Deserialize(content);
            ViewerSettings? settings = LoadViewerSettings(path, content, captured.TerminalCapture.Configuration.Matrix.SpeciesCount);
            if (action == DiscoveryLoadAction.RestoreCapturedState)
            {
                PreparedCapturedRestore prepared = PrepareCapturedRestore(captured);
                ExperimentDefinition initial = captured.Actions.InitialExperiment;
                return new PreparedDiscoveryLoad(owner =>
                {
                    owner.ApplyCapturedRestore(prepared);
                    owner.PublishLoadedSettings(initial, settings, null);
                }, notice: "Restored stored terminal values, clock and history on CPU. Future CPU motion may differ; GPU continuation is a separate explicit transfer.");
            }
            return PrepareActionReplay(captured.Actions, settings,
                "CPU action re-simulation selected. This does not restore the stored terminal pattern; its resulting trajectory may differ from the recorded execution.");
        }
        if (action == DiscoveryLoadAction.LoadState)
        {
            (ExperimentDefinition experiment, ViewerSettings? settings) = PrepareExperiment(path, null, null, null);
            return new PreparedDiscoveryLoad(owner =>
            {
                owner.EnsureNotReplaying(); owner.Playback.ReplaceExperiment(experiment); owner.Playback.SetPaused(true);
                owner.ClearTimeline(); owner.PublishLoadedSettings(experiment, settings, null);
            });
        }
        if (action == DiscoveryLoadAction.ReplayActions)
        {
            string content = File.ReadAllText(path);
            SessionDefinition session = SessionFile.Deserialize(content);
            int species = session.Events.Count == 0 ? session.InitialExperiment.Matrix.SpeciesCount : session.Events[^1].Configuration.Matrix.SpeciesCount;
            ViewerSettings? settings = LoadViewerSettings(path, content, species);
            return PrepareActionReplay(session, settings, null);
        }
        if (action is not (DiscoveryLoadAction.PresetLive or DiscoveryLoadAction.PresetNew or DiscoveryLoadAction.ReviewPreset))
        { throw new ArgumentOutOfRangeException(nameof(action)); }
        PresetDefinition preset = PresetFile.Load(path);
        if (action == DiscoveryLoadAction.ReviewPreset) { return new PreparedDiscoveryLoad(_ => { }, preset); }
        if (action == DiscoveryLoadAction.PresetLive) { return new PreparedDiscoveryLoad(owner => owner.ApplyPreset(preset, false), preset); }
        ExperimentDefinition generated = GenerateExperiment(preset.Name, preset.Configuration, preset.Populations, preset.Generation);
        return new PreparedDiscoveryLoad(owner =>
        {
            owner.EnsureNotReplaying(); owner.Playback.ReplaceExperiment(generated);
            owner.Generation = preset.Generation; owner.Appearance = preset.Appearance;
            owner.ApplyPlayback(preset.Playback); owner.Playback.SetPaused(true); owner.ClearTimeline();
        }, preset);
    }

    private static PreparedDiscoveryLoad PrepareActionReplay(SessionDefinition session, ViewerSettings? settings, string? notice) => new(owner =>
    {
        owner.EnsureNotReplaying();
        SessionReplayRunner replay = new(session);
        owner.EndGesture(); owner.EndBrushStroke(); owner.pendingReplay = replay; owner.pendingReplaySettings = settings;
        owner.Playback.SetPaused(true); owner.Revision++;
    }, notice: notice);

    /// <summary>Freezes event references and the clock without membership validation or checkpoint work on the owner.</summary>
    private FrozenActionSession FreezeActionSession(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new(name, Playback.Experiment, events.ToArray(), Playback.Boundary.Step);
    }

    private sealed record FrozenActionSession(string Name, ExperimentDefinition Initial, SessionEvent[] Events, long TerminalStep)
    {
        internal SessionDefinition Build()
        {
            ExperimentDefinition named = new(Name, Initial.Parameters, Initial.Matrix, Initial.InitialState, Initial.TimeStep,
                Initial.Generator, Initial.Backend, Initial.WorkerCount, Initial.Provenance);
            return new(named, Events, TerminalStep);
        }
    }
}
