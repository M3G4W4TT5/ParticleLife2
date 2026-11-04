using System.Globalization;
using System.Text.Json;
using ParticleLife.Core.Experiments;

namespace ParticleLife.MonoGame;

/// <summary>Parses presentation startup options before any native graphics resource is created.</summary>
internal sealed record ViewerOptions(PresetKind Preset, int Particles, int Species, int Seed,
    bool Paused, int SampleEvery, string SaveDirectory, string? InputPath)
{
    internal static ViewerOptions Parse(string[] args)
    {
        PresetKind preset = PresetKind.Random;
        int particles = 500, species = 7, seed = 1, sampleEvery = 144;
        bool paused = false;
        string saveDirectory = "artifacts/viewer";
        string? input = null;
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
                default: throw new ArgumentException($"Unknown option {key}.");
            }
        }

        ArgumentOutOfRangeException.ThrowIfNegative(particles);
        ArgumentOutOfRangeException.ThrowIfLessThan(species, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(sampleEvery);
        return new ViewerOptions(preset, particles, species, seed, paused, sampleEvery,
            Path.GetFullPath(saveDirectory), input);
    }

    private static int Integer(string value, string option) => int.TryParse(value, NumberStyles.Integer,
        CultureInfo.InvariantCulture, out int parsed) ? parsed : throw new ArgumentException($"{option} requires an integer.");

    internal static void PrintHelp() => Console.WriteLine("""
        Particle Life 2 - interactive 2D viewer (MonoGame DesktopGL)

        dotnet run --project src/ParticleLife.MonoGame -c Release -- [experiment.json]
            [--preset random|chains|snakes] [--particles 500] [--species 7] [--seed 1]
            [--paused] [--sample-every 144] [--save-dir artifacts/viewer]

        A startup experiment takes precedence over generation options. Invalid files leave
        the generated default paused with a visible error. Run reports are not inputs.

        Space     Pause/resume                 N / Right   Exactly one step, then pause
        R         Restore saved initial state  I           Resample positions, zero velocity
        M         New random matrix            1 / 2 / 3   Random / chains / snakes preset
        S         Save current initial experiment to a new unique file (never a checkpoint)
        F5        Reload startup file argument (invalid input retains current experiment)
        D         Toggle sampled diagnostics   H / F1      Toggle clickable controls
        Esc       Close help, or quit          F9          Print current timing summary

        Click the playback badge to pause/resume; click HELP for every action.

        I preserves identities, species, matrix, model and timestep. M preserves initial
        state and model. Both restart at t=0. Presets regenerate state and matrix using
        current population, species, model and timestep. New seeds and actual values are saved.
        Focus loss pauses playback. The nominal world is fit isotropically on resize;
        finite off-world particles remain simulated. Units are reduced model units.
        Playback targets one model time unit per wall second, at most eight steps/frame.
        Excess whole-step playback time is discarded and displayed; dt never changes.
        Diagnostics are quadratic and allocate, sampled every N completed steps (0 disables).
        Large direct-solver populations may be slow, including a single uninterruptible step.
        """);
}

internal static class ViewerErrors
{
    internal static bool IsExpected(Exception error) => error is ArgumentException or IOException
        or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidOperationException
        or ArithmeticException or TypeInitializationException or DllNotFoundException;
}
