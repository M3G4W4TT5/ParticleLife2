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
        EnsureVisible();
    }

    internal void Synchronize()
    {
        Receiver = Math.Min(Receiver, Count - 1);
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

    internal override void Press(Point point)
    {
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
        if (point.Y >= Bounds.Y + 32 && point.X >= Bounds.X + 38 && row < Visible && column < Visible
            && firstReceiver + row < Count && firstSource + column < Count)
        {
            Select(firstReceiver + row, firstSource + column);
        }
    }

    internal override void Key(Keys key, bool control, bool shift)
    {
        int row = Receiver + (key == Keys.Down ? 1 : key == Keys.Up ? -1 : 0);
        int column = Source + (key == Keys.Right ? 1 : key == Keys.Left ? -1 : 0);
        Select(Math.Clamp(row, 0, Count - 1), Math.Clamp(column, 0, Count - 1));
    }

    internal override void Draw(UiCanvas canvas)
    {
        Synchronize();
        canvas.Text("Receiver rows / source columns", Bounds.X + 2, Bounds.Y, UiCanvas.Muted, size: 1.25f);
        int rows = Math.Min(Visible, Count - firstReceiver);
        int columns = Math.Min(Visible, Count - firstSource);
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
                double coefficient = editor.Configuration.Matrix[receiver, source];
                Rectangle cell = new(Bounds.X + 38 + column * Cell, Bounds.Y + 32 + row * Cell, Cell - 2, Cell - 2);
                Color tint = coefficient >= 0 ? UiCanvas.Accent : UiCanvas.Error;
                canvas.Rounded(cell, Color.Lerp(UiCanvas.Surface, tint, (float)(0.08 + 0.48 * Math.Abs(coefficient))), 3);
                string value = Math.Abs(coefficient) < 0.005 ? "0" : coefficient.ToString("+.0;-.0");
                canvas.TextCentered(value, cell, UiCanvas.Ink, size: 1.2f);
                if (receiver == Receiver && source == Source)
                {
                    canvas.RoundedBorder(cell, UiCanvas.Ink, 3);
                }
            }
        }
        if (Count > Visible)
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
        ModelParameters parameters = editor.Configuration.Parameters;
        int receiver = Math.Min(selection.Receiver, editor.Configuration.Matrix.SpeciesCount - 1);
        int source = Math.Min(selection.Source, editor.Configuration.Matrix.SpeciesCount - 1);
        double attraction = editor.Configuration.Matrix[receiver, source];
        double reverse = editor.Configuration.Matrix[source, receiver];
        string unit = ShowCoefficient ? "g [dimensionless]" : "F0 g [reduced force]";
        canvas.TextFit($"Pair {receiver} <- {source}   {unit}", new Rectangle(bounds.X + 12, bounds.Y + 9, bounds.Width - 24, 18), UiCanvas.Ink, size: 1.35f);
        Rectangle plot = new(bounds.X + 51, bounds.Y + 34, Math.Max(40, bounds.Width - 70), Math.Max(30, bounds.Height - 84));
        int zero = plot.Y + plot.Height / 2;
        int cutoff = plot.X + (int)(plot.Width / 1.15);
        int core = plot.X + (int)(plot.Width * parameters.RepulsionFraction / 1.15);
        canvas.Fill(new Rectangle(plot.X, plot.Y, Math.Max(1, core - plot.X), plot.Height), UiCanvas.Error * 0.07f);
        canvas.Line(new Vector2(plot.X, zero), new Vector2(plot.Right, zero), UiCanvas.Muted * 0.6f);
        canvas.Line(new Vector2(plot.X, plot.Y), new Vector2(plot.X, plot.Bottom), UiCanvas.Muted * 0.6f);
        canvas.Line(new Vector2(cutoff, plot.Y), new Vector2(cutoff, plot.Bottom), UiCanvas.Muted * 0.5f);
        double scale = ShowCoefficient ? 1 : parameters.ForceScale;
        canvas.TextFit($"+{scale:G3}", new Rectangle(bounds.X + 5, plot.Y - 5, 40, 14), UiCanvas.Muted, size: 1.15f);
        canvas.TextRight("0", plot.X - 8, zero - 7, UiCanvas.Muted, size: 1.15f);
        canvas.TextFit($"-{scale:G3}", new Rectangle(bounds.X + 5, plot.Bottom - 9, 40, 14), UiCanvas.Muted, size: 1.15f);
        DrawCurve(attraction, UiCanvas.Accent, false);
        if (ShowReverse && source != receiver)
        {
            DrawCurve(reverse, new Color(245, 203, 100), true);
        }
        canvas.Rounded(new Rectangle(plot.X - 2, zero - 2, 5, 5), UiCanvas.Accent, 2);
        if (ShowCoefficient || parameters.ForceScale > 0)
        {
            Rectangle limit = new(plot.X - 3, plot.Bottom - 6, 7, 7);
            canvas.Rounded(limit, UiCanvas.Background, 3);
            canvas.RoundedBorder(limit, UiCanvas.Accent, 3);
        }
        canvas.Text("0", plot.X - 3, plot.Bottom + 5, UiCanvas.Muted, size: 1.15f);
        canvas.TextRight($"R = {parameters.Cutoff:G4}", plot.Right, plot.Bottom + 5, UiCanvas.Muted, size: 1.15f);
        canvas.TextCentered("Distance [reduced length]", new Rectangle(plot.X + 10, plot.Bottom + 5, Math.Max(1, plot.Width - 100), 15), UiCanvas.Muted, size: 1.1f);
        canvas.TextFit("Attraction + / repulsion -   |   Overlap = 0", new Rectangle(bounds.X + 12, bounds.Bottom - 21, bounds.Width - 24, 15),
            UiCanvas.Muted, size: 1.2f);

        void DrawCurve(double coefficient, Color color, bool dashed)
        {
            // The production curve is piecewise linear. Sampling its exact knots preserves
            // arbitrarily narrow valid cores and outer triangles; uniform samples do not.
            Span<double> ratios = stackalloc double[5] { 0, parameters.RepulsionFraction,
                (1 + parameters.RepulsionFraction) / 2, 1, 1.15 };
            Vector2 previous = default;
            for (int index = 0; index < ratios.Length; index++)
            {
                double ratio = ratios[index];
                // At q=0 the separate filled point is zero. This open branch begins at
                // its analytical right-hand limit, including subnormal beta values.
                double radial = index == 0 ? -1 : ParticleLifeForce.RadialCoefficient(ratio, coefficient, parameters.RepulsionFraction);
                double normalized = !ShowCoefficient && parameters.ForceScale == 0 ? 0 : radial;
                Vector2 current = new(plot.X + (float)(plot.Width * ratio / 1.15),
                    zero - (float)normalized * (plot.Height / 2f - 3));
                if (index > 0)
                {
                    if (dashed)
                    {
                        float length = Vector2.Distance(previous, current);
                        for (float distance = 0; distance < length; distance += 10)
                        {
                            canvas.Line(Vector2.Lerp(previous, current, distance / length),
                                Vector2.Lerp(previous, current, Math.Min(distance + 5, length) / length), color, 1.5f);
                        }
                    }
                    else
                    {
                        canvas.Line(previous, current, color, 1.5f);
                    }
                }
                previous = current;
            }
        }
    }
}

/// <summary>Keeps the force graph accessible inside the scrollable panel on compact windows.</summary>
internal sealed class ForceGraphWidget(ForceGraph graph) : UiWidget
{
    internal override bool Focusable => false;
    internal override int Height => 184;
    internal override void Draw(UiCanvas canvas) => graph.Draw(canvas, Bounds);
}
