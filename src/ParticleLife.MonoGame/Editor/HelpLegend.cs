using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace ParticleLife.MonoGame.Editor;

/// <summary>Centralizes keyboard and pointer guidance in an explicitly opened, scrollable legend.</summary>
internal sealed class HelpLegend
{
    private readonly UiPanel panel;
    internal Rectangle Bounds => panel.Bounds;
    internal Rectangle Body => panel.Body;
    internal void Arrange(Rectangle bounds) => panel.Arrange(bounds);
    internal void PrepareForOpen(bool pointerHeld) => panel.PrepareForOpen(pointerHeld);

    internal HelpLegend(Action close, Action<string> report)
    {
        panel = new UiPanel(report, dismissOnEscape: true);
        panel.Toolbar.Add(new UiButton(() => "Close help", close));
        Add("Playback", [
            ("Space", "Pause or resume"), ("N / Right", "Pause and advance one step"),
            ("R", "Reset exact initial state and rules"), ("I", "Resample positions and velocities"),
            ("M", "Randomize the live matrix"), ("1 / 2 / 3", "Random / Chains / Snakes matrix")]);
        Add("Workspace", [
            ("E", "Show or hide the inspector"), ("S / O", "Open the file library"),
            ("F5", "Reload startup file or open library"), ("D", "Toggle diagnostic sampling"),
            ("H / F1", "Open or close Help"), ("F9", "Print viewer timing measurements"),
            ("Ctrl+Z / Y", "Undo / redo at the current step"), ("Escape", "Close dialog; exit with world focus")]);
        Add("Inspector", [
            ("Drag label", "Scrub a value; one gesture, one undo"),
            ("Click value", "Type an exact number"), ("Tab / Shift+Tab", "Move between controls"),
            ("Enter", "Apply a draft or dropdown choice"), ("Escape", "Cancel a draft or dropdown"),
            ("Ctrl+A / C / X / V", "Select / copy / cut / paste"),
            ("Arrow keys", "Navigate matrix cells or menu options"),
            ("Scroll", "Scroll the inspector or open dropdown"),
            ("Drag panel edge", "Resize inspector width in wide windows")]);
        Add("Camera and canvas", [
            ("Wheel on canvas", "Zoom around the pointer"), ("Middle / right drag", "Pan the view"),
            ("V", "Fit the nominal world"), ("World > Camera", "Exact zoom/centre, smoothing and size units"),
            ("Particles > Brushes", "Choose move/add/paint/erase/force tools"),
            ("Left drag canvas", "Apply the selected live brush"), ("Escape in gesture", "Stop; Ctrl+Z undoes applied edits")]);
        Add("Directed matrix", [
            ("Drag cells", "Select a rectangle"), ("Ctrl+click", "Toggle cells or add a header selection"),
            ("Shift+click/arrows", "Extend selection from its anchor"), ("Click row/column", "Select a whole directed row/column"),
            ("Shift+header", "Open that species colour picker"), ("Ctrl+A / C / V", "Select all / copy full matrix / validate paste")]);
        panel.Content.Add(new UiLabel(() => "Focused controls capture simulation shortcuts. Hover a control for its units, range and meaning.", 40));
    }

    private void Add(string title, (string Key, string Action)[] entries)
    {
        UiSection section = new(title, true);
        foreach ((string key, string action) in entries)
        {
            section.Children.Add(new LegendEntry(key, action));
        }
        panel.Content.Add(section);
    }

    internal void Update(Rectangle bounds, float scale, MouseState mouse, IReadOnlyList<UiInputEvent> input, Action close)
    {
        panel.Arrange(bounds);
        panel.Update(mouse, scale, input);
        if (panel.DismissRequested || input.Any(entry => !entry.IsText && entry.Key is Keys.H or Keys.F1))
        {
            panel.ClearFocus();
            close();
        }
    }

    internal void LoseFocus() => panel.ClearFocus();
    internal void DrawChrome(UiCanvas canvas) => panel.DrawChrome(canvas, "Help");
    internal void DrawBody(UiCanvas canvas) => panel.DrawBody(canvas);

    private sealed class LegendEntry(string key, string action) : UiWidget
    {
        internal override bool Focusable => false;
        internal override int Height => 26;
        internal override void Draw(UiCanvas canvas)
        {
            int keyWidth = Math.Min(132, Bounds.Width / 3);
            canvas.TextFit(key, new Rectangle(Bounds.X + 5, Bounds.Y, keyWidth - 10, Height), UiCanvas.Accent, 1.35f);
            canvas.TextFit(action, new Rectangle(Bounds.X + keyWidth, Bounds.Y, Bounds.Width - keyWidth - 4, Height));
        }
    }
}
