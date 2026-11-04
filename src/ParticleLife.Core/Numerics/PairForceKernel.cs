using ParticleLife.Core.Models;
using ParticleLife.Core.State;

namespace ParticleLife.Core.Numerics;

/// <summary>Shares only pair mathematics between independent direct and grid neighbour enumerations.</summary>
/// <remarks>
/// For displacement d = x_source - x_receiver and r = |d|, the directed force is
/// F0 g(r/R, A[receiverSpecies, sourceSpecies]) d/r in M L/T^2. A zero displacement
/// has no defined direction and contributes zero. Accepted nonzero pairs have r strictly below R,
/// including pairs with zero coefficients. The kernel has immutable configuration and no work buffers.
/// </remarks>
internal sealed class PairForceKernel(ModelParameters parameters, InteractionMatrix matrix)
{
    internal const double SmallestNormal = 2.2250738585072014E-308;
    private readonly double cutoffSquared = parameters.Cutoff * parameters.Cutoff;

    /// <summary>Classifies a distinct ordered pair and computes its force without allocation.</summary>
    /// <remarks>
    /// Finite-coordinate subtraction can overflow only for a separation beyond the finite cutoff.
    /// Squared-distance rejection is disabled for subnormal squared cutoffs because rounding can erase
    /// the gap between a valid neighbour and the cutoff. Tiny displacements are normalized before
    /// their magnitude is rescaled, avoiding a rounded subnormal length distorting the unit direction.
    /// </remarks>
    internal PairKind Evaluate(Vector2d displacement, int coefficientIndex, out Vector2d force)
    {
        force = default;
        if (displacement.X == 0 && displacement.Y == 0)
        {
            return PairKind.Overlap;
        }

        if (Math.Abs(displacement.X) >= parameters.Cutoff || Math.Abs(displacement.Y) >= parameters.Cutoff)
        {
            return PairKind.Rejected;
        }

        double distanceSquared = displacement.X * displacement.X + displacement.Y * displacement.Y;
        if (cutoffSquared >= SmallestNormal && distanceSquared >= cutoffSquared)
        {
            return PairKind.Rejected;
        }

        double distance;
        Vector2d direction;
        if (distanceSquared < SmallestNormal)
        {
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
            return PairKind.Rejected;
        }

        double distanceRatio = distance / parameters.Cutoff;
        double attraction = matrix.Values[coefficientIndex];
        double coefficient = distance < SmallestNormal || distanceRatio < SmallestNormal
            ? EvaluateTinyDistanceCoefficient(displacement, attraction)
            : ParticleLifeForce.EvaluateValidated(distanceRatio, attraction, parameters.RepulsionFraction);
        force = direction * (parameters.ForceScale * coefficient);
        return PairKind.Accepted;
    }

    /// <summary>Preserves the core fraction without first rounding distance or normalized distance to a subnormal scalar.</summary>
    /// <remarks>
    /// Compute r/(R beta) by dividing displacement components by beta before their magnitude.
    /// This preserves diagonal geometry and a finite core fraction when r/R rounds to zero or beta.
    /// Only subnormal r or r/R reaches this path; with 0 &lt; beta &lt; 1 and positive finite R squared,
    /// these components and the final ratio remain finite. Above the core, this lies below the triangle
    /// peak, where g = a * 2 beta (r/(R beta) - 1)/(1 - beta); multiplying beta last avoids early underflow.
    /// </remarks>
    private double EvaluateTinyDistanceCoefficient(Vector2d displacement, double attraction)
    {
        double beta = parameters.RepulsionFraction;
        double coreRatio = double.Hypot(displacement.X / beta, displacement.Y / beta) / parameters.Cutoff;
        return coreRatio < 1
            ? coreRatio - 1
            : attraction * (2 * (coreRatio - 1) * beta / (1 - beta));
    }
}

/// <summary>Distinguishes cutoff rejection, accepted nonzero separation and exact coincidence.</summary>
internal enum PairKind
{
    Rejected,
    Accepted,
    Overlap
}
