using System.Runtime.InteropServices;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Core.Experiments;

/// <summary>Selects a documented initial species matrix, with uniformly distributed initial positions.</summary>
public enum PresetKind
{
    /// <summary>Independently samples each directed coefficient uniformly in [-1, 1).</summary>
    Random,

    /// <summary>Attracts the same and both adjacent species in a cyclic chain; repels all others.</summary>
    Chains,

    /// <summary>Attracts its own species with 1 and its successor with 0.2; other coefficients are zero.</summary>
    Snakes
}

/// <summary>Creates seeded initial conditions for the three historical Particle Life matrix presets.</summary>
/// <remarks>
/// All positions are independent uniform samples over the rectangular world, all velocities are zero,
/// species are uniform integer samples, and identities follow generation order. System.Random is used
/// only here, never during stepping. Persist the returned values: runtime-dependent generation is not
/// a cross-runtime reproducibility guarantee. The chain and snake rules preserve the original matrix
/// definition; for a one-species snake, successor assignment takes precedence and the sole entry is 0.2.
/// </remarks>
public static class PresetGenerator
{
    /// <summary>Builds owned initial state and directed matrix values, recording generator provenance.</summary>
    /// <param name="kind">Random, chains or snakes matrix rule.</param>
    /// <param name="particleCount">Nonnegative particle count; zero produces an empty experiment.</param>
    /// <param name="speciesCount">Positive species count.</param>
    /// <param name="seed">Seed passed to System.Random during generation.</param>
    /// <param name="parameters">Optional model parameters; null selects the migration defaults.</param>
    /// <param name="timeStep">Positive finite step duration in reduced time units.</param>
    /// <param name="backend">Intended neighbour search; the API defaults to the direct reference.</param>
    /// <returns>A validated experiment containing actual initial state and matrix values.</returns>
    /// <exception cref="ArgumentException">Counts, preset, parameters or timestep are invalid.</exception>
    public static ExperimentDefinition Create(PresetKind kind, int particleCount = 500, int speciesCount = 7,
        int seed = 1, ModelParameters? parameters = null, double timeStep = 1d / 144, ForceBackend backend = ForceBackend.Direct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(particleCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(speciesCount, 1);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (!Enum.IsDefined(backend))
        {
            throw new ArgumentOutOfRangeException(nameof(backend));
        }

        parameters ??= new ModelParameters();
        parameters.Validate();
        Random random = new(seed);
        double[] values = new double[checked(speciesCount * speciesCount)];
        for (int receiver = 0; receiver < speciesCount; receiver++)
        {
            for (int source = 0; source < speciesCount; source++)
            {
                values[receiver * speciesCount + source] = kind switch
                {
                    PresetKind.Random => 2 * random.NextDouble() - 1,
                    PresetKind.Chains => source == receiver || source == (receiver + 1) % speciesCount
                        || source == (receiver + speciesCount - 1) % speciesCount ? 1 : -1,
                    PresetKind.Snakes => source == (receiver + 1) % speciesCount ? 0.2 : source == receiver ? 1 : 0,
                    _ => throw new ArgumentOutOfRangeException(nameof(kind))
                };
            }
        }

        long[] ids = new long[particleCount];
        Vector2d[] positions = new Vector2d[particleCount];
        Vector2d[] velocities = new Vector2d[particleCount];
        int[] species = new int[particleCount];
        for (int i = 0; i < particleCount; i++)
        {
            ids[i] = i;
            positions[i] = new Vector2d(random.NextDouble() * parameters.WorldWidth, random.NextDouble() * parameters.WorldHeight);
            species[i] = random.Next(speciesCount);
        }

        return new ExperimentDefinition(kind.ToString().ToLowerInvariant(), parameters,
            new InteractionMatrix(speciesCount, values), new ParticleSnapshot(ids, positions, velocities, species), timeStep,
            new GeneratorMetadata($"uniform-{kind.ToString().ToLowerInvariant()}", "1", seed, RuntimeInformation.FrameworkDescription), backend);
    }
}
