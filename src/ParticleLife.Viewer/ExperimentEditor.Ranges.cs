using ParticleLife.Core.Models;

namespace ParticleLife.Viewer;

/// <summary>Selects the quantities changed by an explicit interaction randomization.</summary>
public enum InteractionRandomization
{
    /// <summary>Changes coefficients and retains every current radius.</summary>
    Rules,
    /// <summary>Changes core/cutoff radii and retains coefficients.</summary>
    Radii,
    /// <summary>Changes coefficients and core/cutoff radii together.</summary>
    RulesAndRadii
}

public sealed partial class ExperimentEditor
{
    /// <summary>Explicitly switches between the global curve and independent directed pair radii.</summary>
    /// <param name="enabled">True copies global radii when first enabling; false restores the saved global controls.</param>
    /// <remarks>Disabling discards the active pair table as one undoable, replayable rule edit. It does
    /// not average pair values or change particles. An unsupported global periodic cutoff is rejected.</remarks>
    public void EnablePairRanges(bool enabled)
    {
        InteractionMatrix matrix = Configuration.Matrix;
        PairInteractionRanges? ranges = enabled
            ? matrix.Ranges ?? PairInteractionRanges.FromGlobal(matrix.SpeciesCount, Configuration.Parameters)
            : null;
        ApplyConfiguration(WithMatrix(matrix.WithRanges(ranges)), enabled ? "Use pair radii" : "Use global radii");
    }

    /// <summary>Sets one directed pair's physical radii, optionally linking the reverse pair.</summary>
    /// <param name="receiver">Receiver row.</param>
    /// <param name="source">Source column.</param>
    /// <param name="core">Finite nonnegative core radius, strictly below cutoff.</param>
    /// <param name="cutoff">Positive outer radius with finite positive square.</param>
    /// <param name="linkReverse">Whether the reverse pair receives the same actual values.</param>
    public void SetPairRange(int receiver, int source, double core, double cutoff, bool linkReverse = false)
    {
        InteractionMatrix matrix = Configuration.Matrix;
        _ = matrix[receiver, source];
        SetSelectedPairRanges([receiver * matrix.SpeciesCount + source], core, cutoff, linkReverse);
    }

    /// <summary>Sets selected physical radii together, enabling pair mode from actual global values when necessary.</summary>
    /// <param name="cells">Flattened receiver-row/source-column indices; duplicates are harmless and empty is a no-op.</param>
    /// <param name="core">Finite nonnegative core radius, strictly below cutoff.</param>
    /// <param name="cutoff">Positive outer radius with finite positive square.</param>
    /// <param name="linkReverse">Also changes each selected cell's transpose.</param>
    /// <remarks>The complete candidate is validated before publication. Invalid cells or periodic
    /// radii leave configuration, particles, undo and the replay timeline unchanged.</remarks>
    public void SetSelectedPairRanges(ReadOnlySpan<int> cells, double core, double cutoff, bool linkReverse = false)
    {
        InteractionMatrix matrix = Configuration.Matrix;
        bool[] selected = SelectInteractionCells(cells, matrix.SpeciesCount, linkReverse);
        if (cells.IsEmpty) { return; }
        PairInteractionRanges original = matrix.Ranges ?? PairInteractionRanges.FromGlobal(matrix.SpeciesCount, Configuration.Parameters);
        double[] cores = original.CoreRadii.ToArray();
        double[] cutoffs = original.CutoffRadii.ToArray();
        for (int index = 0; index < selected.Length; index++)
        {
            if (!selected[index]) { continue; }
            cores[index] = core;
            cutoffs[index] = cutoff;
        }
        PairInteractionRanges ranges = new(matrix.SpeciesCount, cores, cutoffs);
        ApplyConfiguration(WithMatrix(matrix.WithRanges(ranges)), "Set selected pair radii");
    }

    /// <summary>Randomizes selected coefficients, radii or both while retaining unselected quantities.</summary>
    /// <param name="cells">Flattened directed matrix indices; empty is a no-op.</param>
    /// <param name="components">The independent quantities to generate.</param>
    /// <param name="seed">Generation provenance; replay retains generated actual values.</param>
    /// <param name="minimumCutoff">Inclusive positive lower cutoff for radius generation.</param>
    /// <param name="maximumCutoff">Inclusive upper cutoff; periodic mode also constrains it to half the shortest extent.</param>
    /// <param name="maximumCoreFraction">Largest generated core/cutoff ratio, in [0, 1); zero produces zero cores.</param>
    /// <param name="linkReverse">Generates one value for each unordered selected pair and applies it in both directions.</param>
    /// <remarks>Rules-only generation never enables pair mode or changes any radii. Radius generation
    /// starts from actual current radii. Coefficients are uniform in [-1, 1), cutoffs are uniform across
    /// the specified interval and core fractions are uniform from zero to their stated maximum.</remarks>
    public void RandomizeInteractionSelection(ReadOnlySpan<int> cells, InteractionRandomization components,
        int seed, double minimumCutoff, double maximumCutoff, double maximumCoreFraction = 0.6, bool linkReverse = false)
    {
        if (!Enum.IsDefined(components)) { throw new ArgumentOutOfRangeException(nameof(components)); }
        InteractionMatrix matrix = Configuration.Matrix;
        bool[] selected = SelectInteractionCells(cells, matrix.SpeciesCount, linkReverse);
        if (cells.IsEmpty) { return; }
        bool changeRules = components != InteractionRandomization.Radii;
        bool changeRadii = components != InteractionRandomization.Rules;
        PairInteractionRanges? ranges = matrix.Ranges;
        double[] coefficients = matrix.Values.ToArray();
        double[] cores = [], cutoffs = [];
        if (changeRadii)
        {
            _ = new PairInteractionRanges(1, [0], [minimumCutoff]);
            _ = new PairInteractionRanges(1, [0], [maximumCutoff]);
            if (minimumCutoff > maximumCutoff || !double.IsFinite(maximumCoreFraction)
                || maximumCoreFraction < 0 || maximumCoreFraction >= 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumCutoff), "Cutoffs must be ordered and maximum core fraction must be in [0, 1).");
            }
            if (Configuration.Parameters.Boundary == BoundaryKind.Periodic
                && maximumCutoff > Math.Min(Configuration.Parameters.WorldWidth, Configuration.Parameters.WorldHeight) / 2)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumCutoff), "Periodic cutoffs cannot exceed half the shorter world extent.");
            }
            ranges ??= PairInteractionRanges.FromGlobal(matrix.SpeciesCount, Configuration.Parameters);
            cores = ranges.CoreRadii.ToArray();
            cutoffs = ranges.CutoffRadii.ToArray();
        }
        Random random = new(seed);
        for (int index = 0; index < selected.Length; index++)
        {
            if (!selected[index]) { continue; }
            int reverse = index % matrix.SpeciesCount * matrix.SpeciesCount + index / matrix.SpeciesCount;
            if (linkReverse && reverse < index) { continue; }
            if (changeRules)
            {
                coefficients[index] = random.NextDouble() * 2 - 1;
                if (linkReverse) { coefficients[reverse] = coefficients[index]; }
            }
            if (changeRadii)
            {
                cutoffs[index] = minimumCutoff + random.NextDouble() * (maximumCutoff - minimumCutoff);
                cores[index] = cutoffs[index] * (random.NextDouble() * maximumCoreFraction);
                if (linkReverse)
                {
                    cutoffs[reverse] = cutoffs[index];
                    cores[reverse] = cores[index];
                }
            }
        }
        if (changeRadii) { ranges = new(matrix.SpeciesCount, cores, cutoffs); }
        InteractionMatrix candidate = new(matrix.SpeciesCount, coefficients, ranges);
        Apply(CaptureValues() with { Configuration = WithMatrix(candidate) }, null, false,
            $"Randomize {components}", generator: Metadata("viewer-interaction-selection", seed));
    }

    private static bool[] SelectInteractionCells(ReadOnlySpan<int> cells, int speciesCount, bool linkReverse)
    {
        bool[] selected = new bool[checked(speciesCount * speciesCount)];
        foreach (int cell in cells)
        {
            if ((uint)cell >= (uint)selected.Length) { throw new ArgumentOutOfRangeException(nameof(cells)); }
            selected[cell] = true;
            if (linkReverse) { selected[cell % speciesCount * speciesCount + cell / speciesCount] = true; }
        }
        return selected;
    }
}
