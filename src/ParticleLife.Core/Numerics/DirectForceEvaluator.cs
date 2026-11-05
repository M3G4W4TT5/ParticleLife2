using ParticleLife.Core.Models;
using ParticleLife.Core.State;

namespace ParticleLife.Core.Numerics;

/// <summary>A serial O(N squared) reference evaluation of all directed pair and soft-wall forces.</summary>
/// <remarks>
/// All receivers read the same completed input state. Every distinct ordered pair is considered once;
/// no action-reaction shortcut is valid for a general directed matrix. Public configuration is immutable.
/// The evaluator owns no work buffers and allocates no memory on successful evaluation.
/// Separate callers may use it concurrently with independent stable input and output buffers.
/// </remarks>
public sealed class DirectForceEvaluator : IForceEvaluator
{
    private ModelParameters parameters;
    private InteractionMatrix matrix;
    private PairForceKernel kernel;

    /// <summary>Validates and retains immutable model configuration.</summary>
    /// <param name="parameters">Model values in reduced units.</param>
    /// <param name="matrix">Owned immutable directed species coefficients.</param>
    /// <exception cref="ArgumentNullException">Either configuration object is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A model parameter is invalid.</exception>
    public DirectForceEvaluator(ModelParameters parameters, InteractionMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(matrix);
        parameters.Validate();
        matrix.ValidateFor(parameters);
        this.parameters = parameters;
        this.matrix = matrix;
        kernel = new(parameters, matrix);
    }

    /// <summary>Replaces validated equations between evaluations without owning or copying state.</summary>
    /// <remarks>The simulation establishes exclusive access and validates the complete candidate first.
    /// Allocate the new immutable pair kernel before publishing any replacement reference.</remarks>
    internal void Reconfigure(ModelParameters nextParameters, InteractionMatrix nextMatrix)
    {
        PairForceKernel nextKernel = new(nextParameters, nextMatrix);
        parameters = nextParameters;
        matrix = nextMatrix;
        kernel = nextKernel;
    }

    /// <summary>Gets the direct search configuration; this evaluator never falls back or owns an index.</summary>
    public ForceExecution LastExecution => new(ForceBackend.Direct, ForceBackend.Direct, GridFallbackReason.None, default);

    /// <summary>Overwrites the supplied force buffer using one complete immutable input state.</summary>
    /// <param name="input">A valid observation whose lifetime covers the entire call.</param>
    /// <param name="forces">Caller-owned output of exactly input.Count forces in M L/T^2; must not alias state.</param>
    /// <returns>Candidate, accepted and exact-overlap counts for the input state.</returns>
    /// <remarks>
    /// <para>For displacement d = x_j - x_i and distance r, F_ij = F0 g(r/R,A[s_i,s_j]) d/r.
    /// Exactly equal positions contribute zero and are counted once per unordered overlap.
    /// Finite off-world positions are retained. Squared distances reject pairs before square roots;
    /// scaled magnitude is used for subnormal squared separations to distinguish tiny distances
    /// from exact coincidence. Subnormal distances or distance ratios use a scaled core-distance
    /// calculation so rounding cannot erase the repulsive-core fraction. When the squared cutoff
    /// is subnormal, rejection also uses the scaled magnitude because squaring can erase the
    /// separation from the cutoff. Floating-point sums
    /// follow source storage order.</para>
    /// <para>The input is never mutated. On a numerical failure, the output buffer may be partially
    /// written and must not be published. No I/O, reporting, allocation or integration occurs here.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">The output length, aliasing, finite state or species is invalid.</exception>
    /// <exception cref="ArithmeticException">An accumulated particle force becomes non-finite.</exception>
    public ForceEvaluationStatistics Evaluate(ParticleStateView input, Span<Vector2d> forces)
    {
        ValidateOutput(input, forces);
        ValidateState(input, matrix.SpeciesCount);
        ReadOnlySpan<Vector2d> positions = input.Positions;
        ReadOnlySpan<int> species = input.Species;
        long acceptedPairs = 0;
        long overlapPairs = 0;
        for (int receiver = 0; receiver < input.Count; receiver++)
        {
            Vector2d totalForce = default;
            int row = species[receiver] * matrix.SpeciesCount;
            for (int source = 0; source < input.Count; source++)
            {
                if (source == receiver)
                {
                    continue;
                }

                PairKind kind = kernel.Evaluate(WorldBoundary.Displacement(positions[receiver], positions[source], parameters),
                    row + species[source], out Vector2d pairForce);
                if (kind == PairKind.Accepted)
                {
                    acceptedPairs++;
                    totalForce += pairForce;
                }
                else if (kind == PairKind.Overlap && source > receiver)
                {
                    overlapPairs++;
                }
            }

            totalForce += SoftWall.EvaluateValidated(positions[receiver], parameters);
            if (!totalForce.IsFinite)
            {
                throw new ArithmeticException($"Force on particle {input.Ids[receiver]} is non-finite; reduce the force scale or state magnitude.");
            }

            forces[receiver] = totalForce;
        }

        return new((long)input.Count * (input.Count - 1), acceptedPairs, overlapPairs);
    }

    /// <summary>Rejects wrong output lengths or aliasing before either evaluator writes force values.</summary>
    internal static void ValidateOutput(ParticleStateView input, Span<Vector2d> forces)
    {
        if (forces.Length != input.Count)
        {
            throw new ArgumentException("The force buffer must contain exactly one entry per particle.", nameof(forces));
        }

        if (input.Positions.Overlaps(forces) || input.Velocities.Overlaps(forces))
        {
            throw new ArgumentException("The force buffer must not alias input state.", nameof(forces));
        }
    }

    /// <summary>Checks model-dependent species bounds and numerical state before any stage writes output.</summary>
    internal static void ValidateState(ParticleStateView input, int speciesCount)
    {
        for (int i = 0; i < input.Count; i++)
        {
            if (!input.Positions[i].IsFinite || !input.Velocities[i].IsFinite)
            {
                throw new ArgumentException($"Particle {input.Ids[i]} has a non-finite position or velocity.", nameof(input));
            }

            if ((uint)input.Species[i] >= (uint)speciesCount)
            {
                throw new ArgumentException($"Particle {input.Ids[i]} has species {input.Species[i]} outside the interaction matrix.", nameof(input));
            }
        }
    }
}
