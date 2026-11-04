using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Core.Experiments;

/// <summary>Owns a validated, immutable description of an experiment starting at time and step zero.</summary>
/// <remarks>
/// Positions, velocities and parameters use the reduced units documented by <see cref="ModelParameters"/>.
/// The actual matrix and initial state are authoritative; generator metadata alone cannot reproduce a run.
/// This version describes initial conditions, not a checkpoint of an evolving run.
/// </remarks>
public sealed class ExperimentDefinition
{
    /// <summary>Creates a validated experiment using already owned immutable state and matrix values.</summary>
    /// <param name="name">Nonempty human-readable experiment name.</param>
    /// <param name="parameters">Validated model quantities in reduced units.</param>
    /// <param name="matrix">Directed matrix indexed by receiver species then source species.</param>
    /// <param name="initialState">Actual initial positions, velocities, identities and species at time zero.</param>
    /// <param name="timeStep">Positive finite integration timestep in reduced time units.</param>
    /// <param name="generator">Generator identity, version, seed and runtime provenance.</param>
    /// <exception cref="ArgumentException">Metadata, species, parameters or initial time are invalid.</exception>
    public ExperimentDefinition(string name, ModelParameters parameters, InteractionMatrix matrix,
        ParticleSnapshot initialState, double timeStep, GeneratorMetadata generator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(matrix);
        ArgumentNullException.ThrowIfNull(initialState);
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentException.ThrowIfNullOrWhiteSpace(generator.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(generator.Version);
        ArgumentException.ThrowIfNullOrWhiteSpace(generator.Runtime);
        parameters.Validate();
        _ = SemiImplicitEuler.DampingFactor(timeStep, parameters.DampingHalfLife);
        if (initialState.Time != 0 || initialState.Step != 0)
        {
            throw new ArgumentException("An experiment must start at time and step zero; a run result is not an experiment.", nameof(initialState));
        }

        foreach (int species in initialState.Species)
        {
            if (species >= matrix.SpeciesCount)
            {
                throw new ArgumentException("Every particle species must have a row and column in the matrix.", nameof(initialState));
            }
        }

        Name = name;
        Parameters = parameters;
        Matrix = matrix;
        InitialState = initialState;
        TimeStep = timeStep;
        Generator = generator;
    }

    /// <summary>Gets the experiment's human-readable name.</summary>
    public string Name { get; }

    /// <summary>Gets immutable model quantities and soft-wall boundary dimensions.</summary>
    public ModelParameters Parameters { get; }

    /// <summary>Gets the saved directed matrix.</summary>
    public InteractionMatrix Matrix { get; }

    /// <summary>Gets the owned immutable initial-state snapshot.</summary>
    public ParticleSnapshot InitialState { get; }

    /// <summary>Gets the fixed timestep in reduced time units.</summary>
    public double TimeStep { get; }

    /// <summary>Gets provenance for the actual saved initial conditions.</summary>
    public GeneratorMetadata Generator { get; }

    /// <summary>Creates an independent simulation with owned reusable buffers.</summary>
    /// <returns>A simulation at the saved initial conditions; construction allocates outside the stepping loop.</returns>
    public Simulation CreateSimulation() => new(InitialState, Parameters, Matrix, TimeStep);
}
