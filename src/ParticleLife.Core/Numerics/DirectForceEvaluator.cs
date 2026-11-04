using ParticleLife.Core.Models;
using ParticleLife.Core.State;

namespace ParticleLife.Core.Numerics;

/// <summary>A serial O(N squared) reference evaluation of all directed pair and soft-wall forces.</summary>
/// <remarks>
/// All receivers read the same completed input state. Every distinct ordered pair is considered once;
/// no action-reaction shortcut is valid for a general directed matrix. Configuration is immutable.
/// The evaluator owns no work buffers and allocates no memory on successful evaluation.
/// Separate callers may use it concurrently with independent stable input and output buffers.
/// </remarks>
public sealed class DirectForceEvaluator
{
    private const double SmallestNormal = 2.2250738585072014E-308;
    private readonly ModelParameters parameters;
    private readonly InteractionMatrix matrix;
    private readonly double cutoffSquared;

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
        this.parameters = parameters;
        this.matrix = matrix;
        cutoffSquared = parameters.Cutoff * parameters.Cutoff;
    }

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
        if (forces.Length != input.Count)
        {
            throw new ArgumentException("The force buffer must contain exactly one entry per particle.", nameof(forces));
        }

        if (input.Positions.Overlaps(forces) || input.Velocities.Overlaps(forces))
        {
            throw new ArgumentException("The force buffer must not alias input state.", nameof(forces));
        }

        ValidateState(input, matrix.SpeciesCount);
        ReadOnlySpan<Vector2d> positions = input.Positions;
        ReadOnlySpan<int> species = input.Species;
        ReadOnlySpan<double> coefficients = matrix.Values;
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

                Vector2d displacement = positions[source] - positions[receiver];
                if (displacement.X == 0 && displacement.Y == 0)
                {
                    if (source > receiver)
                    {
                        overlapPairs++;
                    }

                    continue;
                }

                // An overflowing difference of finite coordinates is necessarily beyond a finite cutoff.
                if (Math.Abs(displacement.X) >= parameters.Cutoff || Math.Abs(displacement.Y) >= parameters.Cutoff)
                {
                    continue;
                }

                double distanceSquared = displacement.X * displacement.X + displacement.Y * displacement.Y;
                if (cutoffSquared >= SmallestNormal && distanceSquared >= cutoffSquared)
                {
                    continue;
                }

                double distance;
                Vector2d direction;
                if (distanceSquared < SmallestNormal)
                {
                    // Normalize before rescaling: a rounded subnormal distance must not distort direction.
                    double largestComponent = Math.Max(Math.Abs(displacement.X), Math.Abs(displacement.Y));
                    Vector2d scaled = displacement / largestComponent;
                    double scaledLength = Math.Sqrt(scaled.X * scaled.X + scaled.Y * scaled.Y);
                    distance = largestComponent * scaledLength;
                    direction = scaled / scaledLength;
                }
                else
                {
                    distance = Math.Sqrt(distanceSquared);
                    direction = displacement / distance;
                }

                if (distance >= parameters.Cutoff)
                {
                    continue;
                }

                acceptedPairs++;
                double distanceRatio = distance / parameters.Cutoff;
                double attraction = coefficients[row + species[source]];
                double coefficient = distance < SmallestNormal || distanceRatio < SmallestNormal
                    ? EvaluateTinyDistanceCoefficient(displacement, attraction)
                    : ParticleLifeForce.EvaluateValidated(distanceRatio, attraction, parameters.RepulsionFraction);
                double magnitude = parameters.ForceScale * coefficient;
                totalForce += direction * magnitude;
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

    /// <summary>Evaluates a tiny separation without first rounding its distance or normalized distance to a subnormal value.</summary>
    /// <remarks>
    /// <para>Compute r/(R beta) by dividing displacement components by beta before taking their
    /// magnitude. This preserves a finite core fraction even when r/R rounds to zero or beta.
    /// Normalizing an already rounded subnormal scalar distance would also lose diagonal geometry.</para>
    /// <para>The caller uses this only for subnormal r or r/R. With 0 &lt; beta &lt; 1 and a positive
    /// finite R squared, these scaled components and their final ratio remain finite. In either
    /// case r/R is far below the outer triangle's peak, so its rising branch can be expressed as
    /// 2 beta (r/(R beta) - 1)/(1 - beta). Multiplying by beta last avoids premature underflow.</para>
    /// </remarks>
    private double EvaluateTinyDistanceCoefficient(Vector2d displacement, double attraction)
    {
        double beta = parameters.RepulsionFraction;
        double coreRatio = double.Hypot(displacement.X / beta, displacement.Y / beta) / parameters.Cutoff;
        return coreRatio < 1
            ? coreRatio - 1
            : attraction * (2 * (coreRatio - 1) * beta / (1 - beta));
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
