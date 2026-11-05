using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ParticleLife.Viewer;

namespace ParticleLife.MonoGame.Rendering;

/// <summary>Owns bulk particle rendering with optional native GL points and a compatible MonoGame fallback.</summary>
/// <remarks>Construct, draw and dispose on the graphics owner thread, between completed SpriteBatch
/// batches. Drawing borrows the completed CPU lease only for the call. CPU simulation and headless
/// startup never construct this type. Native draw capability is independent of GPU compute capability.</remarks>
internal sealed class ParticleRenderer : IDisposable
{
    private readonly GraphicsDevice device;
    private readonly Texture2D particleTexture;
    private GlPointRenderer? points;
    private QuadParticleRenderer? quads;
    private string? unavailableReason;
    private bool disposed;

    internal ParticleRenderer(GraphicsDevice device, Texture2D particleTexture)
    {
        this.device = device;
        this.particleTexture = particleTexture;
        try
        {
            points = new GlPointRenderer(device);
        }
        catch (Exception exception) when (IsNativeAvailabilityFailure(exception))
        {
            unavailableReason = exception.Message;
        }
    }

    internal string Description => points?.Description ?? "MonoGame bulk quads";
    internal ParticleRenderMetrics LastMetrics { get; private set; }

    /// <summary>Renders the supplied completed state using its own world configuration and current appearance.</summary>
    /// <remarks>Native particle uploads occur only for a newer publication sequence. Camera/radius changes
    /// use uniforms; palette changes use a separate small buffer. The BasicEffect compatibility path
    /// rebuilds its expanded quads for those display edits, without changing numerical state.</remarks>
    internal void Draw(RenderFrameLease frame, WorldTransform transform, Rectangle clip, AppearanceSettings appearance)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        clip = Rectangle.Intersect(clip, device.Viewport.Bounds);
        if (frame.Count == 0 || clip.Width <= 0 || clip.Height <= 0)
        {
            LastMetrics = new(0, 0, 0, 0, frame.Count, 0, Description, unavailableReason);
            return;
        }
        double requestedRadius = appearance.ParticleSizeMode == ParticleSizeMode.WorldRelative
            ? transform.WorldLengthToPixels(appearance.WorldParticleRadius) : appearance.ParticleRadius;
        float radius = (float)Math.Clamp(requestedRadius, 0.25, Math.Max(clip.Width, clip.Height));

        string? reason = unavailableReason;
        if (points is not null)
        {
            try
            {
                if (points.TryDraw(frame, transform, clip, appearance, radius, out ParticleRenderMetrics metrics, out reason))
                {
                    LastMetrics = metrics;
                    return;
                }
            }
            catch (Exception exception) when (IsNativeAvailabilityFailure(exception))
            {
                unavailableReason = reason = "Native particle rendering became unavailable: " + exception.Message;
                points.Dispose();
                points = null;
            }
        }

        quads ??= new QuadParticleRenderer(device, particleTexture);
        LastMetrics = quads.Draw(frame, transform, clip, appearance, radius, reason);
    }

    private static bool IsNativeAvailabilityFailure(Exception exception) => exception is
        NotSupportedException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException
        or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException;

    public void Dispose()
    {
        if (!disposed)
        {
            points?.Dispose();
            quads?.Dispose();
            disposed = true;
        }
    }
}
