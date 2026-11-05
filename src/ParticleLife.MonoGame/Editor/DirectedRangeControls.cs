using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Viewer;

namespace ParticleLife.MonoGame.Editor;

/// <summary>Separates coupled radius drafts from atomic selected-pair and bulk owner actions.</summary>
internal sealed class DirectedRangeControls
{
    private readonly EditorSession editor;
    private readonly InteractionMatrixWidget matrix;
    private readonly Func<bool> reverse;
    private readonly Action<string> report;
    private double core = 0.03, cutoff = 0.1;
    private bool dirty;
    private int receiver = -1, source = -1;
    private SimulationConfiguration? observed;
    private InteractionRandomization components = InteractionRandomization.Radii;
    private double minimum = 0.02, maximum = 0.2, coreFraction = 0.6;
    internal UiSection Section { get; } = new("Directed core and cutoff radii", true);

    internal DirectedRangeControls(EditorSession editor, InteractionMatrixWidget matrix, Func<bool> reverse, Action<string> report)
    {
        this.editor = editor; this.matrix = matrix; this.reverse = reverse; this.report = report;
        Section.Children.Add(new UiDropdown("Radius mode", ["Global R / beta", "Directed pair radii"],
            () => editor.Configuration.Matrix.Ranges is null ? 0 : 1, index => editor.EnablePairRanges(index == 1),
            "Pair mode copies global radii. Global mode removes the table and restores saved R/beta. Undo restores it. Periodic global R must fit half the shorter world."));
        Section.Children.Add(new UiLabel(() =>
        {
            Synchronize(); SimulationConfiguration configuration = observed!; InteractionMatrix current = configuration.Matrix;
            return $"Actual {receiver} <- {source}: core {current.CoreRadius(receiver, source, configuration.Parameters):G17}, cutoff {current.CutoffRadius(receiver, source, configuration.Parameters):G17}.";
        }, 48, UiCanvas.Accent));
        Section.Children.Add(new UiNumber("Core radius draft [L]", () => { Synchronize(); return core; }, value => { core = value; dirty = true; },
            0, 0, 0.5, "Coupled draft: zero removes core repulsion; otherwise require 0 < core < cutoff. Apply both values together."));
        Section.Children.Add(new UiNumber("Cutoff radius draft [L]", () => { Synchronize(); return cutoff; }, value => { cutoff = value; dirty = true; },
            0.1, 0.001, 1, "Positive outer draft; square must be finite/positive. Periodic cutoff must be <= half the shorter extent.", logarithmic: true));
        Section.Children.Add(new UiLabel(() => dirty ? "Unapplied drafts remain available when selection changes." : "Drafts follow selected actual values.", 32));
        Section.Children.Add(new UiRow(new UiButton(() => "Apply pair radii live", () => Apply(false)),
            new UiButton(() => "Apply selected radii live", () => Apply(true), enabled: () => matrix.Selection.Count > 0)));
        Section.Children.Add(new UiButton(() => "Use selected actual radii", () => { dirty = false; observed = null; Synchronize(); }, "Discard unapplied drafts and copy actual radii."));
        Section.Children.Add(new UiLabel(() => "Rows receive; columns source. Linked reverse applies both directions. Global R/beta remain defaults in pair mode. Exactly overlapping particles always contribute zero.", 62));
        Section.Children.Add(new UiDropdown("Generate selected", ["Rules only", "Radii only", "Rules and radii"],
            () => (int)components, index => components = (InteractionRandomization)index,
            "Rules-only retains radii; radii-only retains coefficients. Actual generated values and seed are recorded; particles are unchanged."));
        Section.Children.Add(new UiNumber("Random cutoff min [L]", () => minimum, value => minimum = value, 0.02, 0.001, 0.5, "Lower cutoff draft for radius generation only.", logarithmic: true));
        Section.Children.Add(new UiNumber("Random cutoff max [L]", () => maximum, value => maximum = value, 0.2, 0.001, 0.5, "Upper cutoff draft; periodic limits validate atomically.", logarithmic: true));
        Section.Children.Add(new UiNumber("Maximum core fraction", () => coreFraction, value => coreFraction = value, 0.6, 0, 0.99, "Generated core/cutoff ratios range from zero to this value in [0,1). Zero generates zero cores."));
        Section.Children.Add(new UiButton(() => "Randomize selection live", Randomize, enabled: () => matrix.Selection.Count > 0));
    }
    private void Synchronize()
    {
        SimulationConfiguration current = editor.Configuration;
        if (ReferenceEquals(current, observed) && receiver == matrix.Receiver && source == matrix.Source) { return; }
        observed = current; receiver = Math.Min(matrix.Receiver, current.Matrix.SpeciesCount - 1); source = Math.Min(matrix.Source, current.Matrix.SpeciesCount - 1);
        if (!dirty) { core = current.Matrix.CoreRadius(receiver, source, current.Parameters); cutoff = current.Matrix.CutoffRadius(receiver, source, current.Parameters); }
    }
    private void Apply(bool selected)
    {
        Synchronize(); _ = new PairInteractionRanges(1, [core], [cutoff]);
        double c = core, r = cutoff; bool linked = reverse();
        MatrixCell[] cells = selected ? matrix.Selection.Cells.ToArray() : [new(receiver, source)];
        Queue("Apply directed radii", owner => owner.SetSelectedPairRanges(Reindex(cells, owner.Configuration.Matrix), c, r, linked), () =>
        {
            if (core == c && cutoff == r) { dirty = false; observed = null; }
        });
    }
    private void Randomize()
    {
        MatrixCell[] cells = matrix.Selection.Cells.ToArray(); InteractionRandomization kind = components;
        double min = minimum, max = maximum, fraction = coreFraction; int seed = editor.Generation.Seed; bool linked = reverse();
        Queue($"Randomize {kind}", owner => owner.RandomizeInteractionSelection(Reindex(cells, owner.Configuration.Matrix), kind, seed, min, max, fraction, linked));
    }
    private void Queue(string label, Action<ExperimentEditor> apply, Action? completed = null)
    {
        if (editor.IsBackground) { report(label + " queued for a completed boundary."); }
        editor.Run(label, apply, () => { completed?.Invoke(); report(label + " applied; particles and clock retained."); });
    }
    private static int[] Reindex(MatrixCell[] cells, InteractionMatrix current)
    {
        int[] result = new int[cells.Length];
        for (int i = 0; i < cells.Length; i++) { _ = current[cells[i].Receiver, cells[i].Source]; result[i] = cells[i].Receiver * current.SpeciesCount + cells[i].Source; }
        return result;
    }
}
