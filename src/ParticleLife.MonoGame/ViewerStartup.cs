using ParticleLife.Core.Experiments;
using ParticleLife.Viewer;

namespace ParticleLife.MonoGame;

/// <summary>Prepares startup state without constructing graphics resources or an unused generated population.</summary>
/// <remarks>The caller owns <see cref="Editor"/>'s playback controller until ownership is transferred
/// to the native game. Saved companions share the in-app load validator; only explicit startup
/// execution/cadence arguments override saved settings. Failed loads use a paused generated fallback.</remarks>
internal sealed record ViewerStartup(ExperimentEditor Editor, string Message)
{
    internal static ViewerStartup Create(ViewerOptions options)
    {
        string message = "Live experiment editor | Expand a section to explore | S opens save/load library";
        bool paused = options.Paused;
        if (options.InputPath is not null)
        {
            try
            {
                ExperimentEditor loaded = ExperimentEditor.CreateFromFile(options.InputPath,
                    options.BackendOverride, options.WorkerOverride, options.SampleEveryOverride);
                loaded.Playback.SetPaused(paused);
                return new ViewerStartup(loaded, $"Loaded {options.InputPath}");
            }
            catch (Exception error) when (ViewerErrors.IsExpected(error))
            {
                message = $"Load failed: {error.Message} Default experiment retained; paused.";
                paused = true;
            }
        }

        PresetInfo recipe = PresetCatalog.Get(options.Preset);
        string label = recipe.Name + (recipe.IsExperimental ? " (experimental)" : "");
        message = options.InputPath is null
            ? $"{label} | Built-in recipes offers more starting points | S opens save/load library"
            : message + $" Fallback: {label}.";
        PlaybackController playback = new(options.CreateDefaultExperiment(), diagnosticEverySteps: options.SampleEvery);
        try
        {
            playback.SetPaused(paused);
            return new ViewerStartup(new ExperimentEditor(playback), message);
        }
        catch
        {
            playback.Dispose();
            throw;
        }
    }
}
