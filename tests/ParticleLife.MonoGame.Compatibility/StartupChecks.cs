using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;
using ParticleLife.MonoGame;
using ParticleLife.MonoGame.Editor;
using ParticleLife.Viewer;

/// <summary>Exercises real startup preparation and shared shortcut operations without native graphics.</summary>
internal static class StartupChecks
{
    internal static void Run()
    {
        string directory = Path.GetFullPath(Path.Combine("artifacts", "startup-check", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "saved.experiment.json");
        using PlaybackController source = new(PresetGenerator.Create(PresetKind.Random, 4, 2, 31,
            backend: ForceBackend.UniformGrid, workerCount: 4));
        ExperimentEditor writer = new(source);
        writer.SetGeneration(writer.Generation with { Seed = 98, Placement = PlacementKind.Disk, Speed = 0.4 });
        writer.SetPopulationRecipe([3, 7]);
        writer.SetAppearance(writer.Appearance.WithSpeciesColor(1, 0x123456) with { UiScale = 1.25, ParticleRadius = 4 });
        writer.SetPlayback(new PlaybackSettings { Speed = 2, MaxStepsPerFrame = 3, DiagnosticEverySteps = 17, DiagnosticRadius = 0.03 });
        writer.SaveCurrent(path, "Startup fixture");

        ViewerStartup startup = ViewerStartup.Create(ViewerOptions.Parse([path, "--species", "50000"]));
        using (startup.Editor.Playback)
        {
            ExperimentEditor loaded = startup.Editor;
            Require(loaded.Playback.Observe().Count == 4 && loaded.Configuration.Matrix.SpeciesCount == 2,
                "Valid startup loading generated an unused population or replaced saved dimensions.");
            Require(loaded.Generation == writer.Generation && loaded.Populations.SequenceEqual(writer.Populations),
                "Startup did not restore the saved generation recipe and future population targets.");
            Require(loaded.Appearance.UiScale == 1.25 && loaded.Appearance.ParticleRadius == 4
                && loaded.Appearance.ColorForSpecies(1) == 0x123456, "Startup ignored saved appearance.");
            Require(PlaybackSettings.Capture(loaded.Playback) == PlaybackSettings.Capture(source),
                "Default startup options overwrote saved playback settings.");
            Require(!loaded.Playback.IsPaused && loaded.Playback.Observe().Step == 0 && !loaded.CanUndo,
                "Default startup changed pause semantics, advanced the state or added setup undo entries.");
            Require(loaded.Configuration.Backend == ForceBackend.UniformGrid && loaded.Configuration.WorkerCount == 4,
                "Startup ignored saved execution settings.");

            loaded.Playback.SingleStep();
            long step = loaded.Playback.Observe().Step;
            bool invalidRejected = false;
            try
            {
                loaded.LoadExperiment(path, diagnosticEveryStepsOverride: -1);
            }
            catch (ArgumentOutOfRangeException)
            {
                invalidRejected = true;
            }
            Require(invalidRejected && loaded.Playback.Observe().Step == step && loaded.Playback.DiagnosticEverySteps == 17,
                "A negative cadence override changed the active experiment before validation.");

            LiveExperimentEditor surface = new(loaded, directory, _ => { }, () => { });
            surface.ReloadExperiment(path, ForceBackend.Direct, null, 5);
            Require(loaded.Playback.IsPaused && loaded.Playback.Observe().Step == 0
                && loaded.Playback.DiagnosticEverySteps == 5 && loaded.Playback.Experiment.Backend == ForceBackend.Direct,
                "F5 preparation did not honour explicit overrides or establish the effective paused baseline.");
            loaded.Reset();
            Require(loaded.Configuration.Backend == ForceBackend.Direct && loaded.Configuration.WorkerCount == 1,
                "Reset lost the startup/reload execution override.");
        }

        ViewerStartup overridden = ViewerStartup.Create(ViewerOptions.Parse(
            [path, "--backend", "direct", "--sample-every", "0", "--paused"]));
        using (overridden.Editor.Playback)
        {
            Require(overridden.Editor.Playback.IsPaused && overridden.Editor.Playback.DiagnosticEverySteps == 0
                && overridden.Editor.Configuration.Backend == ForceBackend.Direct && overridden.Editor.Configuration.WorkerCount == 1,
                "Explicit startup execution, pause or zero-cadence overrides were not honoured.");
            Require(overridden.Editor.Playback.PlaybackSpeed == 2 && overridden.Editor.Generation.Seed == 98,
                "Explicit startup overrides discarded unrelated companion settings.");
        }

        string malformed = Path.Combine(directory, "malformed.experiment.json");
        File.Copy(path, malformed);
        File.WriteAllText(malformed + ".viewer.json", "{}");
        ViewerStartup fallback = ViewerStartup.Create(ViewerOptions.Parse(
            [malformed, "--particles", "2", "--species", "2", "--sample-every", "5"]));
        using (fallback.Editor.Playback)
        {
            Require(fallback.Message.StartsWith("Load failed:", StringComparison.Ordinal) && fallback.Editor.Playback.IsPaused
                && fallback.Editor.Playback.Observe().Count == 2 && fallback.Editor.Playback.DiagnosticEverySteps == 5,
                "A malformed companion was ignored instead of producing a valid paused fallback.");
        }

        VerifyDiagnosticHistory(directory);
        Console.WriteLine("Startup and shortcut integration: saved companions, explicit overrides, atomic validation, fallback and diagnostic undo passed without graphics.");
    }

    private static void VerifyDiagnosticHistory(string directory)
    {
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 4, 2, 31));
        ExperimentEditor editor = new(playback);
        LiveExperimentEditor surface = new(editor, directory, _ => { }, () => { });
        SimulationConfiguration initial = editor.Configuration;
        editor.ApplyConfiguration(new SimulationConfiguration(initial.Parameters with { ForceScale = 2 }, initial.Matrix,
            initial.TimeStep, initial.Backend, initial.WorkerCount));
        surface.ToggleDiagnostics();
        Require(playback.DiagnosticEverySteps == 0, "Diagnostic shortcut did not disable measurements.");
        editor.Undo();
        Require(playback.DiagnosticEverySteps == 144 && editor.Configuration.Parameters.ForceScale == 2,
            "Undo after the diagnostic shortcut skipped it and reverted a prior scientific edit.");
        editor.Redo();
        Require(playback.DiagnosticEverySteps == 0 && editor.Configuration.Parameters.ForceScale == 2,
            "Diagnostic shortcut redo changed the scientific configuration.");
        editor.Undo();
        editor.Undo();
        Require(editor.Configuration.Parameters.ForceScale == initial.Parameters.ForceScale,
            "Scientific undo was lost after reversing the diagnostic shortcut.");
        surface.ToggleDiagnostics();
        Require(!editor.CanRedo, "A diagnostic shortcut left the obsolete redo branch available.");
        editor.SetPlayback(PlaybackSettings.Capture(playback) with { DiagnosticEverySteps = 23 });
        surface.ToggleDiagnostics();
        surface.ToggleDiagnostics();
        Require(playback.DiagnosticEverySteps == 23, "Diagnostic shortcut restored the startup cadence instead of the last enabled cadence.");
        surface.SetDiagnosticCadence(17);
        surface.SetDiagnosticCadence(0);
        bool invalidRejected = false;
        try
        {
            surface.SetDiagnosticCadence(-1);
        }
        catch (ArgumentOutOfRangeException)
        {
            invalidRejected = true;
        }
        surface.ToggleDiagnostics();
        Require(invalidRejected && playback.DiagnosticEverySteps == 17,
            "Typed zero or an invalid cadence discarded the last positive cadence used by the shortcut.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
