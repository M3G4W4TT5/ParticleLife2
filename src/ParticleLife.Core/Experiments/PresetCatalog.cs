namespace ParticleLife.Core.Experiments;

/// <summary>Describes a built-in experiment recipe independently of any host or display framework.</summary>
/// <param name="Kind">Stable recipe identifier.</param>
/// <param name="Key">Lowercase command-line and provenance key.</param>
/// <param name="Name">Readable recipe name.</param>
/// <param name="Description">Short explanation of the intended motif and relevant limitations.</param>
/// <param name="DefaultParticleCount">Suggested total initial population.</param>
/// <param name="DefaultSpeciesCount">Suggested species count; part of the recipe, not a colour preference.</param>
/// <param name="IsExperimental">Whether the recipe needs particular care when interpreting its behaviour.</param>
public sealed record PresetInfo(PresetKind Kind, string Key, string Name, string Description,
    int DefaultParticleCount, int DefaultSpeciesCount, bool IsExperimental);

/// <summary>Provides shared names, defaults and population policies for built-in recipes.</summary>
/// <remarks>Defaults are starting configurations, not guarantees of a particular morphology or physical phase.
/// New recipes use 420 particles so equal populations divide exactly across their recommended species counts.</remarks>
public static class PresetCatalog
{
    /// <summary>Gets the immutable ordered collection of available recipes.</summary>
    public static IReadOnlyList<PresetInfo> All { get; } = Array.AsReadOnly<PresetInfo>(
    [
        new(PresetKind.Random, "random", "Random", "Independent directed interactions sampled from [-1, 1).", 500, 7, false),
        new(PresetKind.Chains, "chains", "Chains", "Same and adjacent species attract; others repel. At three or fewer species every entry attracts.", 500, 7, false),
        new(PresetKind.Snakes, "snakes", "Snakes", "Self-attraction with a directed successor link. Two species make the links reciprocal.", 500, 7, false),
        new(PresetKind.CoreHalo, "core-halo", "Core and halo", "Two species in a 1:4 mix form compact cores with surrounding halo particles.", 420, 2, false),
        new(PresetKind.AlternatingBands, "alternating-bands", "Alternating bands", "Two species attract one another and repel their own kind, encouraging alternating rows.", 420, 2, false),
        new(PresetKind.Ribbons, "ribbons", "Ribbons", "Adjacent species attract; self and other species repel, encouraging curved coloured ribbons.", 420, 7, false),
        new(PresetKind.CyclicPursuit, "cyclic-pursuit", "Cyclic pursuit", "Three species pursue the next and repel the previous species, with mild self-attraction.", 420, 3, false),
        new(PresetKind.ChaseCycle5, "chase-cycle-5", "Chase cycle 5", "Five-species directed cycle inspired by nonreciprocal coupling research; an active exploration recipe.", 420, 5, false),
        new(PresetKind.ChaseCycle6, "chase-cycle-6", "Chase cycle 6", "Six-species comparison for Chase cycle 5; activity can be sensitive to the timestep.", 420, 6, false),
        new(PresetKind.SpeciesIslands, "species-islands", "Species islands", "Self-attraction and cross-species repulsion encourage separate single-species islands.", 420, 6, false),
        new(PresetKind.BiasedRibbons, "biased-ribbons", "Biased ribbons", "Experimental: directional ribbons can move, but seed sensitivity and soft-wall accumulation are strong.", 420, 7, true)
    ]);

    /// <summary>Finds the shared metadata for a defined recipe.</summary>
    /// <param name="kind">Recipe identifier.</param>
    /// <returns>Immutable recipe metadata.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The identifier is undefined.</exception>
    public static PresetInfo Get(PresetKind kind)
    {
        foreach (PresetInfo recipe in All)
        {
            if (recipe.Kind == kind)
            {
                return recipe;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown built-in recipe.");
    }

    /// <summary>Parses an exact lowercase recipe key; enum numbers and aliases are not accepted.</summary>
    /// <param name="key">A key from <see cref="All"/>, such as <c>core-halo</c>.</param>
    /// <returns>The matching recipe identifier.</returns>
    /// <exception cref="ArgumentException">The key is absent or unknown.</exception>
    public static PresetKind Parse(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        foreach (PresetInfo recipe in All)
        {
            if (string.Equals(recipe.Key, key, StringComparison.Ordinal))
            {
                return recipe.Kind;
            }
        }

        throw new ArgumentException($"Unknown preset '{key}'. Choose {string.Join(", ", All.Select(recipe => recipe.Key))}.", nameof(key));
    }

    /// <summary>Checks whether a species count preserves the recipe's defined interaction roles.</summary>
    /// <param name="kind">Recipe identifier.</param>
    /// <param name="speciesCount">Proposed matrix dimension.</param>
    /// <returns>True for a supported count; false for nonpositive or incompatible counts.</returns>
    /// <remarks>Historical recipes accept any positive count. Core and halo and Alternating bands require two;
    /// Cyclic pursuit requires three; Chase cycle 5/6 require their named count. Ribbons and Biased ribbons
    /// require at least three so predecessor and successor are distinct. Species islands requires at least two.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The recipe identifier is undefined.</exception>
    public static bool SupportsSpeciesCount(PresetKind kind, int speciesCount) => kind switch
    {
        PresetKind.Random or PresetKind.Chains or PresetKind.Snakes => speciesCount >= 1,
        PresetKind.CoreHalo or PresetKind.AlternatingBands => speciesCount == 2,
        PresetKind.CyclicPursuit => speciesCount == 3,
        PresetKind.ChaseCycle5 => speciesCount == 5,
        PresetKind.ChaseCycle6 => speciesCount == 6,
        PresetKind.Ribbons or PresetKind.BiasedRibbons => speciesCount >= 3,
        PresetKind.SpeciesIslands => speciesCount >= 2,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown built-in recipe.")
    };

    /// <summary>Allocates suggested per-species counts whose sum is exactly the requested population.</summary>
    /// <param name="kind">Recipe identifier.</param>
    /// <param name="particleCount">Nonnegative total; zero gives an all-zero population.</param>
    /// <param name="speciesCount">Compatible number of species.</param>
    /// <returns>An owned array of nonnegative counts in species-index order.</returns>
    /// <remarks>Core and halo assigns the nearest integer to one fifth of the total to species zero,
    /// with the remainder assigned to species one; integer totals never encounter an exact half tie.
    /// All other recipes divide evenly and assign remainder particles to the lowest indices.
    /// Historical <see cref="PresetGenerator.Create"/> calls retain random species sampling rather than this policy.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The total, recipe identifier or species count is invalid.</exception>
    public static int[] CreatePopulations(PresetKind kind, int particleCount, int speciesCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(particleCount);
        ValidateSpeciesCount(kind, speciesCount);
        int[] counts = new int[speciesCount];
        if (kind == PresetKind.CoreHalo)
        {
            counts[0] = (int)(((long)particleCount + 2) / 5);
            counts[1] = particleCount - counts[0];
            return counts;
        }

        int quotient = particleCount / speciesCount;
        int remainder = particleCount % speciesCount;
        for (int i = 0; i < counts.Length; i++)
        {
            counts[i] = quotient + (i < remainder ? 1 : 0);
        }

        return counts;
    }

    /// <summary>Rejects dimensions that would collapse distinct interaction roles before allocation or editing.</summary>
    internal static void ValidateSpeciesCount(PresetKind kind, int speciesCount)
    {
        if (!SupportsSpeciesCount(kind, speciesCount))
        {
            PresetInfo recipe = Get(kind);
            throw new ArgumentOutOfRangeException(nameof(speciesCount), speciesCount,
                $"{recipe.Name} does not support this species count. Its recommended count is {recipe.DefaultSpeciesCount}.");
        }
    }
}
