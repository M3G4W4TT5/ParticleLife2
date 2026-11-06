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
                    preset = PresetCatalog.Parse(value);
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

        PresetInfo recipe = PresetCatalog.Get(preset);
        if (!seen.Contains("--particles"))
        {
            particles = recipe.DefaultParticleCount;
        }
        if (!seen.Contains("--species"))
        {
            species = recipe.DefaultSpeciesCount;
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

    internal static void PrintHelp()
    {
        Console.WriteLine("""
        Particle Life 2 - native live experiment editor (MonoGame DesktopGL)

        dotnet run --project src/ParticleLife.MonoGame -c Release -- [experiment.json]
            [--preset NAME] [--particles N] [--species N] [--seed 1]
            [--paused] [--sample-every 144] [--save-dir artifacts/viewer] [--backend direct|grid] [--workers 1]

        The inspector groups Particles, Interactions, World, Playback, Appearance and Discoveries.
        ? / H / F1 opens a quickstart, file choices and keyboard help without changing the scene.
        Click a numeric value, type, then Enter to apply; Esc cancels a draft. Tab navigates.
        Drag labels/sliders for live edits; one drag is one Undo operation. The reset arrow resets
        that value. Invalid/partial text never changes the simulation. Hover for explanations.

        Space     Pause/resume                 N / Right   One step, then pause
        R         Reset defined baseline       I           Explicit live particle resampling
        M         Randomize matrix, new seed   1 / 2 / 3   Random / chains / snakes matrix live
        S / O     Reveal Discoveries library   E           Hide/show editor
        Ctrl+Z/Y  Undo/redo editor changes      F5          Reload startup file or open library
        D         Toggle sampled diagnostics   H / F1      Open/close Help
        Esc       Cancel field/modal or quit   F9 / F10    Print / reset viewer timing window
        V         Fit world to canvas          Ctrl+0      Restore 1x UI scale (canvas focus)
        B / middle click Toggle Brush tools    Right drag  Pan camera (menu closed)
        Ctrl+plus/minus Adjust UI scale         PageUp/Down Scroll a focused panel or Help

        Shortcuts do not fire while a control has keyboard focus. Click the world to return
        focus to shortcuts. Focus loss pauses; resume explicitly. BUILT-IN RECIPES selects a
        named configuration. NEW FROM RECIPE creates a new population and reset baseline using
        its recommended settings. APPLY MATRIX LIVE requires compatible species counts and keeps
        particles, time and steps. Generation recipe also supports custom new experiments.
        Ordinary rule/timestep/search changes retain completed state and accumulated time.
        Playback offers explicit CPU / GPU execution. GPU uses supported OpenGL compute hardware
        and float32 particle values; switching records an explicit transfer and any value conversion.
        CPU uses binary64 and retains independent direct/grid search and manual worker selection.
        World drafts apply together; population drafts explain survivor/remapping/addition policy.
        Generation seed, placement and initial velocity edits alone do not move particles.
        Appearance offers manual Economy, Balanced and Luminous display presets. Individual controls
        remain editable; quality presets never change solver, precision, population, rules or timestep.

        Discoveries embeds the local searchable discovery library. Enter a folder/name, then Save preset,
        Save pattern, Save initial or Save session. Existing names are protected by exclusive creation.
        Select an item, then Load state, Preset live, Preset new or Replay actions as appropriate.
        Preset live retains compatible populations; Preset new explicitly generates a baseline.
        Save pattern stores exact completed state at a new zero-time origin with source metadata.
        Save initial exports the reset baseline. CPU action sessions replay actual boundary changes
        to the recorded terminal step, paused. Esc cancels replay. After GPU execution, Save session
        includes actual terminal/checkpoint captures: Restore capture restores stored values on CPU;
        Re-simulate CPU repeats actions and can produce a different trajectory. Exact restoration
        does not imply identical future motion across hardware. Export .plshare creates a local file.
        Appearance also contains trails, glow, velocity shading, sampled heatmaps and region tracking.
        Discoveries offers canvas PNG and short GIF capture with explicit size/rate/cost controls.

        Loaded startup experiments take precedence over generation options. Invalid files retain
        a valid paused fallback. Reports are not inputs. Saved solver choices are honoured unless
        an explicit startup/reload --backend or --workers override is supplied. Direct needs one
        worker; grid accepts 1..64 and falls back to direct when required by numerical range.
        Matching .viewer.json companions restore appearance, generation and playback choices at
        startup and on F5 reload. Only an explicit --sample-every overrides their saved cadence.
        World display uses one isotropic scale. Appearance does not affect trajectories.
        Units are reduced model units; interactions are phenomenological, not molecular validation.
        Numerical dt is separate from wall-clock playback speed. Catch-up limits drop excess
        whole-step playback time; dense populations may still make one step expensive.
        """);
        Console.WriteLine("\nBuilt-in recipes (omitted --particles/--species use the listed defaults):");
        foreach (PresetInfo recipe in PresetCatalog.All)
        {
            Console.WriteLine($"  {recipe.Key,-20} {recipe.DefaultParticleCount} particles, {recipe.DefaultSpeciesCount} species" +
                (recipe.IsExperimental ? " [experimental]" : string.Empty));
        }
        Console.WriteLine("Core and halo uses 20% core particles (rounded) and 80% halo; other new recipes use balanced populations.");
        Console.WriteLine("Explicit counts are honoured; unsupported species counts are rejected when generating, not when loading saved state.");
    }
}

internal static class ViewerErrors
{
    internal static bool IsExpected(Exception error) => error is ArgumentException or IOException
        or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidOperationException
        or ArithmeticException or TypeInitializationException or DllNotFoundException;
}
