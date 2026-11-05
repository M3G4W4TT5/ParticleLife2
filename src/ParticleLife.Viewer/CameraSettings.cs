using ParticleLife.Core.State;

namespace ParticleLife.Viewer;

/// <summary>Immutable target view; camera changes never alter particles or world geometry.</summary>
/// <remarks>Centres are fractions of nominal world width/height, including off-world values.
/// Zoom one fits the nominal rectangle. Smoothing interpolates the displayed view only; saves retain
/// this target rather than frame-dependent interpolation samples.</remarks>
public sealed record CameraSettings
{
    /// <summary>Gets the zoom relative to a fitted nominal world, from 0.02 through 500.</summary>
    public double Zoom { get; init; } = 1;
    /// <summary>Gets the horizontal view centre as a fraction of nominal world width.</summary>
    public double CenterX { get; init; } = 0.5;
    /// <summary>Gets the vertical view centre as a fraction of nominal world height.</summary>
    public double CenterY { get; init; } = 0.5;
    /// <summary>Gets whether the native display eases towards the target view.</summary>
    public bool Smooth { get; init; }
    /// <summary>Checks finite supported view values before presentation or persistence.</summary>
    public void Validate()
    {
        if (!double.IsFinite(Zoom) || Zoom < 0.02 || Zoom > 500 || !double.IsFinite(CenterX)
            || !double.IsFinite(CenterY) || Math.Abs(CenterX) > 1e6 || Math.Abs(CenterY) > 1e6)
        {
            throw new ArgumentOutOfRangeException(nameof(Zoom), "Camera zoom must be 0.02..500 and normalized centres finite within +/-1000000.");
        }
    }
    /// <summary>Changes zoom while retaining the world point beneath one screen position.</summary>
    /// <param name="transform">Current mapping using this target camera.</param>
    /// <param name="screenPoint">Anchor position in pixels.</param>
    /// <param name="factor">Positive finite zoom multiplier.</param>
    /// <param name="worldWidth">Nominal width in reduced units.</param>
    /// <param name="worldHeight">Nominal height in reduced units.</param>
    /// <returns>A validated anchored target.</returns>
    public CameraSettings ZoomAt(WorldTransform transform, Vector2d screenPoint, double factor, double worldWidth, double worldHeight)
    {
        if (!double.IsFinite(factor) || factor <= 0) { throw new ArgumentOutOfRangeException(nameof(factor)); }
        Vector2d anchor = transform.ToWorld(screenPoint);
        double zoom = Math.Clamp(Zoom * factor, 0.02, 500);
        double ratio = Zoom / zoom;
        CameraSettings result = this with
        {
            Zoom = zoom,
            CenterX = anchor.X / worldWidth + (CenterX - anchor.X / worldWidth) * ratio,
            CenterY = anchor.Y / worldHeight + (CenterY - anchor.Y / worldHeight) * ratio
        };
        result.Validate();
        return result;
    }
}

/// <summary>Selects presentation radius units independently of every physical interaction radius.</summary>
public enum ParticleSizeMode
{
    /// <summary>A fixed screen radius while zooming.</summary>
    ScreenPixels,
    /// <summary>A reduced-world-unit radius transformed with the camera.</summary>
    WorldRelative
}
