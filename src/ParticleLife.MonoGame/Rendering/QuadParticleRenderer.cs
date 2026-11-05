using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ParticleLife.Core.State;
using ParticleLife.Viewer;

namespace ParticleLife.MonoGame.Rendering;

/// <summary>Draws reusable indexed quads through ordinary MonoGame APIs when native points are unsuitable.</summary>
/// <remarks>Uses 16-bit indices in batches of 8,192 quads, including on the Reach profile. Geometry is
/// uploaded only after a new observation or relevant display edit. Double mapping/culling happens
/// before float conversion, preserving visible points in extreme CPU worlds. The shared disc texture
/// is borrowed; this owner disposes only its own effect, buffers and rasterizer.</remarks>
internal sealed class QuadParticleRenderer : IDisposable
{
    private const int QuadsPerDraw = 8192;
    private readonly GraphicsDevice device;
    private readonly BasicEffect effect;
    private readonly IndexBuffer indices;
    private readonly RasterizerState rasterizer;
    private DynamicVertexBuffer? vertices;
    private VertexPositionColorTexture[] staging = [];
    private uint[] palette = [];
    private long sequence = -1;
    private ParticleDisplayTransform previousTransform;
    private Rectangle previousClip;
    private float previousRadius;
    private int visibleCount;
    private bool disposed;

    internal QuadParticleRenderer(GraphicsDevice device, Texture2D particleTexture)
    {
        this.device = device;
        effect = new BasicEffect(device)
        {
            TextureEnabled = true,
            VertexColorEnabled = true,
            Texture = particleTexture,
            World = Matrix.Identity,
            View = Matrix.Identity
        };
        try
        {
            rasterizer = new RasterizerState { CullMode = CullMode.None, ScissorTestEnable = true };
            short[] indexData = new short[QuadsPerDraw * 6];
            for (int index = 0; index < QuadsPerDraw; index++)
            {
                int vertex = index * 4;
                int offset = index * 6;
                indexData[offset] = (short)vertex;
                indexData[offset + 1] = (short)(vertex + 1);
                indexData[offset + 2] = (short)(vertex + 2);
                indexData[offset + 3] = (short)vertex;
                indexData[offset + 4] = (short)(vertex + 2);
                indexData[offset + 5] = (short)(vertex + 3);
            }
            indices = new IndexBuffer(device, IndexElementSize.SixteenBits, indexData.Length, BufferUsage.WriteOnly);
            indices.SetData(indexData);
        }
        catch
        {
            effect.Dispose();
            rasterizer?.Dispose();
            indices?.Dispose();
            throw;
        }
    }

    internal ParticleRenderMetrics Draw(RenderFrameLease frame, WorldTransform transform, Rectangle clip,
        AppearanceSettings appearance, float radius, string? fallbackReason)
    {
        double preparation = 0, upload = 0;
        long uploadedBytes = 0;
        ParticleDisplayTransform mapping = ParticleDisplayTransform.Create(frame, transform);
        bool paletteChanged = !appearance.SpeciesColors.SequenceEqual(palette);
        if (sequence != frame.Sequence || mapping != previousTransform || clip != previousClip
            || radius != previousRadius || paletteChanged)
        {
            long started = Stopwatch.GetTimestamp();
            int required = checked(frame.Count * 4);
            if (staging.Length < required)
            {
                staging = new VertexPositionColorTexture[required];
            }
            visibleCount = 0;
            ReadOnlySpan<Vector2d> positions = frame.Positions;
            ReadOnlySpan<int> species = frame.Species;
            for (int index = 0; index < frame.Count; index++)
            {
                Vector2d mapped = transform.ToScreen(positions[index]);
                if (!ParticleDisplayData.IsVisible(mapped, clip, radius))
                {
                    continue;
                }
                uint rgb = appearance.ColorForSpecies(species[index]);
                Color colour = new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
                float x = (float)mapped.X;
                float y = (float)mapped.Y;
                int vertex = visibleCount++ * 4;
                staging[vertex] = new(new Vector3(x - radius, y - radius, 0), colour, new Vector2(0, 0));
                staging[vertex + 1] = new(new Vector3(x + radius, y - radius, 0), colour, new Vector2(1, 0));
                staging[vertex + 2] = new(new Vector3(x + radius, y + radius, 0), colour, new Vector2(1, 1));
                staging[vertex + 3] = new(new Vector3(x - radius, y + radius, 0), colour, new Vector2(0, 1));
            }
            preparation = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            started = Stopwatch.GetTimestamp();
            int vertexCount = visibleCount * 4;
            if (vertexCount > 0)
            {
                if (vertices is null || vertices.VertexCount < vertexCount)
                {
                    DynamicVertexBuffer replacement = new(device, VertexPositionColorTexture.VertexDeclaration,
                        vertexCount, BufferUsage.WriteOnly);
                    vertices?.Dispose();
                    vertices = replacement;
                }
                vertices.SetData(staging, 0, vertexCount, SetDataOptions.Discard);
                uploadedBytes = (long)vertexCount * VertexPositionColorTexture.VertexDeclaration.VertexStride;
            }
            upload = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (paletteChanged)
            {
                if (palette.Length != appearance.SpeciesColors.Length)
                {
                    palette = new uint[appearance.SpeciesColors.Length];
                }
                appearance.SpeciesColors.CopyTo(palette);
            }
            sequence = frame.Sequence;
            previousTransform = mapping;
            previousClip = clip;
            previousRadius = radius;
        }

        long submitted = Stopwatch.GetTimestamp();
        if (visibleCount > 0)
        {
            Viewport viewport = device.Viewport;
            effect.Projection = Matrix.CreateOrthographicOffCenter(viewport.X, viewport.X + viewport.Width,
                viewport.Y + viewport.Height, viewport.Y, 0, 1);
            device.BlendState = BlendState.AlphaBlend;
            device.DepthStencilState = DepthStencilState.None;
            device.RasterizerState = rasterizer;
            device.ScissorRectangle = clip;
            device.SamplerStates[0] = SamplerState.LinearClamp;
            device.SetVertexBuffer(vertices);
            device.Indices = indices;
            foreach (EffectPass pass in effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                for (int first = 0; first < visibleCount; first += QuadsPerDraw)
                {
                    int count = Math.Min(QuadsPerDraw, visibleCount - first);
                    device.DrawIndexedPrimitives(PrimitiveType.TriangleList, first * 4, 0, count * 2);
                }
            }
        }
        double submission = Stopwatch.GetElapsedTime(submitted).TotalMilliseconds;
        return new(preparation, upload, submission, uploadedBytes, frame.Count, visibleCount,
            "MonoGame bulk quads", fallbackReason);
    }

    public void Dispose()
    {
        if (!disposed)
        {
            vertices?.Dispose();
            indices.Dispose();
            effect.Dispose();
            rasterizer.Dispose();
            disposed = true;
        }
    }
}
