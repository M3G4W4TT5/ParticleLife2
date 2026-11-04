using System.Runtime.InteropServices;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.State;

namespace ParticleLife.Viewer;

/// <summary>Creates explicit experiment variations without mutating the source or advancing its simulation.</summary>
/// <remarks>These allocating operations belong between completed steps. Persist returned experiments:
/// actual generated values are authoritative, while System.Random seeds only record provenance.</remarks>
public static class ExperimentEditing
{
    /// <summary>Resamples uniform positions and zero velocities, preserving particle identities and species.</summary>
    /// <param name="experiment">Source whose exact model, matrix, counts and timestep are retained.</param>
    /// <param name="seed">New System.Random seed recorded with the generated initial values.</param>
    /// <returns>A time-zero experiment with the same immutable matrix and model and newly owned initial state.</returns>
    /// <remarks>Positions are independent uniform samples in the nominal rectangular world. This operation
    /// does not sample a new matrix or change species assignments. Reset subsequently returns these values.</remarks>
    public static ExperimentDefinition ResampleState(ExperimentDefinition experiment, int seed)
    {
        ArgumentNullException.ThrowIfNull(experiment);
        ParticleSnapshot initial = experiment.InitialState;
        Random random = new(seed);
        Vector2d[] positions = new Vector2d[initial.Count];
        for (int i = 0; i < positions.Length; i++)
        {
            positions[i] = new Vector2d(random.NextDouble() * experiment.Parameters.WorldWidth,
                random.NextDouble() * experiment.Parameters.WorldHeight);
        }

        ParticleSnapshot resampled = new(initial.Ids, positions, new Vector2d[initial.Count], initial.Species);
        return new ExperimentDefinition(experiment.Name, experiment.Parameters, experiment.Matrix,
            resampled, experiment.TimeStep, Metadata("viewer-uniform-state-resample", seed));
    }

    /// <summary>Samples a new directed matrix while retaining the exact saved initial state.</summary>
    /// <param name="experiment">Source whose model, timestep and original initial values are retained.</param>
    /// <param name="seed">New System.Random seed recorded with the generated matrix values.</param>
    /// <returns>A time-zero experiment with independent uniform coefficients in [-1, 1).</returns>
    /// <remarks>This changes matrix values only. Replacing playback with the result restarts from the
    /// saved initial conditions, not the currently evolved positions or a checkpoint.</remarks>
    public static ExperimentDefinition RandomizeMatrix(ExperimentDefinition experiment, int seed)
    {
        ArgumentNullException.ThrowIfNull(experiment);
        Random random = new(seed);
        double[] values = new double[experiment.Matrix.Values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = 2 * random.NextDouble() - 1;
        }

        return new ExperimentDefinition(experiment.Name, experiment.Parameters,
            new InteractionMatrix(experiment.Matrix.SpeciesCount, values), experiment.InitialState,
            experiment.TimeStep, Metadata("viewer-random-matrix", seed));
    }

    private static GeneratorMetadata Metadata(string name, int seed) =>
        new(name, "1", seed, RuntimeInformation.FrameworkDescription);
}
