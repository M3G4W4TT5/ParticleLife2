using System.Globalization;
using System.Text.Json;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;

namespace ParticleLife.MonoGame;

/// <summary>Parses presentation startup options before any native graphics resource is created.</summary>
internal sealed record ViewerOptions(PresetKind Preset, int Particles, int Species, int Seed,
    bool Paused, int SampleEvery, string SaveDirectory, string? InputPath, ForceBackend? BackendOverride, int? WorkerOverride = null,
    int? SampleEveryOverride = null)
{
    internal static ViewerOptions Parse(string[] args)
    {
        PresetKind preset = PresetKind.Random;
        int particles = 500, species = 7, seed = 1, sampleEvery = 144;
        bool paused = false;
        string saveDirectory = "artifacts/viewer";
        string? input = null;
        ForceBackend? backendOverride = null;
        int? workerOverride = null;
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal))
            {
                if (input is not null)
                {
                    throw new ArgumentException("Supply only one startup experiment path.");
                }

                input = Path.GetFullPath(key);
                continue;
            }

            if (!seen.Add(key))
            {
                throw new ArgumentException($"Duplicate option {key}.");
            }

            if (key == "--paused")
            {
                paused = true;
                continue;
            }

            if (++i == args.Length)
            {
                throw new ArgumentException($"Missing value for {key}.");
            }

            string value = args[i];
            switch (key)
            {
                case "--preset":
                    preset = value switch
                    {
                        "random" => PresetKind.Random,
                        "chains" => PresetKind.Chains,
                        "snakes" => PresetKind.Snakes,
                        _ => throw new ArgumentException("Preset must be random, chains or snakes.")
                    };
                    break;
                case "--particles": particles = Integer(value, key); break;
                case "--species": species = Integer(value, key); break;
                case "--seed": seed = Integer(value, key); break;
                case "--sample-every": sampleEvery = Integer(value, key); break;
                case "--save-dir": saveDirectory = value; break;
                case "--workers": workerOverride = Integer(value, key); break;
                case "--backend":
                    backendOverride = value switch
                    {
                        "direct" => ForceBackend.Direct,
                        "grid" => ForceBackend.UniformGrid,
                        _ => throw new ArgumentException("Backend must be direct or grid.")
                    };
                    break;
                default: throw new ArgumentException($"Unknown option {key}.");
            }
        }

        ArgumentOutOfRangeException.ThrowIfNegative(particles);
        ArgumentOutOfRangeException.ThrowIfLessThan(species, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(sampleEvery);
        if (workerOverride is int workers)
        {
            ForceWorkerConfiguration.Validate(backendOverride ?? ForceBackend.UniformGrid, workers);
        }
        return new ViewerOptions(preset, particles, species, seed, paused, sampleEvery,
            Path.GetFullPath(saveDirectory), input, backendOverride, workerOverride,
            seen.Contains("--sample-every") ? sampleEvery : null);
    }

    /// <summary>Generates fallback/default values only when no saved experiment was loaded.</summary>
    internal ExperimentDefinition CreateDefaultExperiment() =>
        PresetGenerator.Create(Preset, Particles, Species, Seed, backend: BackendOverride ?? ForceBackend.UniformGrid,
            workerCount: WorkerOverride ?? 1);

    private static int Integer(string value, string option) => int.TryParse(value, NumberStyles.Integer,
        CultureInfo.InvariantCulture, out int parsed) ? parsed : throw new ArgumentException($"{option} requires an integer.");

    internal static void PrintHelp() => Console.WriteLine("""
        Particle Life 2 - native live experiment editor (MonoGame DesktopGL)

        dotnet run --project src/ParticleLife.MonoGame -c Release -- [experiment.json]
            [--preset random|chains|snakes] [--particles 500] [--species 7] [--seed 1]
            [--paused] [--sample-every 144] [--save-dir artifacts/viewer] [--backend direct|grid] [--workers 1]

        The editor opens beside the simulation. Expand a section and scroll to its controls.
        Click a numeric value, type, then Enter to apply; Esc cancels a draft. Tab navigates.
        Drag sliders for live edits; one drag is one Undo operation. R beside a slider resets
        that value. Invalid/partial text never changes the simulation. Hover for explanations.

        Space     Pause/resume                 N / Right   One step, then pause
        R         Reset defined baseline       I           Explicit live particle resampling
        M         Randomize matrix, new seed   1 / 2 / 3   Random / chains / snakes matrix live
        S / O     Open save/load file library  E           Hide/show editor
        Ctrl+Z/Y  Undo/redo editor changes      F5          Reload startup file or open library
        D         Toggle sampled diagnostics   H / F1      Toggle keyboard hints
        Esc       Cancel field/modal or quit   F9          Print separated viewer timings

        Shortcuts do not fire while a control has keyboard focus. Click the world to return
        focus to shortcuts. Focus loss pauses; resume explicitly. New experiments are explicit
        actions under Generation recipe. Matrix templates retain particles, time and steps.
        Ordinary rule/timestep/backend changes also retain completed state and accumulated time.
        World drafts apply together; population drafts explain survivor/remapping/addition policy.
        Generation seed, placement and initial velocity edits alone do not move particles.

        FILES opens the in-app local browser. Enter a folder/name; save preset, save from here,
        save initial or save session. Existing names are protected by exclusive creation.
        Select a JSON file, then Load experiment, Preset live, Preset new or Replay session.
        Preset live retains compatible populations; Preset new explicitly generates a baseline.
        Save from here stores exact completed state at a new zero-time origin with source metadata.
        Save initial exports the reset baseline. Sessions contain actual boundary changes and
        replay through the shared engine to the recorded terminal step, paused. Esc cancels replay.

        Loaded startup experiments take precedence over generation options. Invalid files retain
        a valid paused fallback. Reports are not inputs. Saved solver choices are honoured unless
        an explicit startup/reload --backend or --workers override is supplied. Direct needs one
        worker; grid accepts 1..8 and falls back to direct when required by numerical range.
        Matching .viewer.json companions restore appearance, generation and playback choices at
        startup and on F5 reload. Only an explicit --sample-every overrides their saved cadence.
        World display uses one isotropic scale. Appearance does not affect trajectories.
        Units are reduced model units; interactions are phenomenological, not molecular validation.
        Numerical dt is separate from wall-clock playback speed. Catch-up limits drop excess
        whole-step playback time; dense populations may still make one step expensive.
        """);
}

internal static class ViewerErrors
{
    internal static bool IsExpected(Exception error) => error is ArgumentException or IOException
        or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidOperationException
        or ArithmeticException or TypeInitializationException or DllNotFoundException;
}
