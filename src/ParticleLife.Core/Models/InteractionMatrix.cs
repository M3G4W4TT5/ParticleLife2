namespace ParticleLife.Core.Models;

/// <summary>An owned immutable matrix of directed, dimensionless species coefficients.</summary>
/// <remarks>Entry A[i,j] controls the force on receiver species i due to source species j.
/// No reciprocity is assumed. Construction copies the row-major input; reads allocate no memory.</remarks>
public sealed class InteractionMatrix
{
    private readonly double[] values;

    /// <summary>Copies and validates a square directed matrix.</summary>
    /// <param name="speciesCount">Positive number of species.</param>
    /// <param name="values">Exactly speciesCount squared row-major values, each finite and in [-1, 1].</param>
    /// <exception cref="ArgumentOutOfRangeException">The count or a coefficient is invalid.</exception>
    /// <exception cref="ArgumentException">The matrix has the wrong number of entries.</exception>
    public InteractionMatrix(int speciesCount, ReadOnlySpan<double> values)
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
    }

    /// <summary>Gets the number of supported species.</summary>
    public int SpeciesCount { get; }

    /// <summary>Gets the immutable row-major coefficient values.</summary>
    public ReadOnlySpan<double> Values => values;

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
