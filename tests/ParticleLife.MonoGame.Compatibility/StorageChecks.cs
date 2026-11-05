using System.Diagnostics;
using System.Globalization;
using ParticleLife.Core.Experiments;
using ParticleLife.Viewer;

/// <summary>Exercises the real save protocol in independent processes without graphics.</summary>
internal static class StorageChecks
{
    internal static async Task RunAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "particle-life-process-saves-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (string mode in new[] { "single", "paired" })
            {
                for (int round = 0; round < 4; round++)
                {
                    await RaceAsync(directory, mode, round);
                }
            }
            Console.WriteLine("Cross-process saving: eight six-writer races published exactly one complete document or matching pair; reservations and temporary files were cleaned.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task RaceAsync(string directory, string mode, int round)
    {
        string path = Path.Combine(directory, mode + round + ".json");
        string start = path + ".go";
        List<Process> children = [];
        try
        {
            for (int index = 0; index < 6; index++)
            {
                ProcessStartInfo info = new("dotnet") { UseShellExecute = false };
                info.ArgumentList.Add(typeof(StorageChecks).Assembly.Location);
                foreach (string argument in new[] { "--save-worker", path, mode, index.ToString(CultureInfo.InvariantCulture) })
                {
                    info.ArgumentList.Add(argument);
                }
                children.Add(Process.Start(info) ?? throw new InvalidOperationException("Could not start save worker."));
            }
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            while (Enumerable.Range(0, 6).Any(index => !File.Exists(path + ".ready" + index)))
            {
                if (children.Any(child => child.HasExited))
                {
                    throw new InvalidOperationException("A save worker exited before the start barrier.");
                }
                await Task.Delay(10, timeout.Token);
            }
            File.WriteAllText(start, "start");
            await Task.WhenAll(children.Select(child => child.WaitForExitAsync(timeout.Token)));
            int[] winners = Enumerable.Range(0, 6).Where(index => children[index].ExitCode == 0).ToArray();
            if (winners.Length != 1 || children.Any(child => child.ExitCode is not (0 or 10)))
            {
                throw new InvalidOperationException("Concurrent save processes did not produce exactly one winner.");
            }
            string expected = File.ReadAllText(path + ".expected" + winners[0]);
            if (ExperimentFile.Serialize(ExperimentFile.Load(path)) != expected)
            {
                throw new InvalidOperationException("The published experiment differs from the winning writer.");
            }
            if (mode == "paired")
            {
                using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 4, 2, 1));
                ExperimentEditor editor = new(playback);
                editor.LoadExperiment(path);
                if (editor.Generation.Seed != winners[0] || playback.Experiment.Name != "writer " + winners[0])
                {
                    throw new InvalidOperationException("The experiment and companion came from different processes.");
                }
            }
            if (Directory.GetFiles(directory, "*.save.lock").Length != 0
                || Directory.GetFiles(directory, ".particle-life-*.tmp*").Length != 0)
            {
                throw new InvalidOperationException("Save workers left reservations or temporary files behind.");
            }
        }
        finally
        {
            foreach (Process child in children)
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                }
                child.Dispose();
            }
        }
    }

    /// <summary>Waits at a file barrier, then saves through the production single/pair APIs.</summary>
    internal static int RunWorker(string[] args)
    {
        string path = args[1];
        int index = int.Parse(args[3], CultureInfo.InvariantCulture);
        ExperimentDefinition experiment = PresetGenerator.Create(PresetKind.Random, 4, 2, index);
        using PlaybackController playback = new(experiment);
        ExperimentEditor editor = new(playback);
        editor.SetGeneration(editor.Generation with { Seed = index });
        if (args[2] == "paired")
        {
            experiment = editor.CaptureCurrentExperiment("writer " + index);
        }
        File.WriteAllText(path + ".expected" + index, ExperimentFile.Serialize(experiment));
        File.WriteAllText(path + ".ready" + index, "ready");
        Stopwatch timer = Stopwatch.StartNew();
        while (!File.Exists(path + ".go"))
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(30))
            {
                throw new TimeoutException("Save start barrier timed out.");
            }
            Thread.Sleep(5);
        }
        try
        {
            if (args[2] == "paired")
            {
                editor.SaveCurrent(path, "writer " + index);
            }
            else
            {
                ExperimentStorage.SaveNew(path, experiment);
            }
            return 0;
        }
        catch (IOException)
        {
            return 10;
        }
    }
}
