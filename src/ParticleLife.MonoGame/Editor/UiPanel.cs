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
    private Point previousPointer;
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
        bool pointerMoved = point != previousPointer;
        previousPointer = point;
        bool pressed = mouse.LeftButton == ButtonState.Pressed && previousButton == ButtonState.Released;
        bool released = mouse.LeftButton == ButtonState.Released && previousButton == ButtonState.Pressed;
        int wheel = mouse.ScrollWheelValue - previousWheel;
        previousWheel = mouse.ScrollWheelValue;
        previousButton = mouse.LeftButton;

        UiDropdown? popup = focused as UiDropdown;
        if (popup?.IsOpen != true)
        {
            popup = null;
        }
        if (popup is not null)
        {
            if (wheel != 0)
            {
                popup.ScrollOptions(wheel);
            }
            if (pointerMoved || wheel != 0)
            {
                popup.HoverOption(point);
            }
        }
        else if (wheel != 0 && Body.Contains(point) && captured is null)
        {
            scroll = Math.Clamp(scroll - wheel * 0.3f, 0, Math.Max(0, contentHeight - Body.Height));
            Layout();
        }

        // Wheel input can move the layout during the same frame as a press. Hit-test only
        // after that move, so the visible control under the pointer receives the click.
        hovered = null;
        foreach (UiWidget widget in layout)
        {
            widget.Hovered = popup is null && widget.Bounds.Contains(point) && (IsToolbar(widget) || Body.Contains(point));
            if (widget.Hovered && widget.Focusable)
            {
                hovered = widget;
            }
        }
        if (popup is not null)
        {
            popup.Hovered = popup.Bounds.Contains(point) || popup.PopupBounds.Contains(point);
            hovered = popup;
        }

        if (pressed && popup is not null)
        {
            // Even an outside dismissal consumes this press. Nothing behind the menu is activated.
            Try(() => popup.Press(point));
            captured = null;
        }
        else if (pressed)
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
            else if (key == Keys.Escape && focused is UiDropdown { IsOpen: true } dropdown)
            {
                dropdown.Close();
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
        int y = Bounds.Y + 34;
        if (!Compact)
        {
            foreach (UiWidget widget in Toolbar)
            {
                Place(widget, y);
                y += widget.Height + 3;
            }
        }
        Body = new Rectangle(Bounds.X + 10, y + 5, Math.Max(1, Bounds.Width - 20), Math.Max(20, Bounds.Bottom - y - 52));
        y = Body.Y - (int)scroll;
        if (Compact)
        {
            foreach (UiWidget widget in Toolbar)
            {
                Place(widget, y);
                y += widget.Height + 3;
            }
        }
        foreach (UiWidget widget in Content)
        {
            Place(widget, y);
            y += widget.Height + 3;
            if (widget is UiSection { Expanded: true } section)
            {
                foreach (UiWidget child in section.Children)
                {
                    Place(child, y);
                    y += child.Height + 3;
                }
                y += 4;
            }
        }
        contentHeight = y + scroll - Body.Y;
        float clamped = Math.Clamp(scroll, 0, Math.Max(0, contentHeight - Body.Height));
        if (clamped != scroll)
        {
            scroll = clamped;
            Layout();
            return;
        }
        if (focused is not null && !layout.Contains(focused) || captured is not null && !layout.Contains(captured))
        {
            ClearFocus();
        }
        foreach (UiWidget widget in layout)
        {
            if (widget is UiDropdown dropdown)
            {
                dropdown.ArrangePopup(Bounds);
            }
        }
    }

    private void Place(UiWidget widget, int y)
    {
        widget.ReportValidationError = reportError;
        widget.Bounds = new Rectangle(Bounds.X + 10, y, Math.Max(1, Bounds.Width - 24), 0);
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
        canvas.Glass(Bounds);
        canvas.TextFit(title, new Rectangle(Bounds.X + 12, Bounds.Y + 3, Bounds.Width - 24, 28), UiCanvas.Ink);
        canvas.Fill(new Rectangle(Bounds.X + 10, Bounds.Y + 30, Bounds.Width - 20, 1), UiCanvas.Muted * 0.14f);
        if (!Compact)
        {
            foreach (UiWidget widget in Toolbar)
            {
                widget.Draw(canvas);
            }
        }
        UiWidget? explained = hovered ?? focused;
        string help = explained?.Explanation ?? string.Empty;
        Rectangle footer = new(Bounds.X + 12, Body.Bottom + 7, Bounds.Width - 24, 38);
        canvas.Fill(new Rectangle(Bounds.X + 10, Body.Bottom + 3, Bounds.Width - 20, 1), UiCanvas.Muted * 0.12f);
        canvas.Wrapped(help, footer, UiCanvas.Muted);
        if (contentHeight > Body.Height)
        {
            Rectangle track = new(Bounds.Right - 8, Body.Y, 4, Body.Height);
            canvas.Fill(track, UiCanvas.Surface);
            int size = Math.Max(22, (int)(Body.Height * Body.Height / contentHeight));
            int offset = (int)((Body.Height - size) * scroll / (contentHeight - Body.Height));
            canvas.Rounded(new Rectangle(track.X, track.Y + offset, track.Width, size), UiCanvas.Muted * 0.55f, 2);
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

    /// <summary>Draws the active menu after body scissoring has ended, within the panel bounds.</summary>
    internal void DrawOverlay(UiCanvas canvas)
    {
        if (focused is UiDropdown { IsOpen: true } dropdown)
        {
            dropdown.DrawPopup(canvas);
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
