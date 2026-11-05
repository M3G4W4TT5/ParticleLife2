using ParticleLife.Core.State;

namespace ParticleLife.Viewer;

/// <summary>Selects how translucent particle fragments combine; neither choice changes forces.</summary>
public enum ParticleBlend
{
    /// <summary>Premultiplied source-over transparency.</summary>
    Normal,
    /// <summary>Additive light accumulation, useful for dense luminous patterns.</summary>
    Additive
}

/// <summary>Selects presentation colour derived from completed velocities.</summary>
public enum VelocityShading
{
    /// <summary>Use the species palette unchanged.</summary>
    Species,
    /// <summary>Scale species brightness from 20% at rest to full brightness at the reference speed.</summary>
    SpeedBrightness,
    /// <summary>Map zero through reference speed from blue through green to red.</summary>
    SpeedColour,
    /// <summary>Map velocity direction continuously around a hue wheel.</summary>
    DirectionColour
}

/// <summary>Selects an optional sampled spatial observation overlay.</summary>
public enum HeatmapKind
{
    /// <summary>No spatial sampling or overlay.</summary>
    Off,
    /// <summary>Population density on a fixed 32 by 32 world grid.</summary>
    Population,
    /// <summary>Occupancy on cutoff-sized observation bins, capped at 128 cells per axis.</summary>
    SpatialBins
}

/// <summary>Immutable optional visual quality and observation settings, independent of numerical stepping.</summary>
public sealed record VisualSettings
{
    /// <summary>Gets particle opacity from zero through one.</summary>
    public double Opacity { get; init; } = 1;
    /// <summary>Gets particle compositing mode.</summary>
    public ParticleBlend Blend { get; init; }
    /// <summary>Gets velocity-derived shading; species colours are the default.</summary>
    public VelocityShading Velocity { get; init; }
    /// <summary>Gets positive reference speed in L/T for velocity shading.</summary>
    public double SpeedScale { get; init; } = 0.1;
    /// <summary>Gets trail decay half-life in presentation seconds, from zero (off) through eight.</summary>
    public double TrailHalfLifeSeconds { get; init; }
    /// <summary>Gets glow contribution from zero (off) through four.</summary>
    public double GlowStrength { get; init; }
    /// <summary>Gets glow spread in display pixels, from one through 64.</summary>
    public double GlowRadiusPixels { get; init; } = 10;
    /// <summary>Gets the optional spatial occupancy overlay.</summary>
    public HeatmapKind Heatmap { get; init; }
    /// <summary>Gets optional observation cadence in presentation seconds, from 0.1 through 10.</summary>
    public double ObservationSeconds { get; init; } = 0.5;
    /// <summary>Gets whether sampled speed and population statistics are displayed.</summary>
    public bool ShowStatistics { get; init; }

    /// <summary>Rejects unsupported or non-finite display settings before application or persistence.</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Blend) || !Enum.IsDefined(Velocity) || !Enum.IsDefined(Heatmap))
        { throw new ArgumentOutOfRangeException(nameof(Blend), "Unsupported visual mode."); }
        if (!double.IsFinite(Opacity) || Opacity < 0 || Opacity > 1
            || !double.IsFinite(SpeedScale) || SpeedScale < 1e-20 || SpeedScale > 1e20
            || !double.IsFinite(TrailHalfLifeSeconds) || TrailHalfLifeSeconds < 0 || TrailHalfLifeSeconds > 8
            || !double.IsFinite(GlowStrength) || GlowStrength < 0 || GlowStrength > 4
            || !double.IsFinite(GlowRadiusPixels) || GlowRadiusPixels < 1 || GlowRadiusPixels > 64
            || !double.IsFinite(ObservationSeconds) || ObservationSeconds < 0.1 || ObservationSeconds > 10)
        { throw new ArgumentOutOfRangeException(nameof(Opacity), "Visual values are outside their supported ranges."); }
    }
}

/// <summary>Defines the graphics-free colour convention used by compatible particle renderers.</summary>
public static class ParticleColour
{
    /// <summary>Returns straight RGB components in [0,1] for one completed velocity.</summary>
    /// <param name="rgb">Species RGB colour.</param><param name="velocity">Completed velocity in L/T.</param>
    /// <param name="settings">Validated visual settings.</param>
    /// <returns>Red, green and blue components before opacity and coverage premultiplication.</returns>
    public static (float Red, float Green, float Blue) Evaluate(uint rgb, Vector2d velocity, VisualSettings settings)
    {
        float r = ((rgb >> 16) & 255) / 255f, g = ((rgb >> 8) & 255) / 255f, b = (rgb & 255) / 255f;
        double x = Math.Min(1, Math.Abs(velocity.X) / settings.SpeedScale);
        double y = Math.Min(1, Math.Abs(velocity.Y) / settings.SpeedScale);
        double speed = Math.Min(1, Math.Sqrt(x * x + y * y));
        if (settings.Velocity == VelocityShading.SpeedBrightness)
        {
            float brightness = (float)(0.2 + 0.8 * speed);
            return (r * brightness, g * brightness, b * brightness);
        }
        if (settings.Velocity == VelocityShading.Species) { return (r, g, b); }
        double hue = settings.Velocity == VelocityShading.SpeedColour ? (1 - speed) * 2 / 3
            : (Math.Atan2(velocity.Y, velocity.X) + Math.PI) / (2 * Math.PI);
        float Channel(double offset) => (float)Math.Clamp(Math.Abs((hue + offset - Math.Floor(hue + offset)) * 6 - 3) - 1, 0, 1);
        return (Channel(0), Channel(2d / 3), Channel(1d / 3));
    }
}
