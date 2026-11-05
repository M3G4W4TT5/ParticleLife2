using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ParticleLife.Viewer;

namespace ParticleLife.MonoGame.Rendering;

/// <summary>Owns optional screen-space trail and glow targets on the graphics thread.</summary>
/// <remarks>Only completed particle images enter these targets; controls and grid lines do not.
/// Trails advance on a new completed publication, freeze while paused and clear on camera/display
/// changes. Their decay uses presentation seconds, never a modified numerical timestep.</remarks>
internal sealed class WorldVisuals(GraphicsDevice device, SpriteBatch batch) : IDisposable
{
    private RenderTarget2D? current, trailRead, trailWrite, glow;
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

    /// <summary>Invalidates images belonging to a different experiment/reset epoch.</summary>
    internal void Clear() { validTrail = false; sincePublication = 0; }

    /// <summary>Begins an optional particle-only layer; false leaves the caller's target untouched.</summary>
    internal bool Begin(RenderTarget2D destination, AppearanceSettings appearance)
    {
        VisualSettings settings = appearance.Visuals;
        if (settings.TrailHalfLifeSeconds == 0 && settings.GlowStrength == 0)
        {
            validTrail = false;
            return false;
        }
        EnsureTargets(destination.Width, destination.Height);
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
        if (settings.TrailHalfLifeSeconds > 0)
        {
            if (reset || sequence != previousSequence)
            {
                device.SetRenderTarget(trailWrite);
                device.Clear(Color.Transparent);
                batch.Begin(blendState: BlendState.AlphaBlend, samplerState: SamplerState.LinearClamp);
                if (!reset)
                {
                    float retention = (float)Math.Pow(0.5, sincePublication / settings.TrailHalfLifeSeconds);
                    batch.Draw(trailRead!, destination.Bounds, Color.White * retention);
                }
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
        current?.Dispose(); trailRead?.Dispose(); trailWrite?.Dispose(); glow?.Dispose();
        current = new RenderTarget2D(device, width, height, false, SurfaceFormat.Color, DepthFormat.None);
        trailRead = new RenderTarget2D(device, width, height, false, SurfaceFormat.Color, DepthFormat.None);
        trailWrite = new RenderTarget2D(device, width, height, false, SurfaceFormat.Color, DepthFormat.None);
        glow = new RenderTarget2D(device, Math.Max(1, width / 4), Math.Max(1, height / 4), false, SurfaceFormat.Color, DepthFormat.None);
        validTrail = false;
    }

    public void Dispose()
    {
        current?.Dispose(); trailRead?.Dispose(); trailWrite?.Dispose(); glow?.Dispose(); additive.Dispose(); scissor.Dispose();
    }
}
