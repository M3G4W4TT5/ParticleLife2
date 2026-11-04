using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Core.Experiments;

/// <summary>An immutable physical or numerical change actually applied at a completed-step boundary.</summary>
/// <remarks>List order is authoritative for multiple changes at one boundary. Ordinary changes retain
/// state and clock; structural changes carry exact values, including generated additions, rather than
/// instructions to rerun a generator. Undo grouping is a separate editor concern.</remarks>
public sealed class SessionEvent
{
    /// <summary>Creates a validated event without applying it.</summary>
    /// <param name="boundaryStep">Nonnegative completed-step count before the new settings take effect.</param>
    /// <param name="configuration">Complete settings for subsequent steps.</param>
    /// <param name="replacementState">Optional exact structural state at this boundary, retaining its clock.</param>
    /// <param name="label">Nonempty human-readable operation description, not executable instructions.</param>
    /// <param name="generator">Optional generation identity, version, seed and runtime for matrix generation,
    /// structural additions or resampling. Actual saved values remain authoritative; replay never invokes this generator.</param>
    /// <exception cref="ArgumentException">The boundary, structural clock/species or label is invalid.</exception>
    public SessionEvent(long boundaryStep, SimulationConfiguration configuration,
        ParticleSnapshot? replacementState = null, string label = "Edit", GeneratorMetadata? generator = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(boundaryStep);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (generator is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(generator.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(generator.Version);
            ArgumentException.ThrowIfNullOrWhiteSpace(generator.Runtime);
        }

        if (replacementState is not null)
        {
            if (replacementState.Step != boundaryStep || (boundaryStep == 0 && replacementState.Time != 0)
                || (boundaryStep > 0 && replacementState.Time == 0))
            {
                throw new ArgumentException("A structural payload must have the event's completed boundary clock.", nameof(replacementState));
            }

            DirectForceEvaluator.ValidateState(replacementState.Observe(), configuration.Matrix.SpeciesCount);
        }

        BoundaryStep = boundaryStep;
        Configuration = configuration;
        ReplacementState = replacementState;
        Label = label;
        Generator = generator;
    }

    /// <summary>Gets the completed-step boundary, independent of playback timing.</summary>
    public long BoundaryStep { get; }

    /// <summary>Gets the complete immutable settings that followed this edit.</summary>
    public SimulationConfiguration Configuration { get; }

    /// <summary>Gets the exact structural payload, or null when all particle values are preserved.</summary>
    public ParticleSnapshot? ReplacementState { get; }

    /// <summary>Gets the descriptive operation label.</summary>
    public string Label { get; }

    /// <summary>Gets optional provenance for actual generated values, never a recipe used during replay.</summary>
    public GeneratorMetadata? Generator { get; }
}
