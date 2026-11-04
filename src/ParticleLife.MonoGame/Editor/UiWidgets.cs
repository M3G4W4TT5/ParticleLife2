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
    internal virtual int Height => 34;
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
            canvas.Border(Bounds, UiCanvas.Accent);
        }
    }
}

internal sealed class UiButton(Func<string> label, Action clicked, string help = "", Func<bool>? enabled = null) : UiWidget(help)
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
        canvas.Fill(Bounds, Hovered && active ? UiCanvas.Raised : UiCanvas.Surface);
        canvas.Text(label(), Bounds.X + 10, Bounds.Y + 11, active ? UiCanvas.Ink : UiCanvas.Muted,
            Math.Max(1, (Bounds.Width - 16) / 9));
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

    internal override void Draw(UiCanvas canvas)
    {
        canvas.Fill(Bounds, Hovered ? UiCanvas.Raised : UiCanvas.Surface);
        canvas.Fill(new Rectangle(Bounds.X + 10, Bounds.Y + 9, 16, 16), get() ? UiCanvas.Accent : UiCanvas.Background);
        canvas.Text(label, Bounds.X + 36, Bounds.Y + 11, UiCanvas.Ink, Math.Max(1, (Bounds.Width - 44) / 9));
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
    internal override int Height => error is null ? 58 : 78;
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
        canvas.Text(label, Bounds.X, Bounds.Y + 2, UiCanvas.Muted);
        Rectangle box = new(Bounds.X, Bounds.Y + 21, Bounds.Width, 30);
        canvas.Fill(box, UiCanvas.Background);
        canvas.Border(box, error is not null ? UiCanvas.Error : Focused ? UiCanvas.Accent : UiCanvas.Raised);
        string text = Draft;
        int capacity = Math.Max(1, (box.Width - 16) / 9);
        int offset = Focused && !selected ? Math.Clamp(cursor - capacity + 1, 0, Math.Max(0, text.Length - capacity)) : 0;
        if (text.Length > capacity)
        {
            text = text.Substring(offset, capacity);
        }
        if (Focused && selected)
        {
            canvas.Fill(new Rectangle(box.X + 6, box.Y + 5, Math.Min(box.Width - 12, text.Length * 9), 20), UiCanvas.Raised);
        }
        canvas.Text(text, box.X + 8, box.Y + 10, UiCanvas.Ink, capacity);
        if (Focused && !selected)
        {
            canvas.Fill(new Rectangle(box.X + 8 + (cursor - offset) * 9, box.Y + 6, 1, 18), UiCanvas.Accent);
        }
        if (error is not null)
        {
            canvas.Text(error, Bounds.X, Bounds.Y + 58, UiCanvas.Error, Math.Max(1, Bounds.Width / 9));
        }
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

    internal override int Height => error is null ? 68 : 86;
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
    private Rectangle Entry => new(Bounds.X + Bounds.Width - 186, Bounds.Y + 1, 146, 28);
    private Rectangle Reset => new(Bounds.Right - 34, Bounds.Y + 1, 34, 28);
    private Rectangle Slider => new(Bounds.X + 6, Bounds.Y + 39, Bounds.Width - 12, 18);

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
        if (Reset.Contains(point))
        {
            editing = false;
            set(reset);
            error = null;
            dirty = false;
        }
        else if (point.Y >= Bounds.Y + 33)
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
        double fraction = Math.Clamp((double)(point.X - Slider.X) / Slider.Width, 0, 1);
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
        canvas.Text(label, Bounds.X, Bounds.Y + 9, UiCanvas.Ink, Math.Max(1, (Bounds.Width - 194) / 9));
        canvas.Fill(Entry, UiCanvas.Background);
        canvas.Border(Entry, error is not null ? UiCanvas.Error : Focused && editing ? UiCanvas.Accent : UiCanvas.Raised);
        string valueText = editing && Focused || dirty ? draft : get().ToString("G9", CultureInfo.InvariantCulture);
        if (Focused && editing && selected)
        {
            canvas.Fill(new Rectangle(Entry.X + 4, Entry.Y + 4, Math.Min(Entry.Width - 8, valueText.Length * 9), 20), UiCanvas.Raised);
        }
        int offset = Focused && editing && !selected ? Math.Clamp(cursor - 14, 0, Math.Max(0, valueText.Length - 15)) : 0;
        canvas.Text(valueText.Length > 15 ? valueText.Substring(offset, 15) : valueText, Entry.X + 6, Entry.Y + 9, UiCanvas.Ink, 15);
        if (Focused && editing && !selected)
        {
            canvas.Fill(new Rectangle(Entry.X + 6 + (cursor - offset) * 9, Entry.Y + 5, 1, 18), UiCanvas.Accent);
        }
        canvas.Fill(Reset, UiCanvas.Surface);
        canvas.Text("R", Reset.X + 12, Reset.Y + 9, UiCanvas.Muted);
        double value = get();
        double fraction = SliderFraction;
        Rectangle slider = Slider;
        canvas.Fill(new Rectangle(slider.X, slider.Y + 6, slider.Width, 4), UiCanvas.Raised);
        canvas.Fill(new Rectangle(slider.X, slider.Y + 6, (int)(slider.Width * fraction), 4), UiCanvas.Accent * 0.65f);
        canvas.Fill(new Rectangle(slider.X + (int)(slider.Width * fraction) - 4, slider.Y + 1, 8, 14), UiCanvas.Accent);
        if (value < minimum || value > Maximum)
        {
            canvas.Text("VALUE OUTSIDE SLIDER RANGE", Bounds.X, Bounds.Y + 57, UiCanvas.Muted, size: 1);
        }
        if (error is not null)
        {
            canvas.Text(error, Bounds.X, Bounds.Y + 68, UiCanvas.Error, Math.Max(1, Bounds.Width / 9));
        }
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

internal sealed class UiSection(string title, bool expanded = false) : UiWidget("Click or press Enter to expand/collapse. Scroll the panel; Tab moves between controls.")
{
    internal List<UiWidget> Children { get; } = [];
    internal bool Expanded { get; set; } = expanded;
    internal override int Height => 38;
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
        canvas.Fill(Bounds, Hovered ? UiCanvas.Raised : UiCanvas.Surface);
        canvas.Text((Expanded ? "- " : "+ ") + title, Bounds.X + 10, Bounds.Y + 13, UiCanvas.Accent);
        DrawFocus(canvas);
    }
}
