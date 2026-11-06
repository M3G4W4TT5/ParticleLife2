using System.Diagnostics;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using ParticleLife.MonoGame;
using ParticleLife.MonoGame.Editor;
using ParticleLife.Viewer;

/// <summary>Exercises nested native controls through panel input without creating a graphics device.</summary>
internal static class NestedSectionChecks
{
    private static readonly MouseState Neutral = MouseAt(new Point(-20, -20));

    internal static void Run()
    {
        foreach (Rectangle bounds in new[] { new Rectangle(0, 0, 460, 700),
            new Rectangle(14, 165, 392, 121), new Rectangle(0, 0, 350, 80) })
        {
            CheckEditor(bounds);
        }
        CheckTimingControls(withReset: true);
        CheckTimingControls(withReset: false);
        CheckNavigationDrafts();
        CheckTimingSummary();
        CheckDrawAndCollapse();
        Console.WriteLine("Nested section managed checks: real appearance/tracking/capture/timing controls, compact keyboard scrolling, pointer actions and recursive draw dispatch passed.");
    }

    private static void CheckEditor(Rectangle bounds)
    {
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 4, 2, 31), diagnosticEverySteps: 0);
        ExperimentEditor editor = new(playback);
        using EditorSession session = new(editor);
        List<string> feedback = [];
        using LiveExperimentEditor surface = new(session, Path.GetTempPath(), feedback.Add, () => { });
        UiPanel panel = Field<UiPanel>(surface, "panel");
        UiSection appearance = Field<UiSection>(surface, "appearance");
        UiSection discoveries = Field<UiSection>(surface, "discoveries");
        UiSection effects = SectionContaining(appearance, "Opacity");
        UiSection observations = SectionContaining(appearance, "Tracking radius [L]");
        UiSection captures = SectionContaining(discoveries, "Capture width [px]");
        UiNumber opacity = Number(effects, "Opacity");
        UiNumber captureWidth = Number(captures, "Capture width [px]");
        UiWidget appearancePage = ((UiRow)panel.Toolbar[^1]).Children[1];
        UiWidget discoveriesPage = ((UiRow)panel.Toolbar[^1]).Children[2];
        Tick();
        Activate(appearancePage);
        Require(!effects.Expanded && opacity.Bounds == Rectangle.Empty,
            "A collapsed appearance section exposed its children before expansion.");
        Activate(effects);
        Focus(opacity);
        Require(panel.Body.Contains(opacity.Bounds) && opacity.Bounds.Left > effects.Bounds.Left,
            "Expanded appearance controls did not receive visible indented bounds.");
        EnterNumber("0.4");
        Require(editor.Appearance.Visuals.Opacity == 0.4 && playback.Observe().Step == 0 && editor.SessionEventCount == 0,
            "The nested opacity field was unreachable or changed the scientific timeline.");

        Activate(observations);
        UiNumber radius = Number(observations, "Tracking radius [L]");
        Focus(radius);
        EnterNumber("0.2");
        Require(surface.VisualTools.TrackingRadius == 0.2,
            "The nested tracking radius did not apply its exact keyboard draft.");

        Activate(discoveriesPage);
        Activate(captures);
        Focus(captureWidth);
        Require(panel.Body.Contains(captureWidth.Bounds), "Keyboard navigation did not scroll nested capture controls into view.");
        EnterNumber("640");
        Require(surface.VisualTools.CaptureWidth == 640, "The nested capture width did not apply its exact keyboard draft.");
        UiRow captureActions = captures.Children.OfType<UiRow>().Single();
        UiWidget screenshot = captureActions.Children[0];
        Focus(screenshot);
        Require(panel.Body.Contains(screenshot.Bounds), "The nested screenshot action remained clipped after keyboard focus.");
        Point point = screenshot.Bounds.Center;
        Tick(mouse: MouseAt(point));
        Tick(mouse: MouseAt(point, pressed: true));
        Tick(mouse: MouseAt(point));
        Require(surface.VisualTools.ScreenshotRequested, "The visible nested Save PNG action did not receive its pointer press.");

        // Changing pages must preserve expanded groups and let Shift+Tab return to a nested field.
        Activate(appearancePage);
        Focus(opacity);
        Key(Keys.Tab, shift: true);
        Require(effects.Focused && effects.Expanded && observations.Expanded,
            "Reverse focus navigation or page switching lost a nested section's retained expansion.");
        Require(!feedback.Any(message => message.StartsWith("Edit rejected:", StringComparison.Ordinal)),
            "A nested control reported an unexpected validation failure: " + string.Join(" | ", feedback));

        void Tick(IReadOnlyList<UiInputEvent>? input = null, MouseState? mouse = null) =>
            surface.Update(bounds, 1, mouse ?? Neutral, input ?? []);
        void Key(Keys key, bool control = false, bool shift = false) => Tick([UiInputEvent.KeyPress(key, control, shift)]);
        void Focus(UiWidget target)
        {
            for (int count = 0; !target.Focused && count < 256; count++) { Key(Keys.Tab); }
            Require(target.Focused, $"Tab could not reach {target.GetType().Name} in the actual {bounds.Width} x {bounds.Height} inspector.");
        }
        void Activate(UiWidget target) { Focus(target); Key(Keys.Enter); }
        void EnterNumber(string text)
        {
            Key(Keys.A, control: true);
            foreach (char character in text) { Tick([UiInputEvent.Text(character, false, false)]); }
            Key(Keys.Enter);
        }
    }

    private static void CheckTimingControls(bool withReset)
    {
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 4, 2, 73), diagnosticEverySteps: 0);
        ExperimentEditor editor = new(playback);
        playback.SingleStep();
        ParticleStateView before = playback.Observe();
        ParticleSnapshot saved = new(before.Ids, before.Positions, before.Velocities, before.Species, before.Time, before.Step);
        long revision = editor.Revision;
        SimulationConfiguration configuration = editor.Configuration;
        AppearanceSettings appearance = editor.Appearance;
        PlaybackSettings settings = PlaybackSettings.Capture(playback);
        int prints = 0, resets = 0;
        List<string> feedback = [];
        using EditorSession session = new(editor);
        using LiveExperimentEditor surface = new(session, Path.GetTempPath(), feedback.Add, () => prints++,
            resetTimings: withReset ? () => resets++ : null);
        UiPanel panel = Field<UiPanel>(surface, "panel");
        UiSection simulation = Field<UiSection>(surface, "simulation");
        UiSection advanced = simulation.Children.OfType<UiSection>().Single();
        UiRow timingActions = advanced.Children.OfType<UiRow>().Last();
        UiWidget print = timingActions.Children[0], reset = timingActions.Children[1];
        Rectangle bounds = new(14, 165, 392, 121);
        Tick();
        Focus(((UiRow)panel.Toolbar[^1]).Children[0]);
        Key(Keys.Enter);
        Focus(advanced);
        Key(Keys.Enter);
        Focus(print);
        Require(panel.Body.Contains(print.Bounds), "The visible Print timing control was unreachable in a compact inspector.");
        Key(Keys.Enter);
        Require(prints == 1 && resets == 0, "Print invoked the wrong timing callback or invoked it more than once.");
        Focus(reset);
        Require(panel.Body.Contains(reset.Bounds), "The visible Reset timing control was unreachable in a compact inspector.");
        Point point = reset.Bounds.Center;
        Tick(mouse: MouseAt(point));
        Tick(mouse: MouseAt(point, pressed: true));
        Tick(mouse: MouseAt(point, pressed: true));
        Tick(mouse: MouseAt(point));
        Require(prints == 1 && resets == (withReset ? 1 : 0),
            "Reset did not respect its optional host callback or repeated during a held press.");
        foreach (bool middle in new[] { true, false })
        {
            Focus(reset);
            Point outside = new(bounds.Right + 40, bounds.Center.Y);
            Tick(mouse: MouseAt(outside, middle: middle, right: !middle));
            Require(!surface.CapturesKeyboard && !reset.Focused,
                "A focused timing button blocked the next middle/right canvas navigation press.");
            Tick(mouse: MouseAt(outside, middle: middle, right: !middle));
            Tick(mouse: MouseAt(outside));
            Require(prints == 1 && resets == (withReset ? 1 : 0),
                "A secondary navigation press activated the previously focused timing button.");
        }
        ParticleStateView after = playback.Observe();
        Require(after.Step == saved.Step && after.Time == saved.Time && after.Ids.SequenceEqual(saved.Ids)
            && after.Positions.SequenceEqual(saved.Positions) && after.Velocities.SequenceEqual(saved.Velocities)
            && after.Species.SequenceEqual(saved.Species) && editor.Revision == revision && editor.SessionEventCount == 0
            && ReferenceEquals(editor.Configuration, configuration) && editor.Appearance == appearance
            && PlaybackSettings.Capture(playback) == settings && playback.IsPaused && feedback.Count == 0,
            "Timing controls changed simulation state, settings, history or pause policy.");

        void Tick(IReadOnlyList<UiInputEvent>? input = null, MouseState? mouse = null) =>
            surface.Update(bounds, 1, mouse ?? Neutral, input ?? []);
        void Key(Keys key) => Tick([UiInputEvent.KeyPress(key, false, false)]);
        void Focus(UiWidget target)
        {
            for (int count = 0; !target.Focused && count < 256; count++) { Key(Keys.Tab); }
            Require(target.Focused, "Keyboard traversal could not reach the actual timing controls.");
        }
    }

    private static void CheckNavigationDrafts()
    {
        foreach (bool middle in new[] { true, false })
        {
            double value = 2.5;
            int began = 0, ended = 0;
            List<string> errors = [];
            UiPanel panel = new(errors.Add);
            UiNumber number = new("Radius", () => value, next => value = next, 2.5, 0.5, 12, "",
                beginGesture: _ => began++, endGesture: () => ended++);
            panel.Content.Add(number);
            panel.Arrange(new Rectangle(0, 0, 460, 700));
            Point outside = new(500, 200);
            Draft("4.75");
            Tick(MouseAt(outside));
            Require(number.Focused && value == 2.5, "Pointer hover committed an outgoing numeric draft.");
            PanPress();
            Require(value == 4.75 && !panel.Capturing && !number.Focused,
                "Middle/right navigation did not validate and commit the outgoing numeric draft.");
            Tick(MouseAt(outside));
            Draft("-");
            PanPress();
            Require(value == 4.75 && number.Focused && panel.Capturing && errors.Count == 1,
                "Middle/right navigation abandoned an invalid numeric draft or escaped its focus gate.");
            Tick(MouseAt(outside, middle: middle, right: !middle));
            Require(errors.Count == 1, "Holding a navigation button repeatedly retried an invalid blur.");
            Tick(MouseAt(outside), [UiInputEvent.KeyPress(Keys.Space, false, false)]);
            Require(panel.ConsumedKeyboard && number.Focused, "An invalid navigation handoff leaked keyboard capture.");
            Tick(MouseAt(outside), [UiInputEvent.KeyPress(Keys.Escape, false, false)]);
            Require(value == 4.75 && !panel.Capturing, "Cancelling the retained invalid draft changed its applied value.");

            Point slider = number.SliderBounds.Center;
            Tick(MouseAt(slider));
            Tick(MouseAt(slider, pressed: true));
            Require(began == 1 && ended == 0, "The actual slider did not start one pending gesture.");
            Tick(MouseAt(outside, pressed: true, middle: middle, right: !middle));
            Tick(MouseAt(outside, pressed: true, middle: middle, right: !middle));
            Tick(MouseAt(outside));
            Require(ended == 1 && !panel.Capturing, "Secondary navigation did not end the captured slider gesture exactly once.");

            void Tick(MouseState mouse, IReadOnlyList<UiInputEvent>? input = null) => panel.Update(mouse, 1, input ?? []);
            void PanPress() => Tick(MouseAt(outside, middle: middle, right: !middle));
            void Draft(string text)
            {
                Point point = number.ValueBounds.Center;
                Tick(MouseAt(point)); Tick(MouseAt(point, pressed: true)); Tick(MouseAt(point));
                Tick(MouseAt(point), [UiInputEvent.KeyPress(Keys.A, true, false)]);
                foreach (char character in text) { Tick(MouseAt(point), [UiInputEvent.Text(character, false, false)]); }
            }
        }
    }

    private static void CheckTimingSummary()
    {
        PresentationTimings timings = new();
        long completed = Stopwatch.Frequency;
        timings.RecordPresent(completed, 0);
        for (int milliseconds = 1; milliseconds <= 20; milliseconds++)
        {
            long interval = (long)Math.Round(Stopwatch.Frequency * (milliseconds / 1000d));
            timings.ObserveInput(completed);
            completed += interval;
            timings.RecordPresent(completed, 0);
        }
        string summary = timings.DescribeSummary();
        Require(summary.Split("19.000 ms").Length == 3 && summary.Split("n=20").Length == 3,
            "Visible frame/input summaries did not retain their actual nearest-rank p95 and independent counts.");
        _ = timings.Describe();
        Require(timings.DescribeSummary() == summary, "Printing a full timing report changed the visible sample window.");
        timings.Reset();
        Require(!timings.DescribeSummary().Contains("p95=", StringComparison.Ordinal),
            "Reset timing feedback retained a percentile from the preceding window.");
    }

    private static void CheckDrawAndCollapse()
    {
        UiPanel panel = new(message => throw new InvalidOperationException(message));
        UiSection outer = new("Outer", true), middle = new("Middle", true), inner = new("Inner", true);
        DrawProbe probe = new();
        outer.Children.Add(middle); middle.Children.Add(inner); inner.Children.Add(probe); panel.Content.Add(outer);
        // Exactly one row can be visible, so drawing the nested leaf needs no native canvas.
        panel.Arrange(new Rectangle(0, 0, 350, 75));
        Focus(probe);
        Require(panel.Body.Top == probe.Bounds.Top && panel.Body.Height == probe.Bounds.Height,
            "The nested draw probe was not scrolled into the sole complete body row.");
        panel.DrawBody(null!);
        Require(probe.Draws == 1, "An expanded nested leaf was omitted or duplicated during actual body drawing.");
        Focus(middle);
        panel.Update(Neutral, 1, [UiInputEvent.KeyPress(Keys.Enter, false, false)]);
        Require(!middle.Expanded && inner.Expanded, "Collapsing an ancestor changed its child's retained expansion.");
        for (int index = 0; index < 6; index++)
        {
            panel.Update(Neutral, 1, [UiInputEvent.KeyPress(Keys.Tab, false, false)]);
            Require(!probe.Focused && !inner.Focused, "Collapsed descendants remained in the keyboard traversal.");
        }
        Focus(middle);
        panel.Update(Neutral, 1, [UiInputEvent.KeyPress(Keys.Enter, false, false)]);
        Focus(probe);
        panel.DrawBody(null!);
        Require(probe.Draws == 2, "Reopening an ancestor failed to restore the nested draw path.");

        void Focus(UiWidget target)
        {
            for (int count = 0; !target.Focused && count < 16; count++)
            { panel.Update(Neutral, 1, [UiInputEvent.KeyPress(Keys.Tab, false, false)]); }
            Require(target.Focused, "Nested draw/collapse target was absent from keyboard traversal.");
        }
    }

    private static UiSection SectionContaining(UiSection parent, string label) => parent.Children.OfType<UiSection>()
        .Single(section => section.Children.OfType<UiNumber>().Any(number => Field<string>(number, "label") == label));
    private static UiNumber Number(UiSection section, string label) => section.Children.OfType<UiNumber>()
        .Single(number => Field<string>(number, "label") == label);
    private static T Field<T>(object owner, string name) =>
        (T)(owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
            ?? throw new InvalidOperationException($"Missing retained widget field {name}."));
    private static MouseState MouseAt(Point point, bool pressed = false, bool middle = false, bool right = false) => new(point.X, point.Y, 0,
        pressed ? ButtonState.Pressed : ButtonState.Released, middle ? ButtonState.Pressed : ButtonState.Released,
        right ? ButtonState.Pressed : ButtonState.Released, ButtonState.Released, ButtonState.Released);
    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
    private sealed class DrawProbe : UiWidget
    {
        internal int Draws { get; private set; }
        internal override void Draw(UiCanvas canvas) => Draws++;
    }
}
