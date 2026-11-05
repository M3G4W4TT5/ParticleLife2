using System.Globalization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace ParticleLife.MonoGame.Editor;

/// <summary>A retained editor control; input runs at the host's completed-step boundary, never in Draw.</summary>
internal abstract class UiWidget(string help = "")
{
    internal Rectangle Bounds { get; set; }
    internal string Help { get; set; } = help;
    internal virtual string Explanation => Help;
    internal Action<string>? ReportValidationError { get; set; }
    internal virtual string? ClipboardText => null;
    internal bool Focused { get; set; }
    internal bool Hovered { get; set; }
    internal virtual bool Focusable => true;
    internal virtual bool EditingText => false;
    internal virtual int Height => 28;
    internal virtual bool Focus(bool focused)
    {
        Focused = focused;
        return true;
    }
    internal virtual void Press(Point point) { }
    internal virtual void Drag(Point point) { }
    internal virtual void Release() { }
    internal virtual void Key(Keys key, bool control, bool shift) { }
    internal virtual void Text(char character) { }
    internal abstract void Draw(UiCanvas canvas);

    protected void DrawFocus(UiCanvas canvas)
    {
        if (Focused)
        {
            canvas.RoundedBorder(Bounds, UiCanvas.Accent * 0.7f, 4);
        }
    }
}

internal sealed class UiButton(Func<string> label, Action clicked, string help = "", Func<bool>? enabled = null,
    Func<bool>? selected = null) : UiWidget(help)
{
    internal override void Press(Point point)
    {
        if (enabled?.Invoke() != false)
        {
            clicked();
        }
    }

    internal override void Key(Keys key, bool control, bool shift)
    {
        if (key is Keys.Enter or Keys.Space)
        {
            Press(default);
        }
    }

    internal override void Draw(UiCanvas canvas)
    {
        bool active = enabled?.Invoke() != false;
        bool current = selected?.Invoke() == true;
        canvas.Rounded(Bounds, current ? UiCanvas.Accent * 0.15f : Hovered && active ? UiCanvas.Raised : UiCanvas.Surface, 4);
        if (current)
        {
            canvas.Fill(new Rectangle(Bounds.X + 8, Bounds.Bottom - 2, Bounds.Width - 16, 1), UiCanvas.Accent * 0.7f);
        }
        canvas.TextCentered(label(), Bounds, !active ? UiCanvas.Muted * 0.65f : current ? UiCanvas.Accent : UiCanvas.Ink);
        DrawFocus(canvas);
    }
}

internal sealed class UiToggle(string label, Func<bool> get, Action<bool> set, string help = "") : UiWidget(help)
{
    internal override void Press(Point point) => set(!get());
    internal override void Key(Keys key, bool control, bool shift)
    {
        if (key is Keys.Enter or Keys.Space)
        {
            Press(default);
        }
    }

    internal override int Height => 26;

    internal override void Draw(UiCanvas canvas)
    {
        canvas.Rounded(Bounds, Hovered ? UiCanvas.Raised : UiCanvas.Surface, 4);
        Rectangle track = new(Bounds.Right - 36, Bounds.Y + 6, 28, 14);
        bool enabled = get();
        canvas.Rounded(track, enabled ? UiCanvas.Accent * 0.55f : UiCanvas.Muted * 0.22f, 7);
        canvas.Rounded(new Rectangle(track.X + (enabled ? 15 : 3), track.Y + 3, 10, 8),
            enabled ? UiCanvas.Ink : UiCanvas.Muted, 4);
        canvas.TextFit(label, new Rectangle(Bounds.X + 8, Bounds.Y, Bounds.Width - 52, Height), UiCanvas.Ink);
        DrawFocus(canvas);
    }
}

/// <summary>Exact draft text is committed only on Enter, Tab or blur; incomplete input never reaches a model.</summary>
internal class UiTextField(string label, Func<string> get, Action<string> commit, string help = "") : UiWidget(help)
{
    private string draft = string.Empty;
    private string? error;
    private bool selected;
    private bool dirty;
    private int cursor;
    internal override int Height => 28;
    internal override bool EditingText => Focused;
    internal string Draft => Focused || dirty ? draft : get();
    internal override string? ClipboardText => Draft;
    internal override string Explanation => error is null ? Help : $"INVALID: {error} {Help}";

    internal override bool Focus(bool focused)
    {
        if (focused && !Focused)
        {
            draft = get();
            cursor = draft.Length;
            selected = true;
            dirty = false;
            error = null;
        }
        else if (!focused && Focused && dirty)
        {
            if (!Commit())
            {
                return false;
            }
        }
        return base.Focus(focused);
    }

    internal override void Key(Keys key, bool control, bool shift)
    {
        if (control && key == Keys.A)
        {
            selected = true;
            cursor = draft.Length;
        }
        else if (key == Keys.Back)
        {
            if (selected)
            {
                draft = string.Empty;
                cursor = 0;
            }
            else if (cursor > 0)
            {
                draft = draft.Remove(--cursor, 1);
            }
            selected = false;
            dirty = true;
        }
        else if (key == Keys.Delete)
        {
            if (selected)
            {
                draft = string.Empty;
                cursor = 0;
            }
            else if (cursor < draft.Length)
            {
                draft = draft.Remove(cursor, 1);
            }
            selected = false;
            dirty = true;
        }
        else if (key == Keys.Enter)
        {
            Commit();
            selected = true;
        }
        else if (key is Keys.Left or Keys.Right or Keys.Home or Keys.End)
        {
            cursor = key == Keys.Home || key == Keys.Left && selected ? 0
                : key == Keys.End || key == Keys.Right && selected ? draft.Length
                : Math.Clamp(cursor + (key == Keys.Left ? -1 : 1), 0, draft.Length);
            selected = false;
        }
        else if (key == Keys.Escape)
        {
            draft = get();
            cursor = draft.Length;
            dirty = false;
            error = null;
        }
    }

    internal override void Text(char character)
    {
        if (!char.IsControl(character) && draft.Length < 32768)
        {
            if (selected)
            {
                draft = string.Empty;
                cursor = 0;
            }
            draft = draft.Insert(cursor++, character.ToString());
            selected = false;
            dirty = true;
            error = null;
        }
    }

    private bool Commit()
    {
        try
        {
            commit(draft);
            draft = get();
            cursor = draft.Length;
            dirty = false;
            error = null;
            return true;
        }
        catch (Exception exception) when (ViewerErrors.IsExpected(exception))
        {
            error = exception.Message;
            ReportValidationError?.Invoke(error);
            return false;
        }
    }

    internal override void Draw(UiCanvas canvas)
    {
        int labelWidth = Math.Min(150, Bounds.Width / 3);
        canvas.TextFit(label, new Rectangle(Bounds.X + 8, Bounds.Y, labelWidth - 12, Height), UiCanvas.Muted);
        Rectangle box = new(Bounds.X + labelWidth, Bounds.Y, Bounds.Width - labelWidth, Height);
        canvas.Rounded(box, UiCanvas.Surface, 4);
        canvas.RoundedBorder(box, error is not null ? UiCanvas.Error : Focused ? UiCanvas.Accent : UiCanvas.Raised, 4);
        UiDraftText.Draw(canvas, Draft, cursor, Focused, selected, box);
    }
}

/// <summary>Combines exact number entry, an optional convenience slider, and an explicit reset value.</summary>
/// <remarks>Slider endpoints are a convenience only. Typed finite values are passed unchanged to the
/// complete candidate validator. A drag starts/ends one undo gesture while each successful sample
/// remains a real boundary edit for scientific replay.</remarks>
internal sealed class UiNumber : UiWidget
{
    private readonly string label;
    private readonly Func<double> get;
    private readonly Action<double> set;
    private readonly double reset;
    private readonly double minimum;
    private readonly double maximum;
    private readonly Func<double>? maximumValue;
    private readonly bool logarithmic;
    private readonly bool integral;
    private readonly Action<string>? beginGesture;
    private readonly Action? endGesture;
    private string draft = string.Empty;
    private string? error;
    private bool selected;
    private bool editing;
    private bool dragging;
    private bool dirty;
    private int cursor;

    internal UiNumber(string label, Func<double> get, Action<double> set, double reset, double minimum,
        double maximum, string help, bool logarithmic = false, bool integral = false,
        Action<string>? beginGesture = null, Action? endGesture = null, Func<double>? maximumValue = null) : base(help)
    {
        this.label = label;
        this.get = get;
        this.set = set;
        this.reset = reset;
        this.minimum = minimum;
        this.maximum = maximum;
        this.maximumValue = maximumValue;
        this.logarithmic = logarithmic;
        this.integral = integral;
        this.beginGesture = beginGesture;
        this.endGesture = endGesture;
    }

    internal override int Height => 28;
    internal override bool EditingText => Focused && editing;
    internal override string? ClipboardText => editing ? draft : Format(get());
    private double Maximum => maximumValue?.Invoke() ?? maximum;
    internal override string Explanation => error is not null ? $"INVALID: {error} The live value remains {get():G17}. Enter corrects; Esc cancels."
        : $"Exact: {get():G17}. {Help} Slider: {minimum:G4}..{Maximum:G4}; reset: {reset:G9}.";
    internal double SliderFraction
    {
        get
        {
            double limit = Maximum;
            if (limit <= minimum)
            {
                return 0;
            }
            double value = get();
            return Math.Clamp(logarithmic && value > 0 ? Math.Log(value / minimum) / Math.Log(limit / minimum)
                : (value - minimum) / (limit - minimum), 0, 1);
        }
    }
    /// <summary>The exact-entry target is separate from the label/slider scrub target.</summary>
    internal Rectangle ValueBounds => new(Bounds.Right - Math.Min(150, Bounds.Width / 2) - 25, Bounds.Y,
        Math.Min(150, Bounds.Width / 2), Height);
    internal Rectangle ResetBounds => new(Bounds.Right - 22, Bounds.Y, 22, Height);
    internal Rectangle SliderBounds => new(Bounds.X + 5, Bounds.Y, Math.Max(1, ValueBounds.X - Bounds.X - 10), Height);

    internal override bool Focus(bool focused)
    {
        if (focused && !Focused)
        {
            draft = Format(get());
            cursor = draft.Length;
            selected = true;
            editing = true;
            dirty = false;
            error = null;
        }
        else if (!focused && Focused)
        {
            if (dirty)
            {
                if (!Commit())
                {
                    return false;
                }
            }
            Release();
            editing = false;
        }
        return base.Focus(focused);
    }

    internal override void Press(Point point)
    {
        if (ResetBounds.Contains(point))
        {
            editing = false;
            set(reset);
            error = null;
            dirty = false;
        }
        else if (point.X < ValueBounds.X)
        {
            editing = false;
            dragging = true;
            dirty = false;
            beginGesture?.Invoke(label);
            Drag(point);
        }
        else
        {
            editing = true;
            draft = Format(get());
            cursor = draft.Length;
            dirty = false;
            selected = true;
        }
    }

    internal override void Drag(Point point)
    {
        if (!dragging)
        {
            return;
        }
        double fraction = Math.Clamp((double)(point.X - SliderBounds.X) / SliderBounds.Width, 0, 1);
        double limit = Maximum;
        double value = logarithmic ? Math.Exp(Math.Log(minimum) + fraction * Math.Log(limit / minimum))
            : minimum + fraction * (limit - minimum);
        if (integral)
        {
            value = Math.Round(value);
        }
        try
        {
            set(value);
            error = null;
        }
        catch (Exception exception) when (ViewerErrors.IsExpected(exception))
        {
            error = exception.Message;
            ReportValidationError?.Invoke(error);
        }
    }

    internal override void Release()
    {
        if (dragging)
        {
            dragging = false;
            endGesture?.Invoke();
        }
    }

    internal override void Key(Keys key, bool control, bool shift)
    {
        if (!editing && (key is Keys.Enter or Keys.Back or Keys.Delete or Keys.Left or Keys.Right or Keys.Home or Keys.End
            || control && key == Keys.A))
        {
            draft = Format(get());
            cursor = draft.Length;
            selected = true;
        }
        if (control && key == Keys.A)
        {
            selected = true;
            editing = true;
            cursor = draft.Length;
        }
        else if (key == Keys.Back)
        {
            if (selected)
            {
                draft = string.Empty;
                cursor = 0;
            }
            else if (cursor > 0)
            {
                draft = draft.Remove(--cursor, 1);
            }
            selected = false;
            editing = dirty = true;
        }
        else if (key == Keys.Delete)
        {
            if (selected)
            {
                draft = string.Empty;
                cursor = 0;
            }
            else if (cursor < draft.Length)
            {
                draft = draft.Remove(cursor, 1);
            }
            selected = false;
            editing = dirty = true;
        }
        else if (key == Keys.Enter)
        {
            if (editing)
            {
                Commit();
            }
            editing = true;
            selected = true;
        }
        else if (key == Keys.Escape)
        {
            draft = Format(get());
            cursor = draft.Length;
            editing = dirty = false;
            error = null;
        }
        else if (key is Keys.Left or Keys.Right or Keys.Home or Keys.End)
        {
            cursor = key == Keys.Home || key == Keys.Left && selected ? 0
                : key == Keys.End || key == Keys.Right && selected ? draft.Length
                : Math.Clamp(cursor + (key == Keys.Left ? -1 : 1), 0, draft.Length);
            selected = false;
            editing = true;
        }
    }

    internal override void Text(char character)
    {
        if (char.IsAsciiDigit(character) || character is '+' or '-' or '.' or 'e' or 'E')
        {
            if (draft.Length < 40)
            {
                if (selected || !editing)
                {
                    draft = string.Empty;
                    cursor = 0;
                }
                draft = draft.Insert(cursor++, character.ToString());
                selected = false;
                editing = dirty = true;
                error = null;
            }
        }
    }

    internal static double Parse(string text, bool integral)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
        {
            throw new ArgumentException("Enter a finite number; Enter applies, Esc cancels.");
        }
        if (integral && value != Math.Truncate(value))
        {
            throw new ArgumentException("A whole number is required.");
        }
        return value;
    }

    private bool Commit()
    {
        try
        {
            set(Parse(draft, integral));
            draft = Format(get());
            cursor = draft.Length;
            dirty = false;
            error = null;
            return true;
        }
        catch (Exception exception) when (ViewerErrors.IsExpected(exception))
        {
            error = exception.Message;
            ReportValidationError?.Invoke(error);
            return false;
        }
    }

    private static string Format(double value) => value.ToString("G17", CultureInfo.InvariantCulture);

    internal override void Draw(UiCanvas canvas)
    {
        Rectangle slider = SliderBounds;
        canvas.Rounded(new Rectangle(Bounds.X, Bounds.Y, ValueBounds.X - Bounds.X - 3, Height),
            Hovered ? UiCanvas.Raised : UiCanvas.Surface, 4);
        double fraction = SliderFraction;
        int marker = slider.X + (int)(slider.Width * fraction);
        canvas.Rounded(new Rectangle(slider.X, slider.Y + 2, Math.Max(1, marker - slider.X), Height - 4),
            UiCanvas.Accent * 0.09f, 3);
        for (int index = 1; index < 5; index++)
        {
            canvas.Fill(new Rectangle(slider.X + slider.Width * index / 5, slider.Bottom - 5, 1, 2), UiCanvas.Muted * 0.28f);
        }
        canvas.Fill(new Rectangle(marker, slider.Bottom - 7, 1, 4), UiCanvas.Accent * 0.8f);
        canvas.TextFit(label, new Rectangle(Bounds.X + 8, Bounds.Y - 1, slider.Width - 10, Height), UiCanvas.Ink);
        canvas.Rounded(ValueBounds, UiCanvas.Surface, 4);
        canvas.RoundedBorder(ValueBounds, error is not null ? UiCanvas.Error
            : Focused && editing ? UiCanvas.Accent * 0.75f : UiCanvas.Raised * 0.5f, 4);
        string valueText = editing && Focused || dirty ? draft : get().ToString("G9", CultureInfo.InvariantCulture);
        if (Focused && editing)
        {
            UiDraftText.Draw(canvas, valueText, cursor, true, selected, ValueBounds);
        }
        else
        {
            canvas.TextRight(valueText, ValueBounds.Right - 8, ValueBounds.Y + 6, UiCanvas.Ink);
        }
        canvas.Rounded(ResetBounds, UiCanvas.Surface, 4);
        Vector2 origin = new(ResetBounds.X + 7, ResetBounds.Y + 9);
        canvas.Line(origin + new Vector2(6, 0), origin, UiCanvas.Muted);
        canvas.Line(origin, origin + new Vector2(0, 7), UiCanvas.Muted);
        canvas.Line(origin + new Vector2(0, 7), origin + new Vector2(6, 7), UiCanvas.Muted);
        canvas.Line(origin, origin + new Vector2(3, -3), UiCanvas.Muted);
        canvas.Line(origin, origin + new Vector2(3, 3), UiCanvas.Muted);
        DrawFocus(canvas);
    }
}

internal sealed class UiLabel(Func<string> text, int height = 40, Color? color = null) : UiWidget
{
    internal override bool Focusable => false;
    internal override int Height => height;
    internal override void Draw(UiCanvas canvas) => canvas.Wrapped(text(), Bounds, color ?? UiCanvas.Muted);
}

/// <summary>Compact row of independently focusable retained widgets.</summary>
internal sealed class UiRow(params UiWidget[] children) : UiWidget
{
    internal IReadOnlyList<UiWidget> Children => children;
    internal override bool Focusable => false;
    internal override int Height => children.Max(widget => widget.Height);
    internal void Layout()
    {
        int width = (Bounds.Width - 6 * (children.Length - 1)) / children.Length;
        for (int index = 0; index < children.Length; index++)
        {
            children[index].Bounds = new Rectangle(Bounds.X + index * (width + 6), Bounds.Y, width, Height);
        }
    }
    internal override void Draw(UiCanvas canvas)
    {
        foreach (UiWidget child in children)
        {
            child.Draw(canvas);
        }
    }
}

internal sealed class UiSection(string title, bool expanded = false) : UiWidget
{
    internal List<UiWidget> Children { get; } = [];
    internal bool Expanded { get; set; } = expanded;
    internal override int Height => 28;
    internal override void Press(Point point) => Expanded = !Expanded;
    internal override void Key(Keys key, bool control, bool shift)
    {
        if (key is Keys.Enter or Keys.Space)
        {
            Expanded = !Expanded;
        }
    }
    internal override void Draw(UiCanvas canvas)
    {
        canvas.Rounded(Bounds, Hovered ? UiCanvas.Raised : UiCanvas.Surface * 0.6f, 4);
        Vector2 origin = new(Bounds.X + 9, Bounds.Y + 12);
        if (Expanded)
        {
            canvas.Line(origin, origin + new Vector2(4, 4), UiCanvas.Muted);
            canvas.Line(origin + new Vector2(4, 4), origin + new Vector2(8, 0), UiCanvas.Muted);
        }
        else
        {
            canvas.Line(origin, origin + new Vector2(4, 4), UiCanvas.Muted);
            canvas.Line(origin + new Vector2(4, 4), origin + new Vector2(0, 8), UiCanvas.Muted);
        }
        canvas.TextFit(title, new Rectangle(Bounds.X + 26, Bounds.Y, Bounds.Width - 34, Height), UiCanvas.Accent);
        DrawFocus(canvas);
    }
}

/// <summary>Draws a horizontally scrolling edit draft with a measured proportional-text caret.</summary>
internal static class UiDraftText
{
    internal static void Draw(UiCanvas canvas, string text, int cursor, bool focused, bool selected, Rectangle box)
    {
        float available = Math.Max(1, box.Width - 16);
        int offset = 0;
        cursor = Math.Clamp(cursor, 0, text.Length);
        if (focused && !selected)
        {
            // Walk only the short suffix that can fit, even for a very long pasted path.
            offset = cursor;
            while (offset > 0 && canvas.Measure(text[(offset - 1)..cursor]) <= available - 2)
            {
                offset--;
            }
        }
        int length = 0;
        while (offset + length < text.Length && canvas.Measure(text.Substring(offset, length + 1)) <= available)
        {
            length++;
        }
        string visible = text.Substring(offset, length);
        if (focused && selected)
        {
            canvas.Rounded(new Rectangle(box.X + 5, box.Y + 3,
                Math.Max(1, (int)Math.Ceiling(canvas.Measure(visible)) + 5), box.Height - 6), UiCanvas.Accent * 0.18f, 2);
        }
        canvas.Text(visible, box.X + 8, box.Y + 6, UiCanvas.Ink);
        if (focused && !selected)
        {
            int caret = (int)Math.Round(canvas.Measure(text[offset..cursor]));
            canvas.Fill(new Rectangle(box.X + 8 + caret, box.Y + 5, 1, box.Height - 10), UiCanvas.Accent);
        }
    }
}

/// <summary>A retained choice control whose popup stages navigation until an explicit selection.</summary>
/// <remarks>Opening, navigating, scrolling and dismissing the popup never invoke the setter.
/// The panel owns popup placement and routes pointer/keyboard input before ordinary controls,
/// preventing a dismissal click from activating a control behind the popup.</remarks>
internal sealed class UiDropdown : UiWidget
{
    private const int OptionHeight = 26;
    private const int Padding = 4;
    private readonly string label;
    private readonly IReadOnlyList<string> options;
    private readonly Func<int> get;
    private readonly Action<int> set;
    private int highlighted;
    private int firstVisible;
    private int visibleCount = 1;
    internal bool IsOpen { get; private set; }
    internal Rectangle PopupBounds { get; private set; }
    internal int HighlightedIndex => highlighted;
    internal int FirstVisibleIndex => firstVisible;
    internal int VisibleOptionCount => visibleCount;

    internal UiDropdown(string label, IReadOnlyList<string> options, Func<int> get, Action<int> set,
        string explanation = "") : base(explanation)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Count == 0)
        {
            throw new ArgumentException("A dropdown needs at least one option.", nameof(options));
        }
        this.label = label;
        this.options = options;
        this.get = get;
        this.set = set;
    }

    internal override bool Focus(bool focused)
    {
        if (!focused)
        {
            Close();
        }
        return base.Focus(focused);
    }

    internal void Close() => IsOpen = false;

    private void Open()
    {
        highlighted = Math.Clamp(get(), 0, options.Count - 1);
        firstVisible = Math.Clamp(highlighted - visibleCount + 1, 0, Math.Max(0, options.Count - visibleCount));
        IsOpen = true;
    }

    /// <summary>Fits the popup into the panel, choosing the side with enough space for useful options.</summary>
    internal void ArrangePopup(Rectangle panelBounds)
    {
        int below = Math.Max(0, panelBounds.Bottom - Bounds.Bottom - 10);
        int above = Math.Max(0, Bounds.Y - panelBounds.Y - 10);
        int desired = Math.Min(9, options.Count) * OptionHeight + Padding * 2;
        bool opensBelow = below >= desired || below >= above;
        int available = opensBelow ? below : above;
        visibleCount = Math.Clamp((available - Padding * 2) / OptionHeight, 1, Math.Min(9, options.Count));
        int height = visibleCount * OptionHeight + Padding * 2;
        int y = opensBelow ? Bounds.Bottom + 3 : Bounds.Y - height - 3;
        y = Math.Clamp(y, panelBounds.Y + 4, Math.Max(panelBounds.Y + 4, panelBounds.Bottom - height - 4));
        PopupBounds = new Rectangle(Bounds.X, y, Bounds.Width, height);
        firstVisible = Math.Clamp(firstVisible, 0, Math.Max(0, options.Count - visibleCount));
    }

    internal Rectangle OptionBounds(int index) => new(PopupBounds.X + Padding,
        PopupBounds.Y + Padding + (index - firstVisible) * OptionHeight,
        PopupBounds.Width - Padding * 2, OptionHeight);

    internal override void Press(Point point)
    {
        if (!IsOpen)
        {
            Open();
        }
        else if (PopupBounds.Contains(point))
        {
            int index = firstVisible + (point.Y - PopupBounds.Y - Padding) / OptionHeight;
            if (point.Y >= PopupBounds.Y + Padding && index < firstVisible + visibleCount && index < options.Count)
            {
                highlighted = index;
                Select();
            }
        }
        else
        {
            Close();
        }
    }

    internal void HoverOption(Point point)
    {
        if (PopupBounds.Contains(point) && point.Y >= PopupBounds.Y + Padding)
        {
            int index = firstVisible + (point.Y - PopupBounds.Y - Padding) / OptionHeight;
            if (index < firstVisible + visibleCount && index < options.Count)
            {
                highlighted = index;
            }
        }
    }

    internal void ScrollOptions(int wheel)
    {
        int rows = Math.Max(1, Math.Abs(wheel) / 120);
        firstVisible = Math.Clamp(firstVisible - Math.Sign(wheel) * rows,
            0, Math.Max(0, options.Count - visibleCount));
    }

    internal override void Key(Keys key, bool control, bool shift)
    {
        if (key == Keys.Escape)
        {
            Close();
            return;
        }
        if (key is Keys.Enter or Keys.Space)
        {
            if (IsOpen)
            {
                Select();
            }
            else
            {
                Open();
            }
            return;
        }
        if (key is Keys.Up or Keys.Down or Keys.Home or Keys.End)
        {
            if (!IsOpen)
            {
                Open();
            }
            highlighted = key == Keys.Home ? 0 : key == Keys.End ? options.Count - 1
                : Math.Clamp(highlighted + (key == Keys.Up ? -1 : 1), 0, options.Count - 1);
            if (highlighted < firstVisible)
            {
                firstVisible = highlighted;
            }
            else if (highlighted >= firstVisible + visibleCount)
            {
                firstVisible = highlighted - visibleCount + 1;
            }
        }
    }

    private void Select()
    {
        if (highlighted != get())
        {
            set(highlighted);
        }
        Close();
    }

    internal override void Draw(UiCanvas canvas)
    {
        int labelWidth = string.IsNullOrEmpty(label) ? 0 : Math.Min(130, Bounds.Width / 3);
        if (labelWidth > 0)
        {
            canvas.TextFit(label, new Rectangle(Bounds.X + 8, Bounds.Y, labelWidth - 12, Height), UiCanvas.Muted);
        }
        Rectangle value = new(Bounds.X + labelWidth, Bounds.Y, Bounds.Width - labelWidth, Height);
        canvas.Rounded(value, Hovered || IsOpen ? UiCanvas.Raised : UiCanvas.Surface, 4);
        canvas.TextFit(options[Math.Clamp(get(), 0, options.Count - 1)],
            new Rectangle(value.X + 8, value.Y, value.Width - 32, Height), UiCanvas.Ink);
        Vector2 origin = new(value.Right - 18, value.Y + 12);
        canvas.Line(origin, origin + new Vector2(4, 4), UiCanvas.Muted);
        canvas.Line(origin + new Vector2(4, 4), origin + new Vector2(8, 0), UiCanvas.Muted);
        DrawFocus(canvas);
    }

    internal void DrawPopup(UiCanvas canvas)
    {
        if (!IsOpen)
        {
            return;
        }
        canvas.Glass(PopupBounds, 6);
        // Menus need enough opacity to separate overlapping inspector labels.
        canvas.Rounded(PopupBounds, UiCanvas.Background * 0.94f, 6);
        canvas.RoundedBorder(PopupBounds, UiCanvas.Muted * 0.4f, 6);
        int selected = Math.Clamp(get(), 0, options.Count - 1);
        for (int index = firstVisible; index < firstVisible + visibleCount; index++)
        {
            Rectangle row = OptionBounds(index);
            if (index == highlighted)
            {
                canvas.Rounded(row, UiCanvas.Accent * 0.15f, 3);
            }
            if (index == selected)
            {
                canvas.Rounded(new Rectangle(row.X + 7, row.Y + 11, 4, 4), UiCanvas.Accent, 2);
            }
            canvas.TextFit(options[index], new Rectangle(row.X + 20, row.Y, row.Width - 30, row.Height),
                index == selected ? UiCanvas.Accent : UiCanvas.Ink);
        }
        if (options.Count > visibleCount)
        {
            int height = Math.Max(12, (PopupBounds.Height - 8) * visibleCount / options.Count);
            int offset = (PopupBounds.Height - 8 - height) * firstVisible / (options.Count - visibleCount);
            canvas.Rounded(new Rectangle(PopupBounds.Right - 5, PopupBounds.Y + 4 + offset, 2, height), UiCanvas.Muted * 0.6f, 1);
        }
    }
}
