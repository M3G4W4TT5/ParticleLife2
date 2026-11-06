using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ParticleLife.Core.Experiments;
using ParticleLife.MonoGame.Editor;
using ParticleLife.Viewer;

/// <summary>Exercises guide paging, scale recovery and quality selection without a graphics device.</summary>
internal static class GuidanceChecks
{
    internal static void Run()
    {
        UiPanel help = new(message => throw new InvalidOperationException(message), dismissOnEscape: true);
        QuickstartHelp.AddTo(help, () => "Retained validation failure");
        help.Arrange(new Rectangle(0, 0, 360, 240));
        UiWidget first = help.Content[0];
        int initialTop = first.Bounds.Top;
        MouseState neutral = new(400, 400, 0, ButtonState.Released, ButtonState.Released,
            ButtonState.Released, ButtonState.Released, ButtonState.Released);
        help.Update(neutral, 1, [UiInputEvent.KeyPress(Keys.PageDown, false, false)]);
        Require(help.ConsumedKeyboard && first.Bounds.Top < initialTop, "Help did not page using the keyboard without pointer/focus.");
        help.Update(neutral, 1, [UiInputEvent.KeyPress(Keys.PageUp, false, false)]);
        Require(first.Bounds.Top == initialTop, "Help PageUp did not return to the top.");

        UiPanel editing = new(message => throw new InvalidOperationException(message));
        UiTextField text = new("Draft", () => "unchanged", _ => { });
        editing.Content.Add(text);
        for (int index = 0; index < 8; index++) { editing.Content.Add(new UiLabel(() => "Scrollable content", 64)); }
        editing.Arrange(new Rectangle(0, 0, 360, 240));
        int fieldTop = text.Bounds.Top;
        editing.Update(neutral, 1, [UiInputEvent.KeyPress(Keys.Tab, false, false), UiInputEvent.KeyPress(Keys.PageDown, false, false)]);
        Require(text.Focused && text.Bounds.Top == fieldTop, "Panel paging stole a key from a focused text draft.");

        UiPanel menus = new(message => throw new InvalidOperationException(message));
        UiDropdown choices = new("Choice", ["First", "Second"], () => 0, _ => { });
        menus.Content.Add(choices);
        for (int index = 0; index < 8; index++) { menus.Content.Add(new UiLabel(() => "Scrollable content", 64)); }
        menus.Arrange(new Rectangle(0, 0, 360, 240));
        int choiceTop = choices.Bounds.Top;
        menus.Update(neutral, 1, [UiInputEvent.KeyPress(Keys.Tab, false, false), UiInputEvent.KeyPress(Keys.Enter, false, false),
            UiInputEvent.KeyPress(Keys.PageDown, false, false)]);
        Require(choices.IsOpen && choices.Bounds.Top == choiceTop, "Paging scrolled a panel through a dropdown opened in the same key batch.");

        Require(UiAccessibility.TryAdjustScale(Keys.D0, 3, out double scale) && scale == 1,
            "The UI scale recovery shortcut did not select 1x.");
        Require(UiAccessibility.TryAdjustScale(Keys.OemPlus, 3, out scale) && scale == 3,
            "Increasing UI scale exceeded its supported maximum.");
        Require(UiAccessibility.TryAdjustScale(Keys.Subtract, 0.5, out scale) && scale == 0.5,
            "Decreasing UI scale exceeded its supported minimum.");
        Require(!UiAccessibility.TryAdjustScale(Keys.R, 1.25, out scale) && scale == 1.25,
            "A simulation shortcut was accidentally handled as a scale edit.");

        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 4, 2, 17), diagnosticEverySteps: 0);
        ExperimentEditor editor = new(playback);
        using EditorSession session = new(editor);
        UiSection appearance = new("Appearance", true);
        appearance.Children.Add(new UiLabel(() => "Independent display settings"));
        QualityControls.AddTo(appearance, session);
        UiDropdown quality = (UiDropdown)appearance.Children[1];
        quality.Bounds = new Rectangle(0, 0, 360, 28);
        quality.ArrangePopup(new Rectangle(0, 0, 360, 240));
        quality.Key(Keys.Down, false, false);
        quality.Key(Keys.Enter, false, false);
        Require(VisualQuality.Match(editor.Appearance.Visuals) == VisualQualityPreset.Economy,
            "Keyboard quality selection did not apply the chosen display preset.");
        Require(playback.Observe().Step == 0 && editor.SessionEventCount == 0,
            "Display quality selection advanced or recorded a numerical edit.");
        Console.WriteLine("Guidance managed checks: keyboard help paging, UI scale recovery and manual quality selection passed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
