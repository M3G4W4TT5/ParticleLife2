using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ParticleLife.Viewer;

namespace ParticleLife.MonoGame.Rendering;

/// <summary>Renders one native point per particle from reusable buffers on the existing graphics context.</summary>
/// <remarks>CPU double state is packed only for a new completed publication. Palette storage and camera
/// uniforms are independent, so pausing, panning, resizing and recolouring do not require another state
/// upload. The private VAO consumes an ordinary buffer object; a later resident GPU adapter can provide
/// its completed buffer at this same draw boundary without introducing readback or a second context.</remarks>
internal sealed unsafe class GlPointRenderer : IDisposable
{
    private readonly GraphicsDevice device;
    private readonly OpenGlDrawApi gl;
    private uint vertexBuffer, vertexArray, paletteBuffer, paletteTexture, program;
    private readonly int cameraUniform, diameterUniform, paletteUniform, paletteCountUniform, visualUniform;
    private ParticleDisplayVertex[] staging = [];
    private uint[] palette = [], paletteRgba = [];
    private long packedSequence = -1, uploadedSequence = -1;
    private int packedCount, capacity;
    private double normalizedMagnitude;
    private bool disposed;

    internal string Description => gl.Description;

    internal GlPointRenderer(GraphicsDevice device)
    {
        this.device = device;
        gl = new OpenGlDrawApi();
        try
        {
            using GlDrawStateScope state = new(gl);
            program = gl.BuildProgram(VertexSource, FragmentSource);
            cameraUniform = gl.GetUniformLocation(program, "camera");
            diameterUniform = gl.GetUniformLocation(program, "diameter");
            paletteUniform = gl.GetUniformLocation(program, "palette");
            paletteCountUniform = gl.GetUniformLocation(program, "paletteCount");
            visualUniform = gl.GetUniformLocation(program, "visual");
            if (cameraUniform < 0 || diameterUniform < 0 || paletteUniform < 0 || paletteCountUniform < 0 || visualUniform < 0)
            {
                throw new InvalidOperationException("The native particle shader is missing an expected presentation uniform.");
            }
            gl.GenBuffers(1, out vertexBuffer);
            gl.GenBuffers(1, out paletteBuffer);
            gl.GenVertexArrays(1, out vertexArray);
            gl.GenTextures(1, out paletteTexture);
            gl.BindVertexArray(vertexArray);
            gl.BindBuffer(OpenGlDrawApi.ArrayBuffer, vertexBuffer);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 2, 0x1406, 0, sizeof(ParticleDisplayVertex), 0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribIPointer(1, 1, 0x1405, sizeof(ParticleDisplayVertex), 8);
            gl.EnableVertexAttribArray(2);
            gl.VertexAttribPointer(2, 2, 0x1406, 0, sizeof(ParticleDisplayVertex), 12);
            gl.BindBuffer(OpenGlDrawApi.ArrayBuffer, paletteBuffer);
            uint initialColour = uint.MaxValue;
            gl.BufferData(OpenGlDrawApi.ArrayBuffer, sizeof(uint), &initialColour, 0x88E8);
            gl.ActiveTexture(OpenGlDrawApi.TextureZero);
            gl.BindTexture(OpenGlDrawApi.TextureBuffer, paletteTexture);
            gl.TexBuffer(OpenGlDrawApi.TextureBuffer, 0x8058, paletteBuffer);
            gl.Check("creating native particle draw resources");
        }
        catch
        {
            ReleaseResources();
            gl.Dispose();
            throw;
        }
    }

    /// <summary>Draws a completed observation, or explains why a compatible quad draw is required.</summary>
    /// <remarks>Point diameter is never silently clamped to a driver limit. The expanded native viewport
    /// admits centres just outside the target whose discs overlap it; the original scissor clips pixels.
    /// Without that expansion OpenGL would discard an entire point when its centre crossed the viewport.</remarks>
    internal bool TryDraw(RenderFrameLease frame, WorldTransform transform, Rectangle clip,
        AppearanceSettings appearance, float radius, out ParticleRenderMetrics metrics, out string? fallbackReason)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        metrics = default;
        fallbackReason = null;
        float diameter = radius * 2;
        int margin = checked((int)Math.Ceiling(radius) + 1);
        Viewport viewport = device.Viewport;
        long expandedWidth = (long)viewport.Width + 2L * margin;
        long expandedHeight = (long)viewport.Height + 2L * margin;
        if (diameter < gl.MinimumPointDiameter || diameter > gl.MaximumPointDiameter
            || expandedWidth > gl.MaximumViewportWidth || expandedHeight > gl.MaximumViewportHeight)
        {
            fallbackReason = "The requested display radius exceeds this context's point or expanded-viewport range.";
            return false;
        }
        if (appearance.SpeciesColors.Length > gl.MaximumPaletteEntries)
        {
            fallbackReason = "The palette exceeds this context's texture-buffer range.";
            return false;
        }

        ParticleDisplayTransform mapping = ParticleDisplayTransform.Create(frame, transform);
        long prepared = Stopwatch.GetTimestamp();
        bool stateChanged = packedSequence != frame.Sequence || packedCount != frame.Count;
        if (stateChanged)
        {
            if (staging.Length < frame.Count)
            {
                staging = new ParticleDisplayVertex[frame.Count];
            }
            normalizedMagnitude = ParticleDisplayData.Pack(frame.Positions, frame.Species, mapping.Extent, staging, frame.Velocities);
            packedSequence = frame.Sequence;
            packedCount = frame.Count;
        }
        if (!mapping.SupportsFloatDisplay(normalizedMagnitude))
        {
            fallbackReason = "Double-mapped quads preserve this frame's extreme world/camera display range.";
            return false;
        }
        bool paletteChanged = !appearance.SpeciesColors.SequenceEqual(palette);
        if (paletteChanged)
        {
            int count = appearance.SpeciesColors.Length;
            if (palette.Length != count)
            {
                palette = new uint[count];
                paletteRgba = new uint[count];
            }
            appearance.SpeciesColors.CopyTo(palette);
            for (int index = 0; index < count; index++)
            {
                uint rgb = palette[index];
                paletteRgba[index] = (rgb >> 16) | (rgb & 0xFF00) | ((rgb & 0xFF) << 16) | 0xFF000000;
            }
        }
        double preparation = stateChanged || paletteChanged ? Stopwatch.GetElapsedTime(prepared).TotalMilliseconds : 0;

        long submitted = Stopwatch.GetTimestamp();
        long uploadedBytes = 0;
        double upload;
        using (GlDrawStateScope state = new(gl))
        {
            long uploading = Stopwatch.GetTimestamp();
            if (uploadedSequence != frame.Sequence)
            {
                capacity = Math.Max(capacity, frame.Count);
                gl.BindBuffer(OpenGlDrawApi.ArrayBuffer, vertexBuffer);
                // Orphan storage before a new CPU publication so the driver can retain prior in-flight draws.
                gl.BufferData(OpenGlDrawApi.ArrayBuffer, checked((nint)capacity * sizeof(ParticleDisplayVertex)), null, 0x88E0);
                fixed (ParticleDisplayVertex* pointer = staging)
                {
                    gl.BufferSubData(OpenGlDrawApi.ArrayBuffer, 0, checked((nint)frame.Count * sizeof(ParticleDisplayVertex)), pointer);
                }
                uploadedBytes += (long)frame.Count * sizeof(ParticleDisplayVertex);
                uploadedSequence = frame.Sequence;
            }
            gl.ActiveTexture(OpenGlDrawApi.TextureZero);
            gl.BindTexture(OpenGlDrawApi.TextureBuffer, paletteTexture);
            if (paletteChanged)
            {
                gl.BindBuffer(OpenGlDrawApi.ArrayBuffer, paletteBuffer);
                fixed (uint* pointer = paletteRgba)
                {
                    gl.BufferData(OpenGlDrawApi.ArrayBuffer, checked((nint)paletteRgba.Length * sizeof(uint)), pointer, 0x88E8);
                }
                gl.TexBuffer(OpenGlDrawApi.TextureBuffer, 0x8058, paletteBuffer);
                uploadedBytes += (long)paletteRgba.Length * sizeof(uint);
            }
            upload = uploadedBytes == 0 ? 0 : Stopwatch.GetElapsedTime(uploading).TotalMilliseconds;
            gl.UseProgram(program);
            gl.BindVertexArray(vertexArray);
            bool renderTarget = device.RenderTargetCount != 0;
            float signY = renderTarget ? 1 : -1;
            gl.Uniform4f(cameraUniform,
                (float)(2 * mapping.ScaleX / expandedWidth), (float)(signY * 2 * mapping.ScaleY / expandedHeight),
                (float)(2 * (mapping.OriginX - viewport.X + margin) / expandedWidth - 1),
                (float)(signY * (2 * (mapping.OriginY - viewport.Y + margin) / expandedHeight - 1)));
            gl.Uniform1f(diameterUniform, diameter);
            gl.Uniform1i(paletteUniform, 0);
            gl.Uniform1i(paletteCountUniform, palette.Length);
            gl.Uniform4f(visualUniform, (float)appearance.Visuals.Opacity, (float)appearance.Visuals.Velocity,
                (float)appearance.Visuals.SpeedScale, 0);
            gl.Viewport(state.Viewport.X - margin, state.Viewport.Y - margin, (int)expandedWidth, (int)expandedHeight);
            int scissorY = renderTarget ? clip.Y : device.PresentationParameters.BackBufferHeight - clip.Bottom;
            gl.Scissor(clip.X, scissorY, clip.Width, clip.Height);
            gl.Enable(OpenGlDrawApi.ScissorTest);
            gl.Disable(OpenGlDrawApi.DepthTest);
            gl.Disable(OpenGlDrawApi.CullFace);
            gl.Enable(OpenGlDrawApi.ProgramPointSize);
            // Compatibility and unprofiled legacy contexts need point sprites for gl_PointCoord.
            // Explicit core profiles generate it unconditionally and reject the legacy capability.
            if (gl.RequiresPointSpriteEnable) { gl.Enable(OpenGlDrawApi.PointSprite); }
            gl.Enable(OpenGlDrawApi.Blend);
            gl.BlendEquationSeparate(0x8006, 0x8006);
            gl.BlendFuncSeparate(1, appearance.Visuals.Blend == ParticleBlend.Additive ? 1u : 0x0303u, 1, 0x0303);
            gl.DrawArrays(0, 0, frame.Count);
            gl.Check("submitting bulk particle drawing");
        }
        double submission = Math.Max(0, Stopwatch.GetElapsedTime(submitted).TotalMilliseconds - upload);
        metrics = new(preparation, upload, submission, uploadedBytes, frame.Count, frame.Count,
            "OpenGL bulk points", null);
        return true;
    }

    private void ReleaseResources()
    {
        if (!gl.CanUseOwner) { return; }
        if (vertexArray != 0)
        {
            gl.DeleteVertexArrays(1, ref vertexArray);
        }
        if (vertexBuffer != 0)
        {
            gl.DeleteBuffers(1, ref vertexBuffer);
        }
        if (paletteTexture != 0)
        {
            gl.DeleteTextures(1, ref paletteTexture);
        }
        if (paletteBuffer != 0)
        {
            gl.DeleteBuffers(1, ref paletteBuffer);
        }
        if (program != 0)
        {
            gl.DeleteProgram(program);
        }
    }

    public void Dispose()
    {
        if (!disposed)
        {
            ReleaseResources();
            gl.Dispose();
            disposed = true;
        }
    }

    private const string VertexSource = """
        #version 330
        layout(location = 0) in vec2 position;
        layout(location = 1) in uint species;
        layout(location = 2) in vec2 velocity;
        uniform vec4 camera;
        uniform float diameter;
        flat out uint particleSpecies;
        flat out vec2 particleVelocity;
        void main() {
            gl_Position = vec4(position * camera.xy + camera.zw, 0.0, 1.0);
            gl_PointSize = diameter;
            particleSpecies = species;
            particleVelocity = velocity;
        }
        """;

    private const string FragmentSource = """
        #version 330
        flat in uint particleSpecies;
        flat in vec2 particleVelocity;
        uniform samplerBuffer palette;
        uniform int paletteCount;
        uniform float diameter;
        uniform vec4 visual;
        out vec4 colour;
        void main() {
            float distanceFromCentre = length(gl_PointCoord * 2.0 - 1.0);
            float coverage = clamp((1.0 - distanceFromCentre) * diameter * 0.5, 0.0, 1.0);
            vec3 rgb = texelFetch(palette, int(particleSpecies % uint(paletteCount))).rgb;
            float speed = min(1.0, length(min(vec2(1.0), abs(particleVelocity) / visual.z)));
            if (visual.y > 0.5 && visual.y < 1.5) rgb *= 0.2 + 0.8 * speed;
            if (visual.y > 1.5) {
                float angle = all(equal(particleVelocity, vec2(0.0))) ? 0.0 : atan(particleVelocity.y, particleVelocity.x);
                float hue = visual.y < 2.5 ? (1.0 - speed) * 2.0 / 3.0
                    : (angle + 3.14159265359) / 6.28318530718;
                rgb = clamp(abs(fract(hue + vec3(0.0, 2.0/3.0, 1.0/3.0)) * 6.0 - 3.0) - 1.0, 0.0, 1.0);
            }
            coverage *= visual.x;
            colour = vec4(rgb * coverage, coverage);
        }
        """;
}
