using System.Diagnostics;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using ParticleLife.MonoGame.Editor;
using ParticleLife.Viewer;

/// <summary>Exercises real native controls and background replay while deliberately withholding UI polls.</summary>
internal static class ReplayCompletionChecks
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "particle-life-replay-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (Outcome outcome in Enum.GetValues<Outcome>()) { Check(directory, outcome); }
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine("Replay UI completion: zero-step publication without intermediate UI polling, retained ordinary/failed/cancelled drafts, and delayed metadata warnings passed without graphics.");
    }

    private static void Check(string directory, Outcome outcome)
    {
        const string metadataWarning = "Controlled recent-file metadata warning.";
        string path = Path.Combine(directory, outcome + ".session.json");
        ExperimentDefinition replayInitial = Fixture("Replay " + outcome, 2, 2);
        SessionEvent[] changes = [];
        if (outcome == Outcome.Failed)
        {
            // The file is valid structurally, but its actual replay must reject the inconsistent
            // positive payload clock at step one. This exercises a real replay failure, not a fake flag.
            ParticleSnapshot initial = replayInitial.InitialState;
            ParticleSnapshot updates = new([initial.Ids[0]], [initial.Positions[0]], [initial.Velocities[0]], [initial.Species[0]], 2, 1);
            ParticleStateDelta delta = new([], updates, new ParticleSnapshot([], [], [], [], 2, 1));
            changes = [new SessionEvent(1, new(replayInitial.Parameters, replayInitial.Matrix, replayInitial.TimeStep,
                replayInitial.Backend, replayInitial.WorkerCount), stateDelta: delta)];
        }
        SessionFile.Save(path, new SessionDefinition(replayInitial, changes, outcome == Outcome.Completed ? 0 : outcome == Outcome.Cancelled ? 100 : 1));
        using PlaybackController playback = new(Fixture("Original", 4, 1), diagnosticEverySteps: 0);
        playback.SetPaused(true);
        using EditorSession session = new(new ExperimentEditor(playback), runInBackground: true);
        using ManualResetEventSlim ownerEntered = new(), releaseOwner = new(), replayEntered = new(), releaseReplay = new();
        using ManualResetEventSlim metadataEntered = new(), releaseMetadata = new();
        List<string> messages = [];
        using LiveExperimentEditor surface = new(session, directory, messages.Add, () => { }, _ =>
        {
            metadataEntered.Set();
            Require(releaseMetadata.Wait(TimeSpan.FromSeconds(10)), "Metadata barrier timed out.");
            return metadataWarning;
        });
        ExperimentFileBrowser browser = Field<ExperimentFileBrowser>(surface, "browser");
        Task? heldOwner = null, heldReplay = null;
        try
        {
            Tick(surface);
            UiSection world = Field<UiSection>(surface, "world");
            UiSection population = Field<UiSection>(surface, "population");
            UiNumber width = Widgets(world.Children).OfType<UiNumber>().Single(widget => Field<string>(widget, "label") == "World width [L]");
            UiNumber total = Widgets(population.Children).OfType<UiNumber>().Single(widget => Field<string>(widget, "label") == "Total particles");
            Enter(width, "7"); Enter(total, "13");
            Await(session.Run("Ordinary rule edit", owner =>
            {
                SimulationConfiguration current = owner.Configuration;
                owner.ApplyConfiguration(new(current.Parameters with { ForceScale = 0.25 }, current.Matrix,
                    current.TimeStep, current.Backend, current.WorkerCount));
            }, requiresCapture: false));
            Tick(surface);
            Require(width.ClipboardText == "7" && total.ClipboardText == "13", "An ordinary edit discarded unrelated world/population drafts.");
            long originalBaseline = session.Publication.BaselineVersion;
            heldOwner = session.Run("Hold owner before replay", _ =>
            {
                ownerEntered.Set(); Require(releaseOwner.Wait(TimeSpan.FromSeconds(10)), "Owner barrier timed out.");
            }, requiresCapture: false);
            Require(ownerEntered.Wait(TimeSpan.FromSeconds(10)), "Owner did not enter its barrier.");

            // Select the actual file and press the production browser's actual Replay actions button.
            // Reflection only reaches retained private UI objects; no production state flag is written.
            DiscoveryEntry entry = DiscoveryLibrary.Inspect(path) ?? throw new InvalidOperationException("Session was not discoverable.");
            Method(browser, "Select").Invoke(browser, [entry]);
            UiPanel panel = Field<UiPanel>(browser, "panel");
            Widgets(panel.Content).OfType<UiButton>().Single(button => button.Help == "Replay actions").Press(default);
            Await(Field<Task>(browser, "fileTask"));
            browser.Pump(); // Queue parsed replay behind the held owner, without pumping owner callbacks.
            Task replayCommand = Field<Task>(browser, "fileOwnerTask");
            if (outcome == Outcome.Cancelled)
            {
                heldReplay = session.Run("Hold applied replay before advancement", owner =>
                {
                    Require(owner.IsReplaying, "The ordered replay command was not applied.");
                    replayEntered.Set(); Require(releaseReplay.Wait(TimeSpan.FromSeconds(10)), "Replay barrier timed out.");
                }, requiresCapture: false);
            }
            releaseOwner.Set(); Await(heldOwner); Await(replayCommand);
            if (outcome == Outcome.Completed)
            {
                WaitUntil(() => session.Publication.BaselineVersion != originalBaseline, "Zero-step replay did not publish its terminal baseline.");
                Require(!session.IsReplaying, "Zero-step publication left a pending replay.");
                // No surface/editor UI pump has run since the replay command was enqueued.
                Tick(surface);
                Require(width.ClipboardText == "2" && total.ClipboardText == "2", "An entirely missed running interval left old world/population drafts visible.");
            }
            else if (outcome == Outcome.Cancelled)
            {
                Require(replayEntered.Wait(TimeSpan.FromSeconds(10)), "Replay did not enter its ordered hold.");
                Tick(surface);
                Tick(surface, [UiInputEvent.KeyPress(Keys.Escape, false, false)]);
                releaseReplay.Set(); Await(heldReplay!);
                WaitUntil(() => !session.IsReplaying, "Cancellation did not complete.");
                Tick(surface);
            }
            else
            {
                WaitUntil(() => !session.IsReplaying && session.Publication.Error is not null, "The inconsistent replay clock did not fail.");
                Tick(surface);
            }
            Require(metadataEntered.Wait(TimeSpan.FromSeconds(10)), "Recent-file metadata did not enter its independent worker barrier.");
            if (outcome != Outcome.Completed)
            {
                Require(session.Publication.BaselineVersion == originalBaseline && session.Publication.Count == 4
                    && width.ClipboardText == "7" && total.ClipboardText == "13", "Failed/cancelled replay changed the baseline or discarded unrelated drafts.");
            }
            releaseMetadata.Set(); Await(Field<Task>(browser, "fileTask"));
            Tick(surface);
            string expected = outcome == Outcome.Completed ? "Replay completed" : outcome == Outcome.Cancelled ? "Replay cancelled" : "Replay failed";
            string status = Field<string>(browser, "result");
            Require(status.StartsWith(expected, StringComparison.Ordinal) && status.Contains(metadataWarning, StringComparison.Ordinal),
                "Late recent-file work replaced the final replay outcome or lost its metadata warning: " + status);
            Require(messages[^1] == status && !status.Contains("Replay started", StringComparison.Ordinal), "The final reported feedback was stale.");
        }
        finally
        {
            releaseOwner.Set(); releaseReplay.Set(); releaseMetadata.Set();
            if (heldOwner is not null) { Await(heldOwner); }
            if (heldReplay is not null) { Await(heldReplay); }
        }
    }

    private static ExperimentDefinition Fixture(string name, int count, double width)
    {
        ExperimentDefinition generated = PresetGenerator.Create(PresetKind.Random, count, 2, 31);
        return new(name, generated.Parameters with { WorldWidth = width, ForceScale = 0, WallStrength = 0 }, generated.Matrix,
            generated.InitialState, generated.TimeStep, generated.Generator, ForceBackend.UniformGrid, 1);
    }

    private static void Enter(UiNumber widget, string value)
    {
        widget.Focus(true); widget.Key(Keys.A, true, false);
        foreach (char character in value) { widget.Text(character); }
        widget.Key(Keys.Enter, false, false);
        Require(widget.Focus(false), "Valid native draft did not commit.");
    }

    private static IEnumerable<UiWidget> Widgets(IEnumerable<UiWidget> source)
    {
        foreach (UiWidget widget in source)
        {
            yield return widget;
            IEnumerable<UiWidget> children = widget switch { UiSection section => section.Children, UiRow row => row.Children, _ => [] };
            foreach (UiWidget child in Widgets(children)) { yield return child; }
        }
    }

    private static void Tick(LiveExperimentEditor surface, IReadOnlyList<UiInputEvent>? input = null) =>
        surface.Update(new Rectangle(0, 0, 460, 700), new Rectangle(0, 0, 900, 700), 1, default, input ?? []);
    private static T Field<T>(object owner, string name) => (T)(owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
        ?? throw new InvalidOperationException("Missing retained UI field: " + name));
    private static MethodInfo Method(object owner, string name) => owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Missing native UI action: " + name);
    private static void Await(Task task)
    {
        Require(task.Wait(TimeSpan.FromSeconds(10)), "An ordered operation timed out.");
        task.GetAwaiter().GetResult();
    }
    private static void WaitUntil(Func<bool> condition, string failure)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!condition()) { Require(elapsed.Elapsed < TimeSpan.FromSeconds(10), failure); Thread.Sleep(1); }
    }
    private static void Require(bool condition, string failure) { if (!condition) { throw new InvalidOperationException(failure); } }
    private enum Outcome { Completed, Cancelled, Failed }
}
