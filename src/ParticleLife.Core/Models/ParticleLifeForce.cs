namespace ParticleLife.Core.Models;

/// <summary>The dimensionless short-range repulsion and species-dependent attraction curve.</summary>
public static class ParticleLifeForce
{
    /// <summary>Evaluates the directed per-pair curve using physical core and cutoff radii.</summary>
    /// <param name="distance">Finite nonnegative separation r in reduced length units.</param>
    /// <param name="attraction">Directed dimensionless coefficient in [-1,1].</param>
    /// <param name="coreRadius">Finite core radius c, with 0 &lt;= c &lt; cutoff.</param>
    /// <param name="cutoff">Positive finite outer radius R with a positive finite square.</param>
    /// <returns>A dimensionless coefficient; multiply by force scale and the directed unit displacement.</returns>
    /// <remarks>Below a positive core, g=r/c-1. From c to R, g is an outer triangle peaking
    /// at (c+R)/2 with height attraction. A zero core removes the repulsion branch. At r=0 and
    /// r&gt;=R, g=0. The branches preserve zero exactly at the core and cutoff. Global beta-mode
    /// uses <see cref="RadialCoefficient"/> to retain its original arithmetic convention.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside its supported range.</exception>
    public static double RadialCoefficientAtDistance(double distance, double attraction, double coreRadius, double cutoff)
    {
        ModelParameters.RequirePositiveFinite(cutoff, nameof(cutoff));
        ModelParameters.RequirePositiveFinite(cutoff * cutoff, nameof(cutoff));
        if (!double.IsFinite(distance) || distance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(distance));
        }
        if (!double.IsFinite(attraction) || attraction < -1 || attraction > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attraction));
        }
        if (!double.IsFinite(coreRadius) || coreRadius < 0 || coreRadius >= cutoff)
        {
            throw new ArgumentOutOfRangeException(nameof(coreRadius));
        }
        return EvaluatePairRangeValidated(distance, attraction, coreRadius, cutoff);
    }

    /// <summary>Uses separate triangle branches to preserve tiny endpoint differences.</summary>
    internal static double EvaluatePairRangeValidated(double distance, double attraction, double core, double cutoff)
    {
        if (distance == 0 || distance >= cutoff)
        {
            return 0;
        }
        if (distance < core)
        {
            return distance / core - 1;
        }
        double width = cutoff - core;
        double triangle = distance <= core + width / 2
            ? 2 * ((distance - core) / width) : 2 * ((cutoff - distance) / width);
        return attraction * triangle;
    }

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
