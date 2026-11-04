using System.Runtime.InteropServices;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Core.Experiments;

/// <summary>Selects a documented directed species matrix and initial population recipe.</summary>
public enum PresetKind
{
    /// <summary>Independently samples each directed coefficient uniformly in [-1, 1).</summary>
    Random,

    /// <summary>Attracts the same and both adjacent species in a cyclic chain; repels all others.</summary>
    Chains,

    /// <summary>Attracts its own species with 1 and its successor with 0.2; other coefficients are zero.</summary>
    Snakes,

    /// <summary>Uses attracting cores and mutually repelling halo particles in a two-species 1:4 mix.</summary>
    CoreHalo,

    /// <summary>Uses reciprocal cross-attraction and self-repulsion in two species.</summary>
    AlternatingBands,

    /// <summary>Attracts adjacent species while repelling its own and all nonadjacent species.</summary>
    Ribbons,

    /// <summary>Uses a three-species pursuit cycle with antisymmetric cross-couplings and mild self-attraction.</summary>
    CyclicPursuit,

    /// <summary>Uses a five-species directed cycle adapted from a nonreciprocal coupling network.</summary>
    ChaseCycle5,

    /// <summary>Uses the same directed network coefficients over six species for comparison.</summary>
    ChaseCycle6,

    /// <summary>Attracts its own species and repels every other species.</summary>
    SpeciesIslands,

    /// <summary>Adds directional bias to Ribbons; experimental because wall accumulation and seed sensitivity are strong.</summary>
    BiasedRibbons
}

/// <summary>Creates seeded initial conditions and directed matrices for the built-in Particle Life recipes.</summary>
/// <remarks>
/// All positions are uniform over the rectangular world and velocities start at zero. Historical Random,
/// Chains and Snakes preserve their original random-number consumption, random species populations,
/// names and generator provenance. The additional recipes use the prescribed counts from
/// <see cref="PresetCatalog.CreatePopulations"/>, shuffled with a separate seed, so position samples
/// remain comparable across recipes. System.Random is used only during generation, never stepping.
/// Save actual initial values: a seed is not a cross-runtime reproducibility guarantee.
/// </remarks>
public static class PresetGenerator
{
    /// <summary>Builds owned initial state and directed matrix values, recording generator provenance.</summary>
    /// <param name="kind">Built-in recipe.</param>
    /// <param name="particleCount">Nonnegative count; null selects the recipe default and zero gives an empty experiment.</param>
    /// <param name="speciesCount">Compatible species count; null selects the recipe default.</param>
    /// <param name="seed">Seed passed to System.Random during generation.</param>
    /// <param name="parameters">Optional model parameters; null selects the migration defaults.</param>
    /// <param name="timeStep">Positive finite step duration in reduced time units.</param>
    /// <param name="backend">Intended neighbour search; the API defaults to the direct reference.</param>
    /// <param name="workerCount">Explicit grid workers, 1..8 including the caller; direct requires one.</param>
    /// <returns>A validated experiment containing actual initial state and matrix values.</returns>
    /// <remarks>New recipes use independent position and species streams: positions use <paramref name="seed"/>,
    /// while a balanced or ratio-weighted species array is shuffled with seed XOR 0x4A7291. Identities follow
    /// generation order. Defaults are reduced-unit parameters: world 1 by 1, cutoff 0.1, force scale 1,
    /// core fraction 0.3, damping half-life 0.025, wall width 0.05 and wall strength 1.44.</remarks>
    /// <exception cref="ArgumentException">Counts, preset, parameters, execution settings or timestep are invalid.</exception>
    public static ExperimentDefinition Create(PresetKind kind, int? particleCount = null, int? speciesCount = null,
        int seed = 1, ModelParameters? parameters = null, double timeStep = 1d / 144, ForceBackend backend = ForceBackend.Direct,
        int workerCount = 1)
    {
        PresetInfo recipe = PresetCatalog.Get(kind);
        int total = particleCount ?? recipe.DefaultParticleCount;
        int speciesTotal = speciesCount ?? recipe.DefaultSpeciesCount;
        ArgumentOutOfRangeException.ThrowIfNegative(total, nameof(particleCount));
        PresetCatalog.ValidateSpeciesCount(kind, speciesTotal);
        if (!Enum.IsDefined(backend))
        {
            throw new ArgumentOutOfRangeException(nameof(backend));
        }

        ForceWorkerConfiguration.Validate(backend, workerCount);
        parameters ??= new ModelParameters();
        parameters.Validate();
        Random random = new(seed);
        InteractionMatrix matrix = CreateMatrixValidated(kind, speciesTotal, random);
        bool historical = kind is PresetKind.Random or PresetKind.Chains or PresetKind.Snakes;

        long[] ids = new long[total];
        Vector2d[] positions = new Vector2d[total];
        Vector2d[] velocities = new Vector2d[total];
        int[] species = new int[total];
        if (!historical)
        {
            FillSpecies(species, PresetCatalog.CreatePopulations(kind, total, speciesTotal), seed);
        }

        for (int i = 0; i < total; i++)
        {
            ids[i] = i;
            positions[i] = new Vector2d(random.NextDouble() * parameters.WorldWidth, random.NextDouble() * parameters.WorldHeight);
            if (historical)
            {
                species[i] = random.Next(speciesTotal);
            }
        }

        string generatorName = historical ? $"uniform-{recipe.Key}" : $"uniform-prescribed-{recipe.Key}";
        return new ExperimentDefinition(recipe.Key, parameters, matrix,
            new ParticleSnapshot(ids, positions, velocities, species), timeStep,
            new GeneratorMetadata(generatorName, "1", seed, RuntimeInformation.FrameworkDescription), backend, workerCount);
    }

    /// <summary>Builds only the directed coefficient matrix for live edits or independent experiment composition.</summary>
    /// <param name="kind">Built-in matrix rule.</param>
    /// <param name="speciesCount">Compatible matrix dimension; see <see cref="PresetCatalog.SupportsSpeciesCount"/>.</param>
    /// <param name="seed">Seed used only by the Random matrix rule.</param>
    /// <returns>An owned immutable matrix with rows as receivers and columns as sources.</returns>
    /// <remarks>
    /// <para>All coefficients are dimensionless. Positive outer coefficients attract and negative ones repel;
    /// the common inner repulsion is unchanged. Chains uses self/adjacent +1 and other -1. Snakes uses self +1,
    /// successor +0.2 and other zero; its one-species successor takes precedence. Core and halo uses rows
    /// [0.7, 0.2] and [0.9, -0.6]. Alternating bands uses self -0.6 and cross +0.8. Ribbons uses self -0.2,
    /// both neighbours +0.8 and other -0.5. Cyclic pursuit uses self +0.3, successor +0.8 and predecessor -0.8.
    /// Species islands uses self +0.7 and other -0.35. Biased ribbons retains Ribbons' symmetric part but
    /// uses successor +0.95 and predecessor +0.65.</para>
    /// <para>Chase cycles use self 0.141/1.07, predecessor 0.929/1.07, successor -1 and other zero.
    /// These coupling ratios adapt Eq. 18 and the Movie 8 parameters of Ouazan-Reboul, Agudo-Canalejo and
    /// Golestanian (2023), <see href="https://doi.org/10.1038/s41467-023-40241-w">Self-organization of primitive
    /// metabolic cycles due to non-reciprocal interactions</see>. With their opposite displacement convention,
    /// mu_s = -0.929 and mu_p = -1.07 give our self = mu_s - mu_p, predecessor = -mu_s and successor = mu_p,
    /// normalized by 1.07. Only the coupling network is adapted: this deterministic two-dimensional,
    /// finite-range force model with soft walls does not reproduce their Brownian three-dimensional,
    /// inverse-square periodic model. The six-species recipe is a comparison using the same ratios.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The recipe or species count is invalid.</exception>
    public static InteractionMatrix CreateMatrix(PresetKind kind, int speciesCount, int seed = 1)
    {
        PresetCatalog.ValidateSpeciesCount(kind, speciesCount);
        return CreateMatrixValidated(kind, speciesCount, new Random(seed));
    }

    /// <summary>Evaluates validated rules while retaining the historical shared matrix/position random stream.</summary>
    private static InteractionMatrix CreateMatrixValidated(PresetKind kind, int speciesCount, Random random)
    {
        double[] values = new double[checked(speciesCount * speciesCount)];
        for (int receiver = 0; receiver < speciesCount; receiver++)
        {
            int next = (receiver + 1) % speciesCount;
            int previous = (receiver + speciesCount - 1) % speciesCount;
            for (int source = 0; source < speciesCount; source++)
            {
                bool self = source == receiver;
                bool adjacent = source == next || source == previous;
                values[receiver * speciesCount + source] = kind switch
                {
                    PresetKind.Random => 2 * random.NextDouble() - 1,
                    PresetKind.Chains => self || adjacent ? 1 : -1,
                    PresetKind.Snakes => source == next ? 0.2 : self ? 1 : 0,
                    PresetKind.CoreHalo => receiver == 0 ? self ? 0.7 : 0.2 : self ? -0.6 : 0.9,
                    PresetKind.AlternatingBands => self ? -0.6 : 0.8,
                    PresetKind.Ribbons => self ? -0.2 : adjacent ? 0.8 : -0.5,
                    PresetKind.CyclicPursuit => self ? 0.3 : source == next ? 0.8 : -0.8,
                    PresetKind.ChaseCycle5 or PresetKind.ChaseCycle6 => self ? 0.141 / 1.07
                        : source == previous ? 0.929 / 1.07 : source == next ? -1 : 0,
                    PresetKind.SpeciesIslands => self ? 0.7 : -0.35,
                    PresetKind.BiasedRibbons => self ? -0.2 : source == next ? 0.95 : source == previous ? 0.65 : -0.5,
                    _ => throw new ArgumentOutOfRangeException(nameof(kind))
                };
            }
        }

        return new InteractionMatrix(speciesCount, values);
    }

    /// <summary>Assigns exact per-species populations, then shuffles independently of position generation.</summary>
    private static void FillSpecies(Span<int> species, ReadOnlySpan<int> counts, int seed)
    {
        int offset = 0;
        for (int group = 0; group < counts.Length; group++)
        {
            species.Slice(offset, counts[group]).Fill(group);
            offset += counts[group];
        }

        new Random(seed ^ 0x4A7291).Shuffle(species);
    }
}
