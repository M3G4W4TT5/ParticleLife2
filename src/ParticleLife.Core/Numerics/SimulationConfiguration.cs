using ParticleLife.Core.Models;

namespace ParticleLife.Core.Numerics;

/// <summary>Owns the validated, immutable physical and numerical settings applied at a completed boundary.</summary>
/// <remarks>Configuration contains no particle state or wall-clock playback settings. A timestep is an
/// increment for subsequent steps; changing it never recomputes elapsed time from the completed-step count.</remarks>
public sealed class SimulationConfiguration
{
    /// <summary>Validates a complete candidate before any simulation state or resources can change.</summary>
    /// <param name="parameters">Immutable model quantities in reduced units.</param>
    /// <param name="matrix">Immutable directed receiver-row/source-column coefficients.</param>
    /// <param name="timeStep">Positive finite integration increment in reduced time units.</param>
    /// <param name="backend">Direct reference or exact uniform-grid enumeration.</param>
    /// <param name="workerCount">Workers including the caller, from one through 64; direct requires one.</param>
    /// <exception cref="ArgumentException">A configuration object or numerical setting is invalid.</exception>
    public SimulationConfiguration(ModelParameters parameters, InteractionMatrix matrix, double timeStep,
        ForceBackend backend = ForceBackend.Direct, int workerCount = 1)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(matrix);
        parameters.Validate();
        ForceWorkerConfiguration.Validate(backend, workerCount);
        _ = SemiImplicitEuler.DampingFactor(timeStep, parameters.DampingHalfLife);
        Parameters = parameters;
        Matrix = matrix;
        TimeStep = timeStep;
        Backend = backend;
        WorkerCount = workerCount;
    }

    /// <summary>Gets the immutable physical quantities and nominal soft-wall rectangle.</summary>
    public ModelParameters Parameters { get; }

    /// <summary>Gets the immutable directed species coefficients.</summary>
    public InteractionMatrix Matrix { get; }

    /// <summary>Gets the integration increment in reduced time units for subsequent steps.</summary>
    public double TimeStep { get; }

    /// <summary>Gets the requested exact neighbour-search implementation.</summary>
    public ForceBackend Backend { get; }

    /// <summary>Gets the requested worker count, including the caller.</summary>
    public int WorkerCount { get; }

    /// <summary>Compares complete values, including matrix contents, without copying particle or matrix storage.</summary>
    /// <param name="other">Another candidate, or null.</param>
    /// <returns>True when both configurations have identical physical and numerical values.</returns>
    public bool EquivalentTo(SimulationConfiguration? other) => other is not null
        && Parameters == other.Parameters && TimeStep == other.TimeStep
        && Backend == other.Backend && WorkerCount == other.WorkerCount
        && Matrix.SpeciesCount == other.Matrix.SpeciesCount
        && (ReferenceEquals(Matrix, other.Matrix) || Matrix.Values.SequenceEqual(other.Matrix.Values));
}
