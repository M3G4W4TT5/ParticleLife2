using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;

namespace ParticleLife.Viewer;

/// <summary>Resolves one displayed directed curve through the same global or pair-range rule used by simulation.</summary>
public readonly struct DirectedForceCurve
{
    private readonly ModelParameters parameters;
    private readonly bool pairMode;
    /// <summary>Captures one direction's actual coefficient, core and cutoff.</summary>
    /// <param name="configuration">Validated rules.</param><param name="receiver">Receiver row.</param><param name="source">Source column.</param>
    public DirectedForceCurve(SimulationConfiguration configuration, int receiver, int source)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        parameters = configuration.Parameters; pairMode = configuration.Matrix.Ranges is not null;
        Coefficient = configuration.Matrix[receiver, source];
        Core = configuration.Matrix.CoreRadius(receiver, source, parameters);
        Cutoff = configuration.Matrix.CutoffRadius(receiver, source, parameters);
    }
    /// <summary>Gets the directed dimensionless outer coefficient.</summary>
    public double Coefficient { get; }
    /// <summary>Gets the core radius in reduced length; zero removes the negative core branch.</summary>
    public double Core { get; }
    /// <summary>Gets the outer cutoff in reduced length.</summary>
    public double Cutoff { get; }
    /// <summary>Gets the physical location of the outer triangle's peak.</summary>
    public double Peak => Core + (Cutoff - Core) / 2;
    /// <summary>Evaluates the actual implemented dimensionless curve, including overlap and cutoff zeros.</summary>
    /// <param name="distance">Finite nonnegative distance in reduced length units.</param><returns>The radial coefficient.</returns>
    public double Evaluate(double distance)
    {
        if (!double.IsFinite(distance) || distance < 0) { throw new ArgumentOutOfRangeException(nameof(distance)); }
        if (distance >= Cutoff) { return 0; }
        return pairMode ? ParticleLifeForce.RadialCoefficientAtDistance(distance, Coefficient, Core, Cutoff)
            : ParticleLifeForce.RadialCoefficient(distance / Cutoff, Coefficient, parameters.RepulsionFraction);
    }
}
