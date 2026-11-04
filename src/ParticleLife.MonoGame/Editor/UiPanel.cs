using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace ParticleLife.MonoGame.Editor;

/// <summary>Owns keyboard focus, clipping and scrolling independently of widget drawing or simulation data.</summary>
/// <remarks>Retains widgets and reuses layout lists. Text input never falls through to game shortcuts.
/// Focus loss cancels pointer capture and ends a slider gesture before the host pauses.</remarks>
internal sealed class UiPanel(Action<string> reportError, bool dismissOnEscape = false)
{
    private readonly List<UiWidget> layout = [];
    private readonly List<UiWidget> focusOrder = [];
    private UiWidget? focused;
    private UiWidget? captured;
    private UiWidget? hovered;
    private int previousWheel;
    private ButtonState previousButton;
    private float scroll;
    private float contentHeight;
    private bool draggingScroll;
    internal List<UiWidget> Toolbar { get; } = [];
    internal List<UiWidget> Content { get; } = [];
    internal Rectangle Bounds { get; set; }
    internal Rectangle Body { get; private set; }
    internal bool HasFocus => focused is not null;
    internal bool Capturing => focused is not null || captured is not null || draggingScroll;
    internal bool EditingText => focused?.EditingText == true;
    internal bool ConsumedKeyboard { get; private set; }
    internal bool DismissRequested { get; private set; }
    private bool Compact => Bounds.Height < 360;

    internal void Arrange(Rectangle bounds)
    {
        Bounds = bounds;
        Layout();
    }

    internal void ClearFocus()
    {
        // Explicit dismissal cancels an unfinished draft. Ordinary pointer/Tab movement
        // uses the validating blur below and cannot proceed past an invalid draft.
        Try(() => focused?.Key(Keys.Escape, false, false));
        Try(() => focused?.Focus(false));
        Try(() => captured?.Release());
        focused = captured = null;
        draggingScroll = false;
    }

    internal void ScrollToTop() => scroll = 0;

    internal void Update(MouseState mouse, float scale, IReadOnlyList<UiInputEvent> input)
    {
        ConsumedKeyboard = false;
        DismissRequested = false;
        Layout();
        Point point = new((int)(mouse.X / scale), (int)(mouse.Y / scale));
        bool pressed = mouse.LeftButton == ButtonState.Pressed && previousButton == ButtonState.Released;
        bool released = mouse.LeftButton == ButtonState.Released && previousButton == ButtonState.Pressed;
        int wheel = mouse.ScrollWheelValue - previousWheel;
        previousWheel = mouse.ScrollWheelValue;
        previousButton = mouse.LeftButton;

        hovered = null;
        foreach (UiWidget widget in layout)
        {
            widget.Hovered = widget.Bounds.Contains(point) && (IsToolbar(widget) || Body.Contains(point));
            if (widget.Hovered && widget.Focusable)
            {
                hovered = widget;
            }
        }
        if (wheel != 0 && Body.Contains(point) && captured is null)
        {
            scroll = Math.Clamp(scroll - wheel * 0.45f, 0, Math.Max(0, contentHeight - Body.Height));
            Layout();
        }

        if (pressed)
        {
            Rectangle scrollTrack = new(Bounds.Right - 10, Body.Y, 10, Body.Height);
            if (scrollTrack.Contains(point))
            {
                draggingScroll = true;
            }
            else
            {
                bool canChangeFocus = true;
                if (focused != hovered)
                {
                    Try(() => canChangeFocus = focused?.Focus(false) ?? true);
                    if (canChangeFocus)
                    {
                        focused = hovered;
                        Try(() => focused?.Focus(true));
                    }
                }
                if (canChangeFocus)
                {
                    captured = hovered;
                    Try(() => captured?.Press(point));
                }
            }
        }
        if (draggingScroll)
        {
            float fraction = Math.Clamp((float)(point.Y - Body.Y) / Math.Max(1, Body.Height), 0, 1);
            scroll = fraction * Math.Max(0, contentHeight - Body.Height);
            Layout();
        }
        else if (captured is not null && mouse.LeftButton == ButtonState.Pressed && !pressed)
        {
            Try(() => captured.Drag(point));
        }
        if (released)
        {
            Try(() => captured?.Release());
            captured = null;
            draggingScroll = false;
        }

        foreach (UiInputEvent entry in input)
        {
            ConsumedKeyboard |= focused is not null || !entry.IsText && entry.Key == Keys.Tab;
            Keys key = entry.Key;
            bool shift = entry.Shift;
            bool control = entry.Control;
            if (entry.IsText)
            {
                if (!control)
                {
                    Try(() => focused?.Text(entry.Character));
                }
                continue;
            }
            if (dismissOnEscape && key == Keys.Escape && focused is null)
            {
                DismissRequested = ConsumedKeyboard = true;
                break;
            }
            if (key == Keys.Tab)
            {
                MoveFocus(shift ? -1 : 1);
            }
            else if (key == Keys.Escape && focused is not null)
            {
                Try(() => focused.Key(key, control, shift));
                ClearFocus();
            }
            else if (control && key == Keys.V && focused?.EditingText == true)
            {
                Try(() =>
                {
                    foreach (char character in NativeClipboard.Read())
                    {
                        focused.Text(character);
                    }
                });
            }
            else if (control && key is Keys.C or Keys.X && focused?.ClipboardText is string copied)
            {
                Try(() => NativeClipboard.Write(copied));
                if (key == Keys.X)
                {
                    Try(() => focused.Key(Keys.A, true, false));
                    Try(() => focused.Key(Keys.Delete, true, false));
                }
            }
            else
            {
                Try(() => focused?.Key(key, control, shift));
            }
        }
        Layout();
    }

    private void MoveFocus(int delta)
    {
        if (focusOrder.Count == 0)
        {
            return;
        }
        int current = focused is null ? (delta > 0 ? -1 : 0) : focusOrder.IndexOf(focused);
        bool canChangeFocus = true;
        Try(() => canChangeFocus = focused?.Focus(false) ?? true);
        if (!canChangeFocus)
        {
            return;
        }
        focused = focusOrder[(current + delta + focusOrder.Count) % focusOrder.Count];
        Try(() => focused.Focus(true));
        if (!IsToolbar(focused))
        {
            if (focused.Bounds.Y < Body.Y)
            {
                scroll -= Body.Y - focused.Bounds.Y;
            }
            else if (focused.Bounds.Bottom > Body.Bottom)
            {
                scroll += focused.Bounds.Bottom - Body.Bottom;
            }
            scroll = Math.Clamp(scroll, 0, Math.Max(0, contentHeight - Body.Height));
        }
        Layout();
    }

    private bool IsToolbar(UiWidget widget) => !Compact && widget.Bounds.Y < Body.Y && Toolbar.Any(root =>
        ReferenceEquals(root, widget) || root is UiRow row && row.Children.Contains(widget));

    private void Layout()
    {
        layout.Clear();
        focusOrder.Clear();
        int y = Bounds.Y + (Compact ? 28 : 36);
        if (!Compact)
        {
            foreach (UiWidget widget in Toolbar)
            {
                Place(widget, y);
                y += widget.Height + 6;
            }
        }
        Body = new Rectangle(Bounds.X + 12, y + 6, Bounds.Width - 24, Math.Max(20, Bounds.Bottom - y - (Compact ? 43 : 87)));
        y = Body.Y - (int)scroll;
        if (Compact)
        {
            foreach (UiWidget widget in Toolbar)
            {
                Place(widget, y);
                y += widget.Height + 6;
            }
        }
        foreach (UiWidget widget in Content)
        {
            Place(widget, y);
            y += widget.Height + 8;
            if (widget is UiSection { Expanded: true } section)
            {
                foreach (UiWidget child in section.Children)
                {
                    Place(child, y);
                    y += child.Height + 7;
                }
                y += 7;
            }
        }
        contentHeight = y + scroll - Body.Y;
        scroll = Math.Clamp(scroll, 0, Math.Max(0, contentHeight - Body.Height));
    }

    private void Place(UiWidget widget, int y)
    {
        widget.ReportValidationError = reportError;
        widget.Bounds = new Rectangle(Bounds.X + 12, y, Bounds.Width - 30, 0);
        widget.Bounds = new Rectangle(widget.Bounds.X, widget.Bounds.Y, widget.Bounds.Width, widget.Height);
        if (widget is UiRow row)
        {
            row.Layout();
            foreach (UiWidget child in row.Children)
            {
                child.ReportValidationError = reportError;
                layout.Add(child);
                if (child.Focusable)
                {
                    focusOrder.Add(child);
                }
            }
        }
        else
        {
            layout.Add(widget);
            if (widget.Focusable)
            {
                focusOrder.Add(widget);
            }
        }
    }

    internal void DrawChrome(UiCanvas canvas, string title)
    {
        canvas.Fill(Bounds, UiCanvas.Background);
        canvas.Border(Bounds, UiCanvas.Raised);
        canvas.Text(title, Bounds.X + 12, Bounds.Y + 12, UiCanvas.Accent);
        if (!Compact)
        {
            foreach (UiWidget widget in Toolbar)
            {
                widget.Draw(canvas);
            }
        }
        UiWidget? explained = hovered ?? focused;
        string help = explained?.Explanation ?? "Scroll to explore. Tab navigates. Click a value to type; Enter applies. R beside a slider resets that value.";
        Rectangle footer = new(Bounds.X + 12, Body.Bottom + (Compact ? 5 : 10), Bounds.Width - 24, Compact ? 30 : 65);
        canvas.Wrapped(help, footer, UiCanvas.Muted);
        if (contentHeight > Body.Height)
        {
            Rectangle track = new(Bounds.Right - 8, Body.Y, 4, Body.Height);
            canvas.Fill(track, UiCanvas.Surface);
            int size = Math.Max(22, (int)(Body.Height * Body.Height / contentHeight));
            int offset = (int)((Body.Height - size) * scroll / (contentHeight - Body.Height));
            canvas.Fill(new Rectangle(track.X, track.Y + offset, track.Width, size), UiCanvas.Accent * 0.7f);
        }
    }

    internal void DrawBody(UiCanvas canvas)
    {
        if (Compact)
        {
            foreach (UiWidget widget in Toolbar)
            {
                if (widget.Bounds.Intersects(Body))
                {
                    widget.Draw(canvas);
                }
            }
        }
        foreach (UiWidget widget in Content)
        {
            if (widget.Bounds.Intersects(Body))
            {
                widget.Draw(canvas);
            }
            if (widget is UiSection { Expanded: true } section)
            {
                foreach (UiWidget child in section.Children)
                {
                    if (child.Bounds.Intersects(Body))
                    {
                        child.Draw(canvas);
                    }
                }
            }
        }
    }

    private void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (ViewerErrors.IsExpected(exception))
        {
            reportError(exception.Message);
        }
    }
}
