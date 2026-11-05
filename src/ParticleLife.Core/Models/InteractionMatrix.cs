namespace ParticleLife.Core.Models;

/// <summary>Owns immutable directed species coefficients and optional per-pair interaction ranges.</summary>
/// <remarks>Entry A[i,j] controls the force on receiver species i due to source species j.
/// No reciprocity is assumed. Construction copies the row-major input; reads allocate no memory.</remarks>
public sealed class InteractionMatrix
{
    private readonly double[] values;

    /// <summary>Copies and validates a square directed matrix.</summary>
    /// <param name="speciesCount">Positive number of species.</param>
    /// <param name="values">Exactly speciesCount squared row-major values, each finite and in [-1, 1].</param>
    /// <param name="ranges">Optional matching directed radius tables; null retains the global cutoff/core model.</param>
    /// <exception cref="ArgumentOutOfRangeException">The count or a coefficient is invalid.</exception>
    /// <exception cref="ArgumentException">The matrix has the wrong number of entries.</exception>
    public InteractionMatrix(int speciesCount, ReadOnlySpan<double> values, PairInteractionRanges? ranges = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(speciesCount);
        if ((long)speciesCount * speciesCount != values.Length)
        {
            throw new ArgumentException("The row-major matrix must contain speciesCount squared entries.", nameof(values));
        }

        for (int i = 0; i < values.Length; i++)
        {
            if (!double.IsFinite(values[i]) || values[i] < -1 || values[i] > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(values), $"Matrix entry {i} must be finite and in [-1, 1].");
            }
        }

        SpeciesCount = speciesCount;
        this.values = values.ToArray();
        if (ranges is not null && ranges.SpeciesCount != speciesCount)
        {
            throw new ArgumentException("Pair ranges must match the coefficient matrix dimensions.", nameof(ranges));
        }
        Ranges = ranges;
    }

    /// <summary>Gets the number of supported species.</summary>
    public int SpeciesCount { get; }

    /// <summary>Gets the immutable row-major coefficient values.</summary>
    public ReadOnlySpan<double> Values => values;

    /// <summary>Gets directed radius tables, or null for the global R/beta mode.</summary>
    public PairInteractionRanges? Ranges { get; }

    /// <summary>Replaces coefficients while retaining the selected pair-radius mode and values.</summary>
    /// <param name="coefficients">Complete row-major coefficients in [-1,1].</param>
    /// <returns>A validated immutable replacement.</returns>
    public InteractionMatrix WithCoefficients(ReadOnlySpan<double> coefficients) => new(SpeciesCount, coefficients, Ranges);

    /// <summary>Replaces radius tables without altering any directed coefficient.</summary>
    /// <param name="ranges">Matching tables, or null to explicitly return to global ranges.</param>
    /// <returns>A validated immutable replacement.</returns>
    public InteractionMatrix WithRanges(PairInteractionRanges? ranges) => new(SpeciesCount, values, ranges);

    /// <summary>Compares coefficients and range mode/values, without copying either table.</summary>
    /// <param name="other">Another matrix, or null.</param>
    /// <returns>True when all implemented pair rules are identical.</returns>
    public bool EquivalentTo(InteractionMatrix? other) => other is not null && SpeciesCount == other.SpeciesCount
        && Values.SequenceEqual(other.Values)
        && (ReferenceEquals(Ranges, other.Ranges) || Ranges?.EquivalentTo(other.Ranges) == true);

    /// <summary>Gets the largest active pair cutoff, including repulsive cores of zero-coefficient pairs.</summary>
    /// <param name="parameters">Global model values used when no pair tables exist.</param>
    /// <returns>The conservative neighbour-search radius in reduced length units.</returns>
    public double MaximumCutoff(ModelParameters parameters) => Ranges?.MaximumCutoff ?? parameters.Cutoff;

    /// <summary>Gets a directed core radius, resolving the global beta/R mode when no table exists.</summary>
    /// <param name="receiver">Receiver species.</param><param name="source">Source species.</param>
    /// <param name="parameters">Validated global model values.</param>
    /// <returns>The effective core radius in reduced length units.</returns>
    public double CoreRadius(int receiver, int source, ModelParameters parameters)
    {
        _ = this[receiver, source];
        return Ranges is null ? parameters.RepulsionFraction * parameters.Cutoff : Ranges.CoreRadii[receiver * SpeciesCount + source];
    }

    /// <summary>Gets a directed outer radius, resolving global R when no table exists.</summary>
    /// <param name="receiver">Receiver species.</param><param name="source">Source species.</param>
    /// <param name="parameters">Validated global model values.</param>
    /// <returns>The effective positive cutoff in reduced length units.</returns>
    public double CutoffRadius(int receiver, int source, ModelParameters parameters)
    {
        _ = this[receiver, source];
        return Ranges is null ? parameters.Cutoff : Ranges.CutoffRadii[receiver * SpeciesCount + source];
    }

    /// <summary>Checks geometry-dependent rules once at the configuration boundary.</summary>
    internal void ValidateFor(ModelParameters parameters)
    {
        if (parameters.Boundary == BoundaryKind.Periodic
            && MaximumCutoff(parameters) > Math.Min(parameters.WorldWidth, parameters.WorldHeight) / 2)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "Periodic cutoffs must be at most half the shortest world extent.");
        }
    }

    /// <summary>Gets the directed coefficient affecting one receiver due to one source.</summary>
    /// <param name="receiver">Zero-based receiver species.</param>
    /// <param name="source">Zero-based source species.</param>
    /// <returns>A dimensionless coefficient in [-1, 1].</returns>
    /// <exception cref="ArgumentOutOfRangeException">Either species is outside the matrix.</exception>
    public double this[int receiver, int source]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(receiver);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(receiver, SpeciesCount);
            ArgumentOutOfRangeException.ThrowIfNegative(source);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(source, SpeciesCount);
            return values[receiver * SpeciesCount + source];
        }
    }
}
