using System.Globalization;
using ParticleLife.MonoGame;

try
{
    if (args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
    {
        ViewerOptions.PrintHelp();
        return 0;
    }

    ViewerOptions options = ViewerOptions.Parse(args);
    ViewerStartup startup = ViewerStartup.Create(options);
    if (startup.Message.StartsWith("Load failed:", StringComparison.Ordinal))
    {
        Console.Error.WriteLine(startup.Message);
    }

    // Formatting is presentation-only. Experiment JSON keeps full round-trip precision.
    CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    ParticleLifeGame game;
    try
    {
        game = new ParticleLifeGame(startup.Editor, options, startup.Message);
    }
    catch
    {
        startup.Editor.Playback.Dispose();
        throw;
    }
    using (game)
    {
        game.Run();
    }
    return 0;
}
catch (Exception error) when (ViewerErrors.IsExpected(error))
{
    Console.Error.WriteLine($"Viewer error: {error.Message}");
    Console.Error.WriteLine("Use --help for launch options. DesktopGL requires a working native OpenGL display.");
    return 1;
}
