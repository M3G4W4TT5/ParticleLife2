using Microsoft.Xna.Framework;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;
using ParticleLife.Viewer;

namespace ParticleLife.MonoGame.Editor;

/// <summary>Retains exploration drafts; every live action is submitted to the completed-state owner.</summary>
internal sealed class ExplorationControls
{
    private readonly EditorSession editor;
    private readonly Action<string> report;
    private readonly InteractionMatrixWidget matrix;
    private readonly Func<bool> linkReverse;
    private BrushSettings brush = new();
    private int tool;
    private double bulkValue, bulkScale = 1;
    private int generationSpecies = -1;
    private SpeciesAssignmentKind assignment;
    private double assignmentFraction = 1;
    private int assignmentSpecies;
    private PaletteKind palette;
    internal bool BrushEnabled => tool > 0;
    internal BrushSettings Brush => brush;
    internal UiSection Tools { get; } = new("Canvas brushes", true);
    internal UiSection Camera { get; } = new("Camera", true);

    internal ExplorationControls(EditorSession editor, InteractionMatrixWidget matrix, Func<bool> linkReverse, Action<string> report)
    {
        this.editor = editor; this.matrix = matrix; this.linkReverse = linkReverse; this.report = report;
        BuildTools(); BuildCamera();
    }

    private UiNumber Number(string label, Func<double> get, Action<double> set, double initial, double min, double max,
        string help, bool integer = false, bool log = false) => new(label, get, set, initial, min, max, help,
            logarithmic: log, integral: integer, beginGesture: editor.BeginGesture, endGesture: editor.EndGesture);

    private UiButton Action(string label, Action<ExperimentEditor> apply, string help) => new(() => label, () => Queue(label, apply), help);
    private void Queue(string label, Action<ExperimentEditor> apply)
    {
        if (editor.IsBackground) { report($"{label} queued for a completed boundary."); }
        editor.Run(label, apply, () => report($"{label} applied at the completed boundary."));
    }
    private static int Integer(double value)
    {
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value < int.MinValue || value > int.MaxValue) { throw new ArgumentOutOfRangeException(nameof(value), "Use a whole number."); }
        return (int)value;
    }

    internal void AddMatrixTools(UiSection section)
    {
        matrix.CopyRequested = CopyMatrix;
        matrix.PasteRequested = PasteMatrix;
        section.Children.Insert(1, new UiLabel(() => $"{matrix.Selection.Count} directed cells selected. Ctrl toggles; Shift extends; headers select rows/columns. Shift+header edits colour.", 56));
        section.Children.Add(new UiRow(new UiButton(() => "Select all", matrix.Selection.SelectAll),
            new UiButton(() => "Row", () => matrix.Selection.SelectRow(matrix.Receiver)),
            new UiButton(() => "Column", () => matrix.Selection.SelectColumn(matrix.Source))));
        section.Children.Add(Number("Bulk coefficient", () => bulkValue, value => bulkValue = value, 0, -1, 1,
            "Draft for Set selected. Receiver rows / source columns; linked reverse includes the transposed cells."));
        section.Children.Add(Number("Bulk scale", () => bulkScale, value => bulkScale = value, 1, -3, 3,
            "Draft finite multiplier. Scale selected clamps each result to [-1,1]."));
        section.Children.Add(new UiRow(new UiButton(() => "Set selected live", () => Bulk(MatrixBulkOperation.Set)),
            new UiButton(() => "Scale selected live", () => Bulk(MatrixBulkOperation.Scale))));
        section.Children.Add(new UiRow(new UiButton(() => "Randomize selected live", () => Bulk(MatrixBulkOperation.Randomize)),
            new UiButton(() => "Zero selected live", () => Bulk(MatrixBulkOperation.Reset))));
        section.Children.Add(new UiRow(new UiButton(() => "Copy full matrix", CopyMatrix, "Copy exact G17 coefficients as TSV, receiver rows and source columns. Ctrl+C while matrix-focused."),
            new UiButton(() => "Paste full matrix live", PasteMatrix, "Validate a complete matching square TSV/CSV matrix before applying. Ctrl+V while matrix-focused. Ranges remain unchanged.")));
    }
    private void Bulk(MatrixBulkOperation operation)
    {
        MatrixCell[] selected = matrix.Selection.Cells.ToArray();
        bool reverse = linkReverse();
        double value = operation == MatrixBulkOperation.Scale ? bulkScale : bulkValue;
        int seed = editor.Generation.Seed;
        Queue($"{operation} selected coefficients live", owner => owner.ApplyMatrixCoefficients(
            MatrixEditing.Apply(owner.Configuration.Matrix, selected, operation, value, seed, reverse),
            $"{operation} selected coefficients", operation == MatrixBulkOperation.Randomize ? seed : null));
    }
    private void CopyMatrix()
    {
        NativeClipboard.Write(MatrixEditing.Export(editor.Configuration.Matrix));
        report("Copied complete matrix: receiver rows, source columns, exact values.");
    }
    private void PasteMatrix()
    {
        double[] coefficients = MatrixEditing.Import(NativeClipboard.Read(), editor.Configuration.Matrix.SpeciesCount);
        Queue("Paste complete coefficients live", owner => owner.ApplyMatrixCoefficients(coefficients, "Paste matrix coefficients"));
    }

    internal void AddPopulationTools(UiSection section, Action<int> selectSpecies, Action<int> colourSpecies)
    {
        section.Children.Insert(0, new PopulationBarsWidget(editor, selectSpecies, colourSpecies, (species, delta) =>
        {
            Queue("Adjust live species population", owner =>
            {
                int[] counts = owner.GetCurrentPopulations();
                counts[species] = Math.Max(0, checked(counts[species] + delta));
                owner.ApplyPopulation(counts.Length, counts);
            });
        }));
        section.Children.Insert(1, Action("Equalize live populations", owner =>
        {
            int[] counts = ExperimentEditor.DistributePopulation(owner.Playback.Observe().Count, owner.Configuration.Matrix.SpeciesCount);
            owner.ApplyPopulation(counts.Length, counts);
        }, "Redistribute targets equally while preserving every possible surviving identity and its current motion. Shortfalls use the generation recipe."));
    }

    internal void AddIndependentGeneration(UiSection section)
    {
        section.Children.Add(new UiLabel(() => "Independent live generators retain every other component. Future recipe choices above do not move particles.", 48));
        section.Children.Add(Number("Live generation species", () => generationSpecies, value =>
        {
            int species = Integer(value);
            if (species < -1 || species >= editor.Configuration.Matrix.SpeciesCount) { throw new ArgumentOutOfRangeException(nameof(value), "Use -1 for all, or an active species."); }
            generationSpecies = species;
        }, -1, -1, 16, "-1 regenerates all species; another index affects only that species. Actual generated results are recorded.", integer: true));
        section.Children.Add(new UiRow(new UiButton(() => "Positions only live", () =>
        {
            int species = generationSpecies; Queue("Regenerate live positions only", owner => owner.RegeneratePositions(species));
        }), new UiButton(() => "Velocities only live", () =>
        {
            int species = generationSpecies; Queue("Regenerate live velocities only", owner => owner.RegenerateVelocities(species));
        })));
        SpeciesAssignmentKind[] modes = Enum.GetValues<SpeciesAssignmentKind>();
        section.Children.Add(new UiDropdown("Live species operation", modes.Select(value => value.ToString()).ToArray(),
            () => (int)assignment, value => assignment = modes[value], "Random, equal, spatial bands, rotate, flip or selected species. Identity, positions and velocity remain unchanged."));
        section.Children.Add(Number("Affected fraction", () => assignmentFraction, value =>
        {
            if (value < 0 || value > 1) { throw new ArgumentOutOfRangeException(nameof(value)); }
            assignmentFraction = value;
        }, 1, 0, 1, "Exactly round(current count * fraction) seeded shuffled identities are considered. Future recipe counts stay independent."));
        section.Children.Add(Number("Assigned species", () => assignmentSpecies, value => assignmentSpecies = Integer(value), 0, 0, 31,
            "Target for SelectedSpecies. Must be an active species when applied.", integer: true));
        section.Children.Add(new UiButton(() => "Reassign species live", () =>
        {
            SpeciesAssignmentKind kind = assignment; double fraction = assignmentFraction; int species = assignmentSpecies;
            Queue("Reassign live species", owner => owner.ReassignSpecies(kind, fraction, species));
        }));
    }

    internal void AddPaletteTools(UiSection section)
    {
        PaletteKind[] choices = Enum.GetValues<PaletteKind>();
        section.Children.Insert(0, new UiDropdown("Palette recipe", choices.Select(value => value.ToString()).ToArray(),
            () => (int)palette, value => palette = choices[value], "Palette is presentation only; it never reassigns species or changes motion."));
        section.Children.Insert(1, new UiButton(() => "Apply palette to display", () =>
        {
            PaletteKind choice = palette; int seed = editor.Generation.Seed;
            Queue("Apply display palette", owner => owner.SetAppearance(owner.Appearance.WithPalette(
                PaletteCatalog.Create(choice, owner.Configuration.Matrix.SpeciesCount, seed))));
        }));
        section.Children.Insert(2, new PaletteSwatchesWidget(editor));
    }

    private void BuildCamera()
    {
        Camera.Children.Add(new UiLabel(() => "Wheel zooms at pointer. Middle/right drag pans. V fits. Camera never edits particles.", 44));
        Camera.Children.Add(Number("Camera zoom", () => editor.Appearance.Camera.Zoom,
            value => SetCamera(editor.Appearance.Camera with { Zoom = value }), 1, 0.02, 500, "Zoom relative to fitted world. Exact supported range 0.02..500.", log: true));
        Camera.Children.Add(Number("Camera centre X", () => editor.Appearance.Camera.CenterX,
            value => SetCamera(editor.Appearance.Camera with { CenterX = value }), 0.5, -1, 2, "Centre as a fraction of nominal world width. Off-world views are supported."));
        Camera.Children.Add(Number("Camera centre Y", () => editor.Appearance.Camera.CenterY,
            value => SetCamera(editor.Appearance.Camera with { CenterY = value }), 0.5, -1, 2, "Centre as a fraction of nominal world height."));
        Camera.Children.Add(new UiToggle("Smooth camera", () => editor.Appearance.Camera.Smooth,
            value => SetCamera(editor.Appearance.Camera with { Smooth = value }), "Ease the displayed camera towards the saved target. Pointer tools use the actually displayed inverse mapping."));
        Camera.Children.Add(new UiRow(new UiButton(() => "Fit world", Fit), new UiButton(() => "Reset camera", () => SetCamera(new CameraSettings()))));
        Camera.Children.Add(new UiDropdown("Particle size units", ["Screen pixels", "World units"],
            () => (int)editor.Appearance.ParticleSizeMode, value => editor.SetAppearance(editor.Appearance with { ParticleSizeMode = (ParticleSizeMode)value }),
            "Screen size remains constant while zooming. World size follows camera zoom. Neither affects interactions."));
        Camera.Children.Add(Number("World display radius [L]", () => editor.Appearance.WorldParticleRadius,
            value => editor.SetAppearance(editor.Appearance with { WorldParticleRadius = value }), 0.003, 0.0001, 0.1,
            "Presentation-only radius in reduced world units when World units is selected.", log: true));
    }
    internal void Fit() => SetCamera(editor.Appearance.Camera with { Zoom = 1, CenterX = 0.5, CenterY = 0.5 });
    private void SetCamera(CameraSettings camera) => editor.SetAppearance(editor.Appearance with { Camera = camera });

    private void BuildTools()
    {
        string[] labels = ["Navigate only", "Move", "Add particles", "Paint species", "Erase", "Attract", "Repel", "Directional force"];
        Tools.Children.Add(new UiDropdown("Left-drag tool", labels, () => tool, value =>
        {
            tool = value; if (value > 0) { brush = brush with { Kind = (BrushKind)(value - 1) }; }
        }, "Choose a live tool, then drag the canvas. Navigate only leaves the left button free. Middle/right drag always pans."));
        Tools.Children.Add(new UiDropdown("Brush shape", ["Circle", "Square"], () => (int)brush.Shape,
            value => brush = brush with { Shape = (BrushShape)value }, "World-space footprint, independent of particle display size."));
        Tools.Children.Add(Number("Brush radius [L]", () => brush.Radius, value =>
        {
            if (!double.IsFinite(value) || value <= 0) { throw new ArgumentOutOfRangeException(nameof(value)); }
            brush = brush with { Radius = value };
        }, 0.06, 0.001, 0.5, "Positive world-space radius; square tools use this as half-width.", log: true));
        Tools.Children.Add(Number("Force intensity [L/T²]", () => brush.Intensity,
            value => brush = brush with { Intensity = value }, 2, 0.01, 100,
            "Per actual applied sample, velocity changes by intensity * fixed numerical dt. Paused samples are explicit impulses; replay uses actual boundary payloads.", log: true));
        Tools.Children.Add(Number("Direction [rad]", () => brush.DirectionRadians, value => brush = brush with { DirectionRadians = value },
            0, -Math.PI, Math.PI, "Directional force angle: zero rightwards, pi/2 downwards."));
        Tools.Children.Add(Number("Added per sample", () => brush.AddCount, value => brush = brush with { AddCount = Integer(value) },
            8, 1, 128, "1..4096 particles per actual add sample. Unapplied previews coalesce while the CPU is busy.", integer: true));
        Tools.Children.Add(Number("Paint/add species", () => brush.Species, value => brush = brush with { Species = Integer(value) },
            0, 0, 31, "Active species assigned by add and paint tools.", integer: true));
        Tools.Children.Add(Number("Affected species", () => brush.SpeciesFilter, value => brush = brush with { SpeciesFilter = Integer(value) },
            -1, -1, 31, "-1 affects every species. Another active index filters move, paint, erase and force tools.", integer: true));
        Tools.Children.Add(new UiLabel(() => "One stroke is one undo; each applied sample remains in replay. Full actual sample states currently grow session memory with population and stroke length.", 66));
    }
}

internal sealed class PopulationBarsWidget(EditorSession editor, Action<int> selected, Action<int> colour, Action<int, int> adjust) : UiWidget(
    "Current populations: click a row to select its exact target, a colour chip to edit colour, or -/+ to adjust live by 1% (at least one).")
{
    internal override int Height => editor.Publication.ActualPopulations.Length * 27 + 22;
    internal override void Press(Point point)
    {
        int species = (point.Y - Bounds.Y - 22) / 27;
        ReadOnlySpan<int> counts = editor.Publication.ActualPopulations.Span;
        if (point.Y < Bounds.Y + 22 || species < 0 || species >= counts.Length) { return; }
        if (point.X < Bounds.X + 21) { colour(species); }
        else if (point.X >= Bounds.Right - 48)
        {
            int amount = Math.Max(1, editor.Publication.Count / 100);
            adjust(species, point.X < Bounds.Right - 24 ? -amount : amount);
        }
        else { selected(species); }
    }
    internal override void Draw(UiCanvas canvas)
    {
        canvas.Text("Current live counts / percentages", Bounds.X + 2, Bounds.Y, UiCanvas.Muted, size: 1.3f);
        ReadOnlySpan<int> counts = editor.Publication.ActualPopulations.Span;
        int total = editor.Publication.Count;
        for (int species = 0; species < counts.Length; species++)
        {
            int y = Bounds.Y + 22 + species * 27;
            uint rgb = editor.Appearance.ColorForSpecies(species);
            Color tint = new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            double fraction = total == 0 ? 0 : (double)counts[species] / total;
            canvas.Rounded(new Rectangle(Bounds.X + 3, y + 5, 13, 14), tint, 3);
            Rectangle bar = new(Bounds.X + 23, y + 2, Math.Max(1, Bounds.Width - 76), 22);
            canvas.Rounded(bar, UiCanvas.Surface, 3);
            canvas.Rounded(new Rectangle(bar.X, bar.Y, (int)(bar.Width * fraction), bar.Height), tint * 0.28f, 3);
            canvas.TextFit($"S{species}: {counts[species]:N0}  ({fraction:P1})", bar, UiCanvas.Ink, 1.3f);
            canvas.TextCentered("-", new Rectangle(Bounds.Right - 47, y + 2, 22, 22), UiCanvas.Muted);
            canvas.TextCentered("+", new Rectangle(Bounds.Right - 23, y + 2, 22, 22), UiCanvas.Accent);
        }
    }
}

internal sealed class PaletteSwatchesWidget(EditorSession editor) : UiWidget
{
    internal override bool Focusable => false;
    internal override int Height => 25;
    internal override void Draw(UiCanvas canvas)
    {
        int count = Math.Min(32, editor.Configuration.Matrix.SpeciesCount);
        for (int i = 0; i < count; i++)
        {
            uint rgb = editor.Appearance.ColorForSpecies(i);
            canvas.Fill(new Rectangle(Bounds.X + Bounds.Width * i / count, Bounds.Y + 3, Math.Max(1, Bounds.Width / count - 2), 18),
                new Color((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        }
    }
}
