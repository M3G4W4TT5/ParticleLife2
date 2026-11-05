using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ParticleLife.Core.Models;
using ParticleLife.Viewer;

namespace ParticleLife.MonoGame.Editor;

/// <summary>Draws a paged directed matrix without copying its immutable values during a frame.</summary>
internal sealed class InteractionMatrixWidget(EditorSession editor) : UiWidget(
    "Rows receive; columns are sources. Select a cell, then edit its coefficient below. Arrow keys move selection. Page buttons expose larger matrices.")
{
    internal InteractionMatrixWidget(ExperimentEditor editor) : this(new EditorSession(editor)) { }
    private int firstReceiver;
    private int firstSource;
    private const int Cell = 28;
    internal MatrixSelection Selection { get; } = new(editor.Configuration.Matrix.SpeciesCount);
    internal Action<int>? ColourRequested { get; set; }
    internal Action? CopyRequested { get; set; }
    internal Action? PasteRequested { get; set; }
    private bool draggingCells;
    private bool additiveDrag;
    private Point previousDragPoint;
    internal int Receiver { get; private set; }
    internal int Source { get; private set; }
    private int Count => editor.Configuration.Matrix.SpeciesCount;
    private int Visible => Math.Min(9, Math.Max(2, (Bounds.Width - 42) / Cell));
    internal override int Height => 34 + Math.Min(Count, Visible) * Cell + (Count > Visible ? 28 : 0);

    internal void Select(int receiver, int source)
    {
        if (receiver < 0 || receiver >= Count || source < 0 || source >= Count)
        {
            throw new ArgumentOutOfRangeException(nameof(receiver), $"Species must be in 0..{Count - 1}.");
        }
        Receiver = receiver;
        Source = source;
        Selection.Select(receiver, source);
        EnsureVisible();
    }

    internal void Synchronize()
    {
        Receiver = Math.Min(Receiver, Count - 1);
        Selection.Resize(Count);
        Source = Math.Min(Source, Count - 1);
        firstReceiver = Math.Min(firstReceiver, Math.Max(0, Count - Visible));
        firstSource = Math.Min(firstSource, Math.Max(0, Count - Visible));
    }

    private void EnsureVisible()
    {
        if (Receiver < firstReceiver || Receiver >= firstReceiver + Visible)
        {
            firstReceiver = Math.Clamp(Receiver - Visible / 2, 0, Math.Max(0, Count - Visible));
        }
        if (Source < firstSource || Source >= firstSource + Visible)
        {
            firstSource = Math.Clamp(Source - Visible / 2, 0, Math.Max(0, Count - Visible));
        }
    }

    internal override void Press(Point point) => Press(point, false, false);

    internal override void Press(Point point, bool control, bool shift)
    {
        draggingCells = false;
        if (Count > Visible && point.Y >= Bounds.Bottom - 26)
        {
            int part = Math.Clamp((point.X - Bounds.X) * 4 / Bounds.Width, 0, 3);
            int maximum = Math.Max(0, Count - Visible);
            if (part < 2)
            {
                firstReceiver = Math.Clamp(firstReceiver + (part == 0 ? -Visible : Visible), 0, maximum);
            }
            else
            {
                firstSource = Math.Clamp(firstSource + (part == 2 ? -Visible : Visible), 0, maximum);
            }
            return;
        }
        int row = (point.Y - Bounds.Y - 32) / Cell;
        int column = (point.X - Bounds.X - 38) / Cell;
        if (point.Y >= Bounds.Y + 15 && point.Y < Bounds.Y + 32 && point.X >= Bounds.X + 38
            && column < Visible && firstSource + column < Count)
        {
            Source = firstSource + column;
            if (shift) { ColourRequested?.Invoke(Source); }
            else { Selection.SelectColumn(Source, control); }
            return;
        }
        if (point.X >= Bounds.X && point.X < Bounds.X + 32 && point.Y >= Bounds.Y + 32
            && row < Visible && firstReceiver + row < Count)
        {
            Receiver = firstReceiver + row;
            if (shift) { ColourRequested?.Invoke(Receiver); }
            else { Selection.SelectRow(Receiver, control); }
            return;
        }
        if (point.Y >= Bounds.Y + 32 && point.X >= Bounds.X + 38 && row < Visible && column < Visible
            && firstReceiver + row < Count && firstSource + column < Count)
        {
            Receiver = firstReceiver + row;
            Source = firstSource + column;
            Selection.Select(Receiver, Source, shift, control);
            draggingCells = true;
            additiveDrag = control;
            previousDragPoint = point;
            EnsureVisible();
        }
    }

    internal override void Drag(Point point)
    {
        if (!draggingCells || point == previousDragPoint) { return; }
        previousDragPoint = point;
        int row = Math.Clamp((point.Y - Bounds.Y - 32) / Cell, 0, Math.Min(Visible, Count - firstReceiver) - 1);
        int column = Math.Clamp((point.X - Bounds.X - 38) / Cell, 0, Math.Min(Visible, Count - firstSource) - 1);
        Receiver = firstReceiver + row; Source = firstSource + column;
        Selection.Select(Receiver, Source, extend: true, additive: additiveDrag);
    }

    internal override void Release() => draggingCells = false;

    internal override void Key(Keys key, bool control, bool shift)
    {
        if (control && key == Keys.A) { Selection.SelectAll(); return; }
        if (control && key == Keys.C) { CopyRequested?.Invoke(); return; }
        if (control && key == Keys.V) { PasteRequested?.Invoke(); return; }
        if (control && key == Keys.Z) { editor.Undo(); return; }
        if (control && key == Keys.Y) { editor.Redo(); return; }
        if (key is not (Keys.Up or Keys.Down or Keys.Left or Keys.Right)) { return; }
        int row = Receiver + (key == Keys.Down ? 1 : key == Keys.Up ? -1 : 0);
        int column = Source + (key == Keys.Right ? 1 : key == Keys.Left ? -1 : 0);
        Receiver = Math.Clamp(row, 0, Count - 1); Source = Math.Clamp(column, 0, Count - 1);
        Selection.Select(Receiver, Source, shift, control); EnsureVisible();
    }

    internal override void Draw(UiCanvas canvas)
    {
        InteractionMatrix current = editor.Configuration.Matrix;
        int count = current.SpeciesCount;
        Receiver = Math.Min(Receiver, count - 1); Source = Math.Min(Source, count - 1);
        Selection.Resize(count);
        int visible = Math.Min(9, Math.Max(2, (Bounds.Width - 42) / Cell));
        firstReceiver = Math.Min(firstReceiver, Math.Max(0, count - visible));
        firstSource = Math.Min(firstSource, Math.Max(0, count - visible));
        canvas.Text("Receiver rows / source columns", Bounds.X + 2, Bounds.Y, UiCanvas.Muted, size: 1.25f);
        int rows = Math.Min(visible, count - firstReceiver);
        int columns = Math.Min(visible, count - firstSource);
        for (int column = 0; column < columns; column++)
        {
            int species = firstSource + column;
            canvas.TextCentered(species.ToString(), new Rectangle(Bounds.X + 38 + column * Cell, Bounds.Y + 15, Cell - 2, 17),
                Colour(species), size: 1.3f);
        }
        for (int row = 0; row < rows; row++)
        {
            int receiver = firstReceiver + row;
            canvas.TextCentered(receiver.ToString(), new Rectangle(Bounds.X, Bounds.Y + 32 + row * Cell, 32, Cell - 2),
                Colour(receiver), size: 1.3f);
            for (int column = 0; column < columns; column++)
            {
                int source = firstSource + column;
                double coefficient = current[receiver, source];
                Rectangle cell = new(Bounds.X + 38 + column * Cell, Bounds.Y + 32 + row * Cell, Cell - 2, Cell - 2);
                Color tint = coefficient >= 0 ? UiCanvas.Accent : UiCanvas.Error;
                canvas.Rounded(cell, Color.Lerp(UiCanvas.Surface, tint, (float)(0.08 + 0.48 * Math.Abs(coefficient))), 3);
                string value = Math.Abs(coefficient) < 0.005 ? "0" : coefficient.ToString("+.0;-.0");
                canvas.TextCentered(value, cell, UiCanvas.Ink, size: 1.2f);
                if (Selection.Cells.Contains(new MatrixCell(receiver, source)))
                {
                    canvas.RoundedBorder(cell, UiCanvas.Ink, 3);
                }
            }
        }
        if (count > visible)
        {
            int width = Bounds.Width / 4;
            string[] labels = ["Rows <", "Rows >", "Cols <", "Cols >"];
            for (int index = 0; index < labels.Length; index++)
            {
                Rectangle button = new(Bounds.X + index * width, Bounds.Bottom - 24, width - 4, 24);
                canvas.Rounded(button, UiCanvas.Surface, 4);
                canvas.TextCentered(labels[index], button, UiCanvas.Muted, size: 1.25f);
            }
        }
        DrawFocus(canvas);
    }

    private Color Colour(int species)
    {
        uint rgb = editor.Appearance.ColorForSpecies(species);
        return new Color((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}

/// <summary>Plots the production radial coefficient, optionally scaled to reduced force units.</summary>
/// <remarks>The exact overlap point is zero; the open-point limit is shown separately instead of
/// connecting it to the repulsive branch. The plot is a phenomenological pair law, never potential energy.</remarks>
internal sealed class ForceGraph(EditorSession editor, InteractionMatrixWidget selection)
{
    internal bool ShowCoefficient { get; set; }
    internal bool ShowReverse { get; set; } = true;

    internal void Draw(UiCanvas canvas, Rectangle bounds)
    {
        canvas.Glass(bounds);
        var configuration = editor.Configuration;
        int receiver = Math.Min(selection.Receiver, configuration.Matrix.SpeciesCount - 1);
        int source = Math.Min(selection.Source, configuration.Matrix.SpeciesCount - 1);
        DirectedForceCurve forward = new(configuration, receiver, source);
        DirectedForceCurve reverse = new(configuration, source, receiver);
        bool showReverse = ShowReverse && source != receiver;
        double maximum = Math.Max(forward.Cutoff, showReverse ? reverse.Cutoff : 0);
        double axis = maximum * 1.15;
        string unit = ShowCoefficient ? "g [dimensionless]" : "F0 g [reduced force]";
        canvas.TextFit($"{receiver} <- {source}   {unit}", new Rectangle(bounds.X + 12, bounds.Y + 9, bounds.Width - 24, 18), UiCanvas.Ink, 1.35f);
        Rectangle plot = new(bounds.X + 47, bounds.Y + 34, Math.Max(40, bounds.Width - 63), Math.Max(32, bounds.Height - 125));
        int zero = plot.Y + plot.Height / 2;
        int coreEnd = plot.X + (int)(plot.Width * (forward.Core / axis));
        canvas.Fill(new Rectangle(plot.X, plot.Y, Math.Max(0, coreEnd - plot.X), plot.Height), UiCanvas.Error * 0.08f);
        canvas.Line(new Vector2(plot.X, zero), new Vector2(plot.Right, zero), UiCanvas.Muted * 0.6f);
        canvas.Line(new Vector2(plot.X, plot.Y), new Vector2(plot.X, plot.Bottom), UiCanvas.Muted * 0.6f);
        double scale = ShowCoefficient ? 1 : configuration.Parameters.ForceScale;
        canvas.TextFit($"+{scale:G3}", new Rectangle(bounds.X + 3, plot.Y - 5, 40, 14), UiCanvas.Muted, 1.1f);
        canvas.TextFit($"-{scale:G3}", new Rectangle(bounds.X + 3, plot.Bottom - 10, 40, 14), UiCanvas.Muted, 1.1f);
        DrawCurve(forward, UiCanvas.Accent, false);
        if (showReverse) { DrawCurve(reverse, new Color(245, 203, 100), true); }
        canvas.Rounded(new Rectangle(plot.X - 2, zero - 2, 5, 5), UiCanvas.Accent, 2);
        canvas.Text("0", plot.X - 3, plot.Bottom + 4, UiCanvas.Muted, size: 1.1f);
        canvas.TextRight($"{axis:G5} L", plot.Right, plot.Bottom + 4, UiCanvas.Muted, size: 1.1f);
        DrawRadii(forward, UiCanvas.Accent, plot.Bottom + 26, $"{receiver} <- {source}");
        if (showReverse) { DrawRadii(reverse, new Color(245, 203, 100), plot.Bottom + 48, $"{source} <- {receiver}"); }
        canvas.TextFit("Overlap = 0; zero core removes core repulsion", new Rectangle(bounds.X + 9, bounds.Bottom - 21, bounds.Width - 18, 16), UiCanvas.Muted, 1.15f);

        void DrawRadii(DirectedForceCurve curve, Color colour, int y, string label)
        {
            int c = (int)(plot.Width * (curve.Core / axis));
            int r = Math.Max(1, (int)(plot.Width * (curve.Cutoff / axis)));
            canvas.Fill(new Rectangle(plot.X, y + 12, r, 4), colour * 0.3f);
            canvas.Fill(new Rectangle(plot.X, y + 12, c, 4), UiCanvas.Error * 0.65f);
            canvas.TextFit($"{label}: c {curve.Core:G5}, R {curve.Cutoff:G5}", new Rectangle(bounds.X + 9, y - 3, bounds.Width - 18, 15), colour, 1.1f);
        }
        void DrawCurve(DirectedForceCurve curve, Color colour, bool dashed)
        {
            // Physical knots retain each direction's own core/cutoff on one common distance axis.
            Span<double> knots = stackalloc double[5] { 0, curve.Core, curve.Peak, curve.Cutoff, axis };
            Vector2 previous = default;
            for (int index = 0; index < knots.Length; index++)
            {
                double radial = index == 0 && curve.Core > 0 ? -1 : curve.Evaluate(knots[index]);
                if (!ShowCoefficient && scale == 0) { radial = 0; }
                Vector2 point = new(plot.X + (float)(plot.Width * (knots[index] / axis)), zero - (float)radial * (plot.Height / 2f - 3));
                if (index == 0 && curve.Core > 0 && (ShowCoefficient || scale > 0))
                {
                    Rectangle open = new((int)point.X - 3, (int)point.Y - 3, 6, 6);
                    canvas.Rounded(open, UiCanvas.Background, 3); canvas.RoundedBorder(open, colour, 3);
                }
                if (index > 0)
                {
                    float length = Vector2.Distance(previous, point);
                    if (!dashed) { canvas.Line(previous, point, colour, 1.5f); }
                    else
                    {
                        for (float distance = 0; distance < length; distance += 10)
                        {
                            canvas.Line(Vector2.Lerp(previous, point, distance / length), Vector2.Lerp(previous, point, Math.Min(distance + 5, length) / length), colour, 1.5f);
                        }
                    }
                }
                previous = point;
            }
        }
    }
}

/// <summary>Keeps the force graph accessible inside the scrollable panel on compact windows.</summary>
internal sealed class ForceGraphWidget(ForceGraph graph) : UiWidget
{
    internal override bool Focusable => false;
    internal override int Height => 250;
    internal override void Draw(UiCanvas canvas) => graph.Draw(canvas, Bounds);
}
