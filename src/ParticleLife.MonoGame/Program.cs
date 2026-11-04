using System.Globalization;
using ParticleLife.Core.Experiments;
using ParticleLife.MonoGame;

try
{
    if (args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
    {
        ViewerOptions.PrintHelp();
        return 0;
    }

    ViewerOptions options = ViewerOptions.Parse(args);
    ExperimentDefinition? experiment = null;
    string message = "SPACE pause | H controls | S save experiment";
    bool paused = options.Paused;
    if (options.InputPath is not null)
    {
        try
        {
            experiment = ExperimentFile.Load(options.InputPath);
            message = $"Loaded {options.InputPath}";
        }
        catch (Exception error) when (ViewerErrors.IsExpected(error))
        {
            message = $"Load failed: {error.Message} Default experiment retained; paused.";
            Console.Error.WriteLine(message);
            paused = true;
        }
    }

    // A loaded experiment supersedes generation options, including their allocation cost.
    experiment ??= PresetGenerator.Create(options.Preset, options.Particles, options.Species, options.Seed);

    // Formatting is presentation-only. Experiment JSON keeps full round-trip precision.
    CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    using ParticleLifeGame game = new(experiment, options, paused, message);
    game.Run();
    return 0;
}
catch (Exception error) when (ViewerErrors.IsExpected(error))
{
    Console.Error.WriteLine($"Viewer error: {error.Message}");
    Console.Error.WriteLine("Use --help for launch options. DesktopGL requires a working native OpenGL display.");
    return 1;
}
