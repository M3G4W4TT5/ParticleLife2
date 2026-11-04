using ParticleLife.Core.State;

namespace ParticleLife.Core.Numerics;

/// <summary>Evaluates one completed state using either direct or uniform-grid exact neighbour discovery.</summary>
/// <remarks>
/// Output is caller owned and must not alias input state. Evaluation never integrates or mutates input.
/// On failure output may be partially written; hosts publish only after all numerical stages succeed.
/// Implementations allocate no managed memory after their particle capacity has been established.
/// </remarks>
public interface IForceEvaluator
{
    /// <summary>Gets execution configuration from the last successful evaluation.</summary>
    /// <remarks>Before the first evaluation this gives configured defaults, not evidence of a completed step.</remarks>
    ForceExecution LastExecution { get; }

    /// <summary>Overwrites one force per particle from the same stable, complete input state.</summary>
    /// <param name="input">Valid completed positions, velocities, identities and species.</param>
    /// <param name="forces">Independent output with exactly input.Count entries, in reduced force units.</param>
    /// <returns>Directed candidate/accepted counts and unordered exact-overlap count.</returns>
    /// <exception cref="ArgumentException">The input, output size or aliasing violates the numerical contract.</exception>
    /// <exception cref="ArithmeticException">An accumulated force is non-finite.</exception>
    ForceEvaluationStatistics Evaluate(ParticleStateView input, Span<Vector2d> forces);
}
