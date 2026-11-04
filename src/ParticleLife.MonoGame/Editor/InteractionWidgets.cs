using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ParticleLife.Core.Models;
using ParticleLife.Viewer;

namespace ParticleLife.MonoGame.Editor;

/// <summary>Draws a paged directed matrix without copying its immutable values during a frame.</summary>
internal sealed class InteractionMatrixWidget(ExperimentEditor editor) : UiWidget(
    "Rows receive; columns are sources. Select a cell, then edit its coefficient below. Arrow keys move selection. Page buttons expose larger matrices.")
{
    private int firstReceiver;
    private int firstSource;
    private const int Cell = 32;
    internal int Receiver { get; private set; }
    internal int Source { get; private set; }
    private int Count => editor.Configuration.Matrix.SpeciesCount;
    private int Visible => Math.Min(9, Math.Max(2, (Bounds.Width - 42) / Cell));
    internal override int Height => 62 + Math.Min(Count, Visible) * Cell;

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
        if (point.Y >= Bounds.Bottom - 26)
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
        canvas.Text("RECEIVER ROW / SOURCE COLUMN ->", Bounds.X, Bounds.Y + 1, UiCanvas.Muted, size: 1);
        int rows = Math.Min(Visible, Count - firstReceiver);
        int columns = Math.Min(Visible, Count - firstSource);
        for (int column = 0; column < columns; column++)
        {
            int species = firstSource + column;
            canvas.Text(species.ToString(), Bounds.X + 42 + column * Cell, Bounds.Y + 17, Colour(species), size: 1.3f);
        }
        for (int row = 0; row < rows; row++)
        {
            int receiver = firstReceiver + row;
            canvas.Text(receiver.ToString(), Bounds.X + 8, Bounds.Y + 43 + row * Cell, Colour(receiver), size: 1.3f);
            for (int column = 0; column < columns; column++)
            {
                int source = firstSource + column;
                double coefficient = editor.Configuration.Matrix[receiver, source];
                Rectangle cell = new(Bounds.X + 38 + column * Cell, Bounds.Y + 32 + row * Cell, Cell - 2, Cell - 2);
                Color tint = coefficient >= 0 ? UiCanvas.Accent : UiCanvas.Error;
                canvas.Fill(cell, Color.Lerp(UiCanvas.Surface, tint, (float)(0.12 + 0.65 * Math.Abs(coefficient))));
                string value = Math.Abs(coefficient) < 0.005 ? "0" : coefficient.ToString("+.0;-.0");
                canvas.Text(value, cell.X + 2, cell.Y + 12, UiCanvas.Ink, size: 1);
                if (receiver == Receiver && source == Source)
                {
                    canvas.Border(cell, Color.White, 2);
                }
            }
        }
        int width = Bounds.Width / 4;
        string[] labels = ["ROWS <", "ROWS >", "COLS <", "COLS >"];
        for (int index = 0; index < labels.Length; index++)
        {
            Rectangle button = new(Bounds.X + index * width, Bounds.Bottom - 24, width - 4, 24);
            canvas.Fill(button, UiCanvas.Surface);
            canvas.Text(labels[index], button.X + 5, button.Y + 8, Count > Visible ? UiCanvas.Ink : UiCanvas.Muted, size: 1.2f);
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
internal sealed class ForceGraph(ExperimentEditor editor, InteractionMatrixWidget selection)
{
    internal bool ShowCoefficient { get; set; }
    internal bool ShowReverse { get; set; } = true;

    internal void Draw(UiCanvas canvas, Rectangle bounds)
    {
        canvas.Fill(bounds, UiCanvas.Background);
        canvas.Border(bounds, UiCanvas.Raised);
        ModelParameters parameters = editor.Configuration.Parameters;
        int receiver = Math.Min(selection.Receiver, editor.Configuration.Matrix.SpeciesCount - 1);
        int source = Math.Min(selection.Source, editor.Configuration.Matrix.SpeciesCount - 1);
        double attraction = editor.Configuration.Matrix[receiver, source];
        double reverse = editor.Configuration.Matrix[source, receiver];
        string unit = ShowCoefficient ? "G (DIMENSIONLESS)" : "F0*G (REDUCED FORCE)";
        canvas.Text($"PAIR {receiver} <- {source}   {unit}", bounds.X + 12, bounds.Y + 10, UiCanvas.Ink, (bounds.Width - 20) / 9);
        Rectangle plot = new(bounds.X + 66, bounds.Y + 35, Math.Max(40, bounds.Width - 90), Math.Max(30, bounds.Height - 92));
        int zero = plot.Y + plot.Height / 2;
        int cutoff = plot.X + (int)(plot.Width / 1.15);
        int core = plot.X + (int)(plot.Width * parameters.RepulsionFraction / 1.15);
        canvas.Fill(new Rectangle(plot.X, plot.Y, Math.Max(1, core - plot.X), plot.Height), new Color(51, 31, 39));
        canvas.Line(new Vector2(plot.X, zero), new Vector2(plot.Right, zero), UiCanvas.Muted * 0.6f);
        canvas.Line(new Vector2(plot.X, plot.Y), new Vector2(plot.X, plot.Bottom), UiCanvas.Muted * 0.6f);
        canvas.Line(new Vector2(cutoff, plot.Y), new Vector2(cutoff, plot.Bottom), UiCanvas.Muted * 0.5f);
        double scale = ShowCoefficient ? 1 : parameters.ForceScale;
        canvas.Text($"+{scale:G3}", bounds.X + 4, plot.Y - 2, UiCanvas.Muted, 9, 1);
        canvas.Text("0", bounds.X + 48, zero - 3, UiCanvas.Muted, size: 1);
        canvas.Text($"-{scale:G3}", bounds.X + 4, plot.Bottom - 4, UiCanvas.Muted, 9, 1);
        DrawCurve(attraction, UiCanvas.Accent, false);
        if (ShowReverse && source != receiver)
        {
            DrawCurve(reverse, new Color(245, 203, 100), true);
        }
        canvas.Fill(new Rectangle(plot.X - 2, zero - 2, 5, 5), UiCanvas.Accent);
        if (ShowCoefficient || parameters.ForceScale > 0)
        {
            Rectangle limit = new(plot.X - 3, plot.Bottom - 6, 7, 7);
            canvas.Fill(limit, UiCanvas.Background);
            canvas.Border(limit, UiCanvas.Accent);
        }
        canvas.Text("0", plot.X - 3, plot.Bottom + 8, UiCanvas.Muted, size: 1);
        canvas.Text($"R={parameters.Cutoff:G4}", cutoff - 24, plot.Bottom + 8, UiCanvas.Muted, 14, 1);
        canvas.Text("DISTANCE [REDUCED LENGTH]", plot.X, plot.Bottom + 22, UiCanvas.Muted, size: 1);
        canvas.Text("+ ATTRACTS / - REPELS; OVERLAP = 0", bounds.X + 12, bounds.Bottom - 24, UiCanvas.Muted, (bounds.Width - 24) / 6, 1);
        canvas.Text("PHENOMENOLOGICAL PAIR LAW, NOT POTENTIAL ENERGY", bounds.X + 12, bounds.Bottom - 12, UiCanvas.Muted, (bounds.Width - 24) / 6, 1);

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
    internal override int Height => 210;
    internal override void Draw(UiCanvas canvas) => graph.Draw(canvas, Bounds);
}
