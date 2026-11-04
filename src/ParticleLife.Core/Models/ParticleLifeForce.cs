namespace ParticleLife.Core.Models;

/// <summary>The dimensionless short-range repulsion and species-dependent attraction curve.</summary>
public static class ParticleLifeForce
{
    /// <summary>Evaluates the Particle Life radial coefficient, not a force or potential energy.</summary>
    /// <param name="distanceRatio">Finite nonnegative separation q = r/R.</param>
    /// <param name="attraction">Directed dimensionless coefficient A[receiver,source] in [-1, 1].</param>
    /// <param name="repulsionFraction">Repulsive-core fraction beta, strictly between zero and one.</param>
    /// <returns>A dimensionless coefficient; positive attracts and negative repels.</returns>
    /// <remarks>
    /// <para>For 0 &lt; q &lt; beta, g = q/beta - 1. For beta &lt;= q &lt; 1,
    /// g = a (1 - |2q - 1 - beta|/(1 - beta)). At and beyond q = 1, g = 0.
    /// The outer triangle is evaluated in algebraically equivalent pieces to avoid cancellation
    /// at q = beta and q = 1. At exactly q = 0, this function returns zero to match the model's
    /// undefined-direction policy; the limiting coefficient as q approaches zero from above is -1.</para>
    /// <para>The pair force is F0 g times the unit vector from receiver to source. No extra
    /// saturation is applied. Evaluation does not mutate state or allocate on valid inputs.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Any argument is outside its documented finite range.</exception>
    public static double RadialCoefficient(double distanceRatio, double attraction, double repulsionFraction)
    {
        if (!double.IsFinite(distanceRatio) || distanceRatio < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(distanceRatio), "Distance ratio must be finite and nonnegative.");
        }

        if (!double.IsFinite(attraction) || attraction < -1 || attraction > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attraction), "Attraction must be finite and in [-1, 1].");
        }

        if (!double.IsFinite(repulsionFraction) || repulsionFraction <= 0 || repulsionFraction >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(repulsionFraction), "Repulsion fraction must be strictly between zero and one.");
        }

        return EvaluateValidated(distanceRatio, attraction, repulsionFraction);
    }

    /// <summary>Evaluates a validated coefficient without repeated parameter checks in the pair loop.</summary>
    /// <remarks>Both branches of the outer triangle vanish exactly at their endpoint in floating-point arithmetic.</remarks>
    internal static double EvaluateValidated(double distanceRatio, double attraction, double repulsionFraction)
    {
        if (distanceRatio == 0 || distanceRatio >= 1)
        {
            return 0;
        }

        if (distanceRatio < repulsionFraction)
        {
            return distanceRatio / repulsionFraction - 1;
        }

        double peak = (1 + repulsionFraction) / 2;
        double triangle = distanceRatio <= peak
            ? 2 * ((distanceRatio - repulsionFraction) / (1 - repulsionFraction))
            : 2 * ((1 - distanceRatio) / (1 - repulsionFraction));
        return attraction * triangle;
    }
}
