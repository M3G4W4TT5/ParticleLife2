namespace ParticleLife.Core.Models;

/// <summary>Owns directed receiver-row/source-column core and cutoff radii in reduced length units.</summary>
/// <remarks>Each pair has 0 &lt;= core &lt; cutoff. A zero core removes the species-independent
/// repulsion region; the outer triangular coefficient still vanishes at zero and at the cutoff.
/// Exactly coincident particles always contribute zero because their direction is undefined.
/// All pairs remain present, including zero coefficients, whose nonzero cores still repel.</remarks>
public sealed class PairInteractionRanges
{
    private readonly double[] cores;
    private readonly double[] cutoffs;

    /// <summary>Copies and validates complete directed radius tables.</summary>
    /// <param name="speciesCount">Positive matrix dimension.</param>
    /// <param name="coreRadii">Exactly speciesCount squared finite nonnegative core radii.</param>
    /// <param name="cutoffRadii">Matching positive cutoffs with finite positive squares; each exceeds its core.</param>
    /// <exception cref="ArgumentException">Dimensions or supported radius ranges are invalid.</exception>
    public PairInteractionRanges(int speciesCount, ReadOnlySpan<double> coreRadii, ReadOnlySpan<double> cutoffRadii)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(speciesCount);
        if ((long)speciesCount * speciesCount != coreRadii.Length || coreRadii.Length != cutoffRadii.Length)
        {
            throw new ArgumentException("Both directed radius tables must contain speciesCount squared entries.");
        }

        double maximum = 0;
        for (int index = 0; index < coreRadii.Length; index++)
        {
            double core = coreRadii[index];
            double cutoff = cutoffRadii[index];
            ModelParameters.RequirePositiveFinite(cutoff, nameof(cutoffRadii));
            ModelParameters.RequirePositiveFinite(cutoff * cutoff, nameof(cutoffRadii));
            if (!double.IsFinite(core) || core < 0 || core >= cutoff)
            {
                throw new ArgumentOutOfRangeException(nameof(coreRadii), "Each core must be finite, nonnegative and smaller than its cutoff.");
            }

            // Reject a nonzero core that would disappear when expressed relative to its cutoff.
            if (core > 0 && core / cutoff == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(coreRadii), "A nonzero core must be representable relative to its cutoff.");
            }
            maximum = Math.Max(maximum, cutoff);
        }

        SpeciesCount = speciesCount;
        cores = coreRadii.ToArray();
        cutoffs = cutoffRadii.ToArray();
        MaximumCutoff = maximum;
    }

    /// <summary>Gets the number of receiver and source species.</summary>
    public int SpeciesCount { get; }

    /// <summary>Gets the owned row-major core radii without allocating.</summary>
    public ReadOnlySpan<double> CoreRadii => cores;

    /// <summary>Gets the owned row-major cutoff radii without allocating.</summary>
    public ReadOnlySpan<double> CutoffRadii => cutoffs;

    /// <summary>Gets the conservative largest pair cutoff used to build a complete neighbour index.</summary>
    public double MaximumCutoff { get; }

    /// <summary>Compares the actual radii and matrix dimensions.</summary>
    /// <param name="other">Another table, or null.</param>
    /// <returns>True when every directed radius has the same stored value.</returns>
    public bool EquivalentTo(PairInteractionRanges? other) => other is not null && SpeciesCount == other.SpeciesCount
        && CoreRadii.SequenceEqual(other.CoreRadii) && CutoffRadii.SequenceEqual(other.CutoffRadii);

    /// <summary>Creates pair tables equal to the current global core and cutoff settings.</summary>
    /// <param name="speciesCount">Positive matrix dimension.</param>
    /// <param name="parameters">Validated global quantities; core equals beta times R.</param>
    /// <returns>An owned table suitable for explicit conversion from global to per-pair mode.</returns>
    public static PairInteractionRanges FromGlobal(int speciesCount, ModelParameters parameters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(speciesCount);
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.Validate();
        double[] cores = new double[checked(speciesCount * speciesCount)];
        double[] cutoffs = new double[cores.Length];
        Array.Fill(cores, parameters.RepulsionFraction * parameters.Cutoff);
        Array.Fill(cutoffs, parameters.Cutoff);
        return new(speciesCount, cores, cutoffs);
    }
}
