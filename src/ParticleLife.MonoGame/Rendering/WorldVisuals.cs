using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ParticleLife.Viewer;

namespace ParticleLife.MonoGame.Rendering;

/// <summary>Owns optional screen-space trail and glow targets on the graphics thread.</summary>
/// <remarks>Only completed particle images enter these targets; controls and grid lines do not.
/// Trails advance on a new completed publication, freeze while paused and clear on camera/display
/// changes. Their decay uses presentation seconds, never a modified numerical timestep. Enabled
/// trails own two RGBA32F history targets (32 bytes per full scene render-target pixel together,
/// including the area behind panels, not just the world clip); current/glow images remain RGBA8.
/// History and its effect are released when trails are disabled. Float history and
/// float attenuation avoid the nonzero fixed point of repeatedly rounded RGBA8 decay and preserve
/// long half-lives at high presentation rates. Unsupported history allocation disables only trails
/// and reports the reason through the optional graphics-thread callback.</remarks>
internal sealed class WorldVisuals(GraphicsDevice device, SpriteBatch batch, Action<string>? reportUnavailable = null) : IDisposable
{
    private RenderTarget2D? current, trailRead, trailWrite, glow;
    private BasicEffect? attenuation;
    private bool trailAllocationFailed;
    private AppearanceSettings? previousAppearance;
    private ParticleDisplayTransform previousMapping;
    private Rectangle previousClip;
    private long previousSequence = -1;
    private double sincePublication;
    private bool validTrail;
    private readonly RasterizerState scissor = new() { CullMode = CullMode.None, ScissorTestEnable = true };
    private readonly BlendState additive = new()
    {
        ColorSourceBlend = Blend.One,
        ColorDestinationBlend = Blend.One,
        AlphaSourceBlend = Blend.Zero,
        AlphaDestinationBlend = Blend.One
    };

    /// <summary>Gets the actual retained history payload, excluding driver allocation overhead.</summary>
    internal long RetainedTrailBytes => trailRead is null ? 0 : (long)trailRead.Width * trailRead.Height * 32;

    /// <summary>Gets why trails are unavailable at the current size, without disabling particles or glow.</summary>
    internal string? TrailUnavailableReason { get; private set; }

    /// <summary>Invalidates images belonging to a different experiment/reset epoch.</summary>
    internal void Clear() { validTrail = false; sincePublication = 0; }

    /// <summary>Begins an optional particle-only layer; false leaves the caller's target untouched.</summary>
    internal bool Begin(RenderTarget2D destination, AppearanceSettings appearance)
    {
        VisualSettings settings = appearance.Visuals;
        if (settings.TrailHalfLifeSeconds == 0 && settings.GlowStrength == 0)
        {
            validTrail = false;
            ReleaseTrails();
            return false;
        }
        EnsureTargets(destination.Width, destination.Height);
        if (settings.TrailHalfLifeSeconds > 0) { EnsureTrails(destination.Width, destination.Height); }
        else { ReleaseTrails(); }
        if (trailRead is null && settings.GlowStrength == 0) { return false; }
        device.SetRenderTarget(current);
        device.Clear(Color.Transparent);
        return true;
    }

    /// <summary>Composites the completed layer and restores the supplied scene target.</summary>
    internal void Complete(RenderTarget2D destination, Rectangle clip, long sequence,
        ParticleDisplayTransform mapping, AppearanceSettings appearance, double presentationSeconds)
    {
        VisualSettings settings = appearance.Visuals;
        sincePublication += Math.Max(0, presentationSeconds);
        bool reset = !validTrail || previousAppearance != appearance || previousMapping != mapping
            || previousClip != clip || sequence < previousSequence;
        Texture2D image = current!;
        if (settings.TrailHalfLifeSeconds > 0 && trailRead is not null)
        {
            if (reset || sequence != previousSequence)
            {
                device.SetRenderTarget(trailWrite);
                device.Clear(Color.Transparent);
                if (!reset)
                {
                    // BasicEffect premultiplies its diffuse RGB by Alpha. A white vertex tint therefore
                    // attenuates all four texture channels by the same float uniform, not an 8-bit Color.
                    attenuation!.Texture = trailRead;
                    attenuation.Alpha = (float)Math.Pow(0.5, sincePublication / settings.TrailHalfLifeSeconds);
                    batch.Begin(blendState: BlendState.Opaque, samplerState: SamplerState.PointClamp,
                        rasterizerState: RasterizerState.CullNone, effect: attenuation);
                    batch.Draw(trailRead, destination.Bounds, Color.White);
                    batch.End();
                }
                batch.Begin(blendState: BlendState.AlphaBlend, samplerState: SamplerState.LinearClamp);
                batch.Draw(current!, destination.Bounds, Color.White);
                batch.End();
                (trailRead, trailWrite) = (trailWrite, trailRead);
                sincePublication = 0;
                validTrail = true;
            }
            image = trailRead!;
        }
        else { validTrail = false; sincePublication = 0; }

        if (settings.GlowStrength > 0)
        {
            device.SetRenderTarget(glow);
            device.Clear(Color.Transparent);
            batch.Begin(blendState: BlendState.Opaque, samplerState: SamplerState.LinearClamp);
            batch.Draw(image, glow!.Bounds, Color.White);
            batch.End();
        }
        device.SetRenderTarget(destination);
        batch.Begin(blendState: BlendState.AlphaBlend, samplerState: SamplerState.LinearClamp);
        batch.Draw(image, destination.Bounds, Color.White);
        batch.End();
        if (settings.GlowStrength > 0)
        {
            // A bounded nine-tap enlarged bilinear image supplies a soft halo without a content shader.
            device.ScissorRectangle = Rectangle.Intersect(clip, destination.Bounds);
            batch.Begin(blendState: additive, samplerState: SamplerState.LinearClamp, rasterizerState: scissor);
            int spread = (int)Math.Ceiling(settings.GlowRadiusPixels / 2);
            Color tint = Color.White * (float)(settings.GlowStrength / 9);
            for (int y = -1; y <= 1; y++)
            {
                for (int x = -1; x <= 1; x++)
                {
                    batch.Draw(glow!, new Rectangle(x * spread, y * spread, destination.Width, destination.Height), tint);
                }
            }
            batch.End();
        }
        previousSequence = sequence;
        previousMapping = mapping;
        previousAppearance = appearance;
        previousClip = clip;
    }

    private void EnsureTargets(int width, int height)
    {
        if (current?.Width == width && current.Height == height) { return; }
        RenderTarget2D? nextCurrent = null, nextGlow = null;
        try
        {
            nextCurrent = new RenderTarget2D(device, width, height, false, SurfaceFormat.Color, DepthFormat.None);
            nextGlow = new RenderTarget2D(device, Math.Max(1, width / 4), Math.Max(1, height / 4), false, SurfaceFormat.Color, DepthFormat.None);
        }
        catch
        {
            nextCurrent?.Dispose(); nextGlow?.Dispose();
            throw;
        }
        current?.Dispose(); glow?.Dispose();
        ReleaseTrails();
        current = nextCurrent;
        glow = nextGlow;
        validTrail = false;
    }

    private void EnsureTrails(int width, int height)
    {
        if (trailRead is not null || trailAllocationFailed) { return; }
        RenderTarget2D? first = null, second = null;
        BasicEffect? effect = null;
        try
        {
            first = new RenderTarget2D(device, width, height, false, SurfaceFormat.Vector4, DepthFormat.None);
            second = new RenderTarget2D(device, width, height, false, SurfaceFormat.Vector4, DepthFormat.None);
            if (first.Format != SurfaceFormat.Vector4 || second.Format != SurfaceFormat.Vector4)
            { throw new NotSupportedException("RGBA32F render targets are required to preserve the trail half-life."); }
            effect = new BasicEffect(device)
            {
                TextureEnabled = true,
                VertexColorEnabled = true,
                LightingEnabled = false,
                DiffuseColor = Vector3.One,
                World = Matrix.Identity,
                View = Matrix.Identity,
                Projection = Matrix.CreateOrthographicOffCenter(0, width, height, 0, 0, 1)
            };
            trailRead = first; trailWrite = second; attenuation = effect;
        }
        catch (Exception failure) when (failure is NotSupportedException or InvalidOperationException
            or ArgumentException or OutOfMemoryException)
        {
            first?.Dispose(); second?.Dispose(); effect?.Dispose();
            trailAllocationFailed = true;
            TrailUnavailableReason = "Trails unavailable: " + failure.Message;
            reportUnavailable?.Invoke(TrailUnavailableReason);
        }
    }

    private void ReleaseTrails()
    {
        trailRead?.Dispose(); trailWrite?.Dispose(); attenuation?.Dispose();
        trailRead = trailWrite = null;
        attenuation = null;
        trailAllocationFailed = false;
        TrailUnavailableReason = null;
        validTrail = false;
        sincePublication = 0;
    }

    public void Dispose()
    {
        current?.Dispose(); glow?.Dispose(); ReleaseTrails(); additive.Dispose(); scissor.Dispose();
    }
}
