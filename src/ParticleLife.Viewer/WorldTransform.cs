using ParticleLife.Core.State;

namespace ParticleLife.Viewer;

/// <summary>Centers a rectangular world in a viewport using one isotropic pixel-per-world-unit scale.</summary>
/// <remarks>Coordinates increase rightwards and downwards. This is presentation-only: resizing changes
/// no model parameters or particle values. Off-world positions remain valid and may map offscreen.</remarks>
public readonly struct WorldTransform
{
    /// <summary>Fits the nominal world inside a display viewport without stretching either axis.</summary>
    /// <param name="worldWidth">Positive finite nominal width in reduced length units.</param>
    /// <param name="worldHeight">Positive finite nominal height in reduced length units.</param>
    /// <param name="viewportX">Finite horizontal viewport origin in pixels.</param>
    /// <param name="viewportY">Finite vertical viewport origin in pixels.</param>
    /// <param name="viewportWidth">Positive finite viewport width in pixels.</param>
    /// <param name="viewportHeight">Positive finite viewport height in pixels.</param>
    /// <exception cref="ArgumentOutOfRangeException">Dimensions or origins are invalid, or the transform is unrepresentable.</exception>
    public WorldTransform(double worldWidth, double worldHeight, double viewportX, double viewportY,
        double viewportWidth, double viewportHeight)
    {
        RequirePositiveFinite(worldWidth, nameof(worldWidth));
        RequirePositiveFinite(worldHeight, nameof(worldHeight));
        RequirePositiveFinite(viewportWidth, nameof(viewportWidth));
        RequirePositiveFinite(viewportHeight, nameof(viewportHeight));
        if (!double.IsFinite(viewportX) || !double.IsFinite(viewportY))
        {
            throw new ArgumentOutOfRangeException(nameof(viewportX), "Viewport origins must be finite.");
        }

        Scale = Math.Min(viewportWidth / worldWidth, viewportHeight / worldHeight);
        RequirePositiveFinite(Scale, nameof(worldWidth));
        Width = worldWidth * Scale;
        Height = worldHeight * Scale;
        OffsetX = viewportX + (viewportWidth - Width) / 2;
        OffsetY = viewportY + (viewportHeight - Height) / 2;
        if (!double.IsFinite(OffsetX) || !double.IsFinite(OffsetY))
        {
            throw new ArgumentOutOfRangeException(nameof(viewportX), "Mapped viewport origin must be finite.");
        }
    }

    /// <summary>Gets the common horizontal and vertical scale in pixels per reduced length unit.</summary>
    public double Scale { get; }

    /// <summary>Gets the horizontal pixel coordinate of the nominal world's origin.</summary>
    public double OffsetX { get; }

    /// <summary>Gets the vertical pixel coordinate of the nominal world's origin.</summary>
    public double OffsetY { get; }

    /// <summary>Gets the nominal world's displayed width in pixels.</summary>
    public double Width { get; }

    /// <summary>Gets the nominal world's displayed height in pixels.</summary>
    public double Height { get; }

    /// <summary>Maps a world position without clamping or modifying simulation membership.</summary>
    /// <param name="worldPosition">Position in reduced length units, including off-world coordinates.</param>
    /// <returns>Pixel coordinates. Extreme offscreen values may overflow; the renderer can skip drawing
    /// such a point while retaining the original finite particle in the simulation.</returns>
    public Vector2d ToScreen(Vector2d worldPosition) =>
        new(OffsetX + worldPosition.X * Scale, OffsetY + worldPosition.Y * Scale);

    private static void RequirePositiveFinite(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, "Transform dimensions and scale must be finite and positive.");
        }
    }
}
