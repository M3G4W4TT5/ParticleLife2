using ParticleLife.Core.Models;
using ParticleLife.Core.State;

namespace ParticleLife.Core.Numerics;

/// <summary>The first-order damped semi-implicit Euler method for unit-mass particles.</summary>
public static class SemiImplicitEuler
{
    /// <summary>Computes the exact force-free velocity decay factor for a timestep.</summary>
    /// <param name="timeStep">Positive finite timestep in reduced time units.</param>
    /// <param name="halfLife">Positive finite velocity half-life in the same units, or null for no damping.</param>
    /// <returns>2^(-timeStep/halfLife), or one when damping is disabled.</returns>
    /// <remarks>Underflow to zero represents decay below double precision. This calculation allocates no memory.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The timestep or a supplied half-life is invalid.</exception>
    public static double DampingFactor(double timeStep, double? halfLife)
    {
        ModelParameters.RequirePositiveFinite(timeStep, nameof(timeStep));
        if (halfLife is not double value)
        {
            return 1;
        }

        ModelParameters.RequirePositiveFinite(value, nameof(halfLife));
        return Math.Pow(2, -timeStep / value);
    }

    /// <summary>Advances all particles from consistent input into separate caller-owned buffers.</summary>
    /// <param name="input">Completed state with positions in L and velocities in L/T.</param>
    /// <param name="forces">Total forces in M L/T^2, one per particle, evaluated from input.</param>
    /// <param name="timeStep">Positive finite timestep dt in T.</param>
    /// <param name="dampingFactor">Finite decay factor in [0, 1], normally from <see cref="DampingFactor"/>.</param>
    /// <param name="outputPositions">Exactly input.Count positions; must not overlap either input, forces or velocities output.</param>
    /// <param name="outputVelocities">Exactly input.Count velocities; must not overlap either input, forces or positions output.</param>
    /// <remarks>
    /// <para>With mass = 1 M, v_next = dampingFactor * v + dt * F/m and
    /// x_next = x + dt * v_next. The force kick is not multiplied by dampingFactor.
    /// Position integration is first order even though force-free velocity damping is exact.</para>
    /// <para>No state, time or buffer ownership is committed here. On failure output may be partially
    /// written; publish it only after a successful return. Valid calls allocate no managed memory.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">Buffer lengths or aliasing violate the contract.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The timestep or damping factor is invalid.</exception>
    /// <exception cref="ArithmeticException">An input force or resulting state is non-finite.</exception>
    public static void Integrate(ParticleStateView input, ReadOnlySpan<Vector2d> forces,
        double timeStep, double dampingFactor, Span<Vector2d> outputPositions, Span<Vector2d> outputVelocities)
    {
        ModelParameters.RequirePositiveFinite(timeStep, nameof(timeStep));
        if (!double.IsFinite(dampingFactor) || dampingFactor < 0 || dampingFactor > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(dampingFactor), "Damping factor must be finite and in [0, 1].");
        }

        if (forces.Length != input.Count || outputPositions.Length != input.Count || outputVelocities.Length != input.Count)
        {
            throw new ArgumentException("Each numerical buffer must contain exactly one value per particle.");
        }

        if (input.Positions.Overlaps(outputPositions) || input.Positions.Overlaps(outputVelocities)
            || input.Velocities.Overlaps(outputPositions) || input.Velocities.Overlaps(outputVelocities)
            || forces.Overlaps(outputPositions) || forces.Overlaps(outputVelocities)
            || outputPositions.Overlaps(outputVelocities))
        {
            throw new ArgumentException("Integration outputs must not overlap inputs or each other.");
        }

        for (int i = 0; i < input.Count; i++)
        {
            Vector2d velocity = input.Velocities[i] * dampingFactor + forces[i] * timeStep;
            Vector2d position = input.Positions[i] + velocity * timeStep;
            if (!forces[i].IsFinite || !velocity.IsFinite || !position.IsFinite)
            {
                throw new ArithmeticException($"Integration for particle {input.Ids[i]} is non-finite; reduce the timestep or state magnitude.");
            }

            outputVelocities[i] = velocity;
            outputPositions[i] = position;
        }
    }
}
