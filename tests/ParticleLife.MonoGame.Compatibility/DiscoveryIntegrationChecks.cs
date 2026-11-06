using System.Diagnostics;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ParticleLife.Core.Experiments;
using ParticleLife.MonoGame.Editor;
using ParticleLife.Viewer;

/// <summary>Exercises integrated Discoveries through the production inspector and file workers.</summary>
internal static class DiscoveryIntegrationChecks
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "particle-life-discoveries-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Check(directory, new Rectangle(0, 0, 460, 700));
            Check(directory, new Rectangle(14, 165, 392, 121));
            Check(directory, new Rectangle(0, 0, 350, 80));
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine("Discoveries integration: six tabs, embedded file controls, compact keyboard save/load, validation, retained drafts, hidden worker completion and consumed overlay edges passed.");
    }

    private static void Check(string parent, Rectangle bounds)
    {
        string directory = Path.Combine(parent, bounds.Height.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 4, 2, 31), diagnosticEverySteps: 0);
        ExperimentEditor editor = new(playback);
        using EditorSession session = new(editor);
        List<string> feedback = [];
        using LiveExperimentEditor surface = new(session, directory, feedback.Add, () => { });
        UiPanel panel = Field<UiPanel>(surface, "panel");
        ExperimentFileBrowser browser = Field<ExperimentFileBrowser>(surface, "browser");
        Tick();
        UiButton[] tabs = panel.Toolbar.Cast<UiRow>().SelectMany(row => row.Children).Cast<UiButton>().ToArray();
        Require(tabs.Length == 6 && tabs.Select(Label).SequenceEqual(new[] { "Particles", "Interactions", "World", "Playback", "Appearance", "Discoveries" }),
            "The actual inspector did not retain precisely the six requested ordered tabs.");
        Require(!Widgets(panel.Content).OfType<UiSection>().Any(section => Captured<string>(section) == "Canvas brushes"),
            "The old Canvas brushes section remained in the inspector.");

        editor.SetAppearance(editor.Appearance with { ShowEditor = false });
        surface.OpenDiscoveries(); Tick();
        Require(editor.Appearance.ShowEditor && Field<int>(surface, "selectedTab") == 5
            && ReferenceEquals(Field<UiPanel>(browser, "panel"), panel) && Widgets(panel.Content).Contains(browser.Root),
            "Opening Discoveries did not reveal the real inline library in the inspector.");
        Require(!playback.IsPaused, "Selecting Discoveries unexpectedly paused playback.");
        UiSection save = Field<UiSection>(browser, "save");
        Activate(save);
        UiTextField name = TextField(save, "New file name");
        Focus(name); Draft("kept-discovery"); Key(Keys.Enter);
        Activate(tabs[0]); Activate(tabs[5]);
        Require(TextField(save, "New file name").Draft == "kept-discovery", "Page switching replaced the save-name draft.");
        Focus(TextField(save, "New file name")); Draft("invalid/name");
        surface.OpenDiscoveries();
        Require(panel.Capturing && TextField(save, "New file name").Draft == "invalid/name",
            "Revealing Discoveries discarded an invalid focused draft.");
        ActivateBlocked(tabs[0]);
        Require(Field<int>(surface, "selectedTab") == 5 && !Directory.EnumerateFiles(directory, "*.json").Any(),
            "An invalid file-name draft allowed a page switch or file action.");
        Key(Keys.Escape);

        UiButton savePattern = Widgets(save.Children).OfType<UiButton>().Single(button => Label(button) == "Save pattern");
        Activate(savePattern);
        // A higher overlay can own input without stranding the library worker. Its dismissing
        // press must also not become a fresh press on an underlying inspector control.
        surface.ToggleVisibility();
        long started = Stopwatch.GetTimestamp();
        while (Field<bool>(browser, "fileOperationActive") && Stopwatch.GetElapsedTime(started).TotalSeconds < 10)
        {
            Tick(allowInput: false);
            Thread.Sleep(1);
        }
        Require(Directory.EnumerateFiles(directory, "*.experiment.json").Count() == 1
            && Directory.EnumerateFiles(directory, "*.viewer.json").Count() == 1
            && Directory.EnumerateFiles(directory, "*.discovery.json").Count() == 1,
            "A hidden inspector stranded its asynchronous scientific/companion/metadata save. Files: " + string.Join(", ", Directory.EnumerateFiles(directory).Select(Path.GetFileName))
            + ". Feedback: " + string.Join(" | ", feedback));
        surface.OpenDiscoveries(); Tick();
        UiSection detail = Field<UiSection>(browser, "detail");
        UiTextField displayName = TextField(detail, "Display name");
        Focus(displayName); Draft("Unpublished display draft"); Key(Keys.Enter);
        Activate(tabs[0]); Activate(tabs[5]);
        Require(TextField(detail, "Display name").Draft == "Unpublished display draft", "Page switching lost a metadata draft.");

        UiButton load = Widgets(detail.Children).OfType<UiButton>().Single(button => Label(button) == "Load state");
        Activate(load);
        Activate(tabs[0]);
        started = Stopwatch.GetTimestamp();
        while (Field<bool>(browser, "fileOperationActive") && Stopwatch.GetElapsedTime(started).TotalSeconds < 10)
        { Tick(); Thread.Sleep(1); }
        Require(playback.IsPaused && !Field<bool>(browser, "fileOperationActive") && feedback.Any(text => text.StartsWith("Loaded paused", StringComparison.Ordinal)),
            "Changing tabs stranded explicit loading or changed its paused completion policy.");

        Focus(tabs[5]);
        Point point = tabs[5].Bounds.Center;
        Tick(mouse: MouseAt(point));
        Tick(mouse: MouseAt(point, pressed: true), allowInput: false);
        Tick(mouse: MouseAt(point, pressed: true));
        Require(Field<int>(surface, "selectedTab") == 0, "A consumed overlay dismissal press activated an underlying tab.");
        Tick(mouse: MouseAt(point)); Tick(mouse: MouseAt(point, pressed: true)); Tick(mouse: MouseAt(point));
        Require(Field<int>(surface, "selectedTab") == 5, "A fresh press after overlay dismissal failed to activate the tab.");

        void Tick(IReadOnlyList<UiInputEvent>? input = null, MouseState? mouse = null, bool allowInput = true) =>
            surface.Update(bounds, 1, mouse ?? MouseAt(new Point(-20, -20)), input ?? [], allowInput: allowInput);
        void Key(Keys key, bool control = false) => Tick([UiInputEvent.KeyPress(key, control, false)]);
        void Focus(UiWidget target)
        {
            for (int count = 0; !target.Focused && count < 1024; count++) { Key(Keys.Tab); }
            Require(target.Focused && panel.Body.Contains(target.Bounds) || target.Focused && bounds.Height >= 360,
                "Keyboard focus could not reach a visible integrated discovery control.");
        }
        void Activate(UiWidget target) { Focus(target); Key(Keys.Enter); }
        void ActivateBlocked(UiWidget target)
        {
            Point targetPoint = target.Bounds.Center;
            Tick(mouse: MouseAt(targetPoint)); Tick(mouse: MouseAt(targetPoint, pressed: true)); Tick(mouse: MouseAt(targetPoint));
        }
        void Draft(string value)
        {
            Key(Keys.A, control: true);
            foreach (char character in value) { Tick([UiInputEvent.Text(character, false, false)]); }
        }
    }

    private static UiTextField TextField(UiSection section, string label) => section.Children.OfType<UiTextField>()
        .Single(field => Captured<string>(field) == label);
    private static string Label(UiButton button) => Captured<Func<string>>(button)();
    private static T Captured<T>(object owner) => (T)(owner.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
        .First(field => field.FieldType == typeof(T)).GetValue(owner) ?? throw new InvalidOperationException("Missing captured widget value."));
    private static T Field<T>(object owner, string name) => (T)(owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
        ?? throw new InvalidOperationException("Missing retained field: " + name));
    private static IEnumerable<UiWidget> Widgets(IEnumerable<UiWidget> source)
    {
        foreach (UiWidget widget in source)
        {
            yield return widget;
            foreach (UiWidget child in widget is UiSection section ? Widgets(section.Children) : widget is UiRow row ? row.Children : []) { yield return child; }
        }
    }
    private static MouseState MouseAt(Point point, bool pressed = false) => new(point.X, point.Y, 0,
        pressed ? ButtonState.Pressed : ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
