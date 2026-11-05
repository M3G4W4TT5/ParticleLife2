using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ParticleLife.Core.Experiments;

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
        if (kind == DiscoveryKind.Preset)
        {
            PresetDefinition preset = CapturePreset(name);
            DiscoveryMetadata preview = DiscoveryLibrary.PreviewPreset(preset, description, tags);
            return new PreparedDiscoverySave(() => (PresetFile.Serialize(preset), null), preview);
        }
        ExperimentDefinition experiment;
        Func<string> scientific;
        ViewerSettings settings;
        if (kind == DiscoveryKind.Pattern)
        {
            experiment = CaptureCurrentExperiment(name);
            scientific = () => ExperimentFile.Serialize(experiment);
            settings = CaptureViewerSettings(populations);
        }
        else if (kind is DiscoveryKind.Initial or DiscoveryKind.Session)
        {
            ExperimentDefinition initial = Playback.Experiment;
            experiment = new(name, initial.Parameters, initial.Matrix, initial.InitialState, initial.TimeStep,
                initial.Generator, initial.Backend, initial.WorkerCount, initial.Provenance);
            if (kind == DiscoveryKind.Session)
            {
                SessionDefinition session = CaptureSession(name);
                scientific = () => SessionFile.Serialize(session);
                settings = CaptureViewerSettings(populations);
            }
            else
            {
                scientific = () => ExperimentFile.Serialize(experiment);
                int[] counts = new int[experiment.Matrix.SpeciesCount];
                foreach (int species in experiment.InitialState.Species) { counts[species]++; }
                settings = CaptureViewerSettings(counts);
            }
        }
        else { throw new ArgumentOutOfRangeException(nameof(kind)); }
        DiscoveryMetadata metadata = DiscoveryLibrary.PreviewExperiment(experiment, Appearance, kind, description, tags);
        // Only immutable settings/experiment/session values enter the worker closure.
        return new PreparedDiscoverySave(() =>
        {
            string content = scientific() + "\n";
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
            string companion = JsonSerializer.Serialize(new ViewerSettingsData("particle-life-viewer-settings", 1, hash,
                settings.Populations, GenerationData.From(settings.Generation), AppearanceData.From(settings.Appearance),
                PlaybackData.From(settings.Playback)), EditorJson.Options) + "\n";
            return (content, companion);
        }, metadata);
    }

    /// <summary>Reads and validates a complete file on a worker, without accessing or modifying an active editor.</summary>
    /// <param name="path">Existing scientific file.</param><param name="action">Explicit intended action.</param>
    /// <returns>Immutable prepared data for owner-only application.</returns>
    public static PreparedDiscoveryLoad PrepareDiscoveryLoad(string path, DiscoveryLoadAction action)
    {
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
            return new PreparedDiscoveryLoad(owner =>
            {
                owner.EnsureNotReplaying();
                SessionReplayRunner replay = new(session);
                owner.EndGesture(); owner.EndBrushStroke(); owner.pendingReplay = replay; owner.pendingReplaySettings = settings;
                owner.Playback.SetPaused(true); owner.Revision++;
            });
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
}
