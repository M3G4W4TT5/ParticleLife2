using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ParticleLife.MonoGame.Gpu;
using ParticleLife.Viewer;

namespace ParticleLife.MonoGame.Rendering;

/// <summary>Draws the completed resident state directly as instanced disc quads without reading or uploading particles.</summary>
/// <remarks>The simulation owns the borrowed 32-byte records. This adapter owns only its VAO, program
/// and palette. Instance attributes read position at byte zero, velocity at byte eight and species at byte 24. Four generated
/// corners avoid hardware point-size limits and centre-based point clipping. Double camera arithmetic
/// preserves the supplied 2D affine transform even for very small float32 worlds or large camera offsets;
/// it changes presentation only, never float32 simulation storage. All touched GL state is restored.</remarks>
internal sealed unsafe class GlResidentRenderer : IDisposable
{
    private readonly GraphicsDevice device;
    private readonly OpenGlDrawApi gl;
    private readonly DrawInstancedCall drawInstanced;
    private readonly AttributeDivisorCall attributeDivisor;
    private readonly UniformDoubleVectorCall uniform4d;
    private uint vertexArray, program, paletteBuffer, paletteTexture;
    private readonly int cameraUniform, cornerUniform, radiusUniform, paletteUniform, paletteCountUniform, visualUniform;
    private uint[] palette = [], rgba = [];
    private bool disposed;

    /// <summary>Gets the retained palette payload; borrowed simulation buffers are accounted by their owner.</summary>
    internal long RetainedDeviceBufferBytes => (long)palette.Length * sizeof(uint);

    internal GlResidentRenderer(GraphicsDevice device)
    {
        this.device = device;
        gl = new();
        try
        {
            drawInstanced = gl.Load<DrawInstancedCall>("glDrawArraysInstanced");
            attributeDivisor = gl.Load<AttributeDivisorCall>("glVertexAttribDivisor");
            uniform4d = gl.Load<UniformDoubleVectorCall>("glUniform4d");
            using GlDrawStateScope scope = new(gl);
            program = gl.BuildProgram(VertexSource, FragmentSource);
            cameraUniform = gl.GetUniformLocation(program, "camera");
            cornerUniform = gl.GetUniformLocation(program, "cornerScale");
            radiusUniform = gl.GetUniformLocation(program, "radius");
            paletteUniform = gl.GetUniformLocation(program, "palette");
            paletteCountUniform = gl.GetUniformLocation(program, "paletteCount");
            visualUniform = gl.GetUniformLocation(program, "visual");
            gl.GenVertexArrays(1, out vertexArray);
            gl.GenBuffers(1, out paletteBuffer);
            gl.GenTextures(1, out paletteTexture);
            gl.Check("creating resident draw adapter");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal ParticleRenderMetrics Draw(GlGpuSimulation simulation, WorldTransform transform, Rectangle clip, AppearanceSettings appearance)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        gl.EnsureOwner();
        clip = Rectangle.Intersect(clip, device.Viewport.Bounds);
        int count = simulation.Boundary.Count;
        if (count == 0 || clip.Width <= 0 || clip.Height <= 0)
        {
            return new(0, 0, 0, 0, count, 0, "OpenGL resident instanced discs", null);
        }
        if (appearance.SpeciesColors.Length > gl.MaximumPaletteEntries)
        {
            throw new NotSupportedException("The resident palette exceeds the native texture-buffer capacity.");
        }
        long started = Stopwatch.GetTimestamp();
        bool changed = !appearance.SpeciesColors.SequenceEqual(palette);
        if (changed)
        {
            if (palette.Length != appearance.SpeciesColors.Length)
            {
                palette = new uint[appearance.SpeciesColors.Length];
                rgba = new uint[palette.Length];
            }
            appearance.SpeciesColors.CopyTo(palette);
            for (int index = 0; index < palette.Length; index++)
            {
                uint rgb = palette[index];
                rgba[index] = (rgb >> 16) | (rgb & 0xFF00) | ((rgb & 0xFF) << 16) | 0xFF000000;
            }
        }
        double preparation = changed ? Stopwatch.GetElapsedTime(started).TotalMilliseconds : 0;
        double requestedRadius = appearance.ParticleSizeMode == ParticleSizeMode.WorldRelative
            ? transform.WorldLengthToPixels(appearance.WorldParticleRadius) : appearance.ParticleRadius;
        float radius = (float)Math.Clamp(requestedRadius, 0.25, Math.Max(clip.Width, clip.Height));
        long submission = Stopwatch.GetTimestamp();
        double upload = 0;
        using (GlDrawStateScope scope = new(gl))
        {
            gl.ActiveTexture(OpenGlDrawApi.TextureZero);
            gl.BindTexture(OpenGlDrawApi.TextureBuffer, paletteTexture);
            if (changed)
            {
                long uploadStart = Stopwatch.GetTimestamp();
                gl.BindBuffer(OpenGlDrawApi.ArrayBuffer, paletteBuffer);
                fixed (uint* pointer = rgba)
                {
                    gl.BufferData(OpenGlDrawApi.ArrayBuffer, checked((nint)rgba.Length * sizeof(uint)), pointer, 0x88E8);
                }
                gl.TexBuffer(OpenGlDrawApi.TextureBuffer, 0x8058, paletteBuffer);
                upload = Stopwatch.GetElapsedTime(uploadStart).TotalMilliseconds;
            }
            gl.BindVertexArray(vertexArray);
            gl.BindBuffer(OpenGlDrawApi.ArrayBuffer, simulation.CompletedBuffer);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 2, 0x1406, 0, sizeof(GpuParticle), 0);
            attributeDivisor(0, 1);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribIPointer(1, 1, 0x1405, sizeof(GpuParticle), 24);
            attributeDivisor(1, 1);
            gl.EnableVertexAttribArray(2);
            gl.VertexAttribPointer(2, 2, 0x1406, 0, sizeof(GpuParticle), 8);
            attributeDivisor(2, 1);
            gl.UseProgram(program);
            Viewport viewport = device.Viewport;
            bool renderTarget = device.RenderTargetCount != 0;
            double signY = renderTarget ? 1 : -1;
            uniform4d(cameraUniform, 2 * transform.NormalizedScale / transform.WorldExtent / viewport.Width,
                signY * 2 * transform.NormalizedScale / transform.WorldExtent / viewport.Height,
                2 * (transform.OffsetX - viewport.X) / viewport.Width - 1,
                signY * (2 * (transform.OffsetY - viewport.Y) / viewport.Height - 1));
            gl.Uniform4f(cornerUniform, 2 * radius / viewport.Width, (float)signY * 2 * radius / viewport.Height, 0, 0);
            gl.Uniform1f(radiusUniform, radius);
            gl.Uniform1i(paletteUniform, 0);
            gl.Uniform1i(paletteCountUniform, palette.Length);
            gl.Uniform4f(visualUniform, (float)appearance.Visuals.Opacity, (float)appearance.Visuals.Velocity,
                (float)appearance.Visuals.SpeedScale, 0);
            gl.Scissor(clip.X, renderTarget ? clip.Y : device.PresentationParameters.BackBufferHeight - clip.Bottom, clip.Width, clip.Height);
            gl.Enable(OpenGlDrawApi.ScissorTest);
            gl.Disable(OpenGlDrawApi.DepthTest);
            gl.Disable(OpenGlDrawApi.CullFace);
            gl.Enable(OpenGlDrawApi.Blend);
            gl.BlendEquationSeparate(0x8006, 0x8006);
            gl.BlendFuncSeparate(1, appearance.Visuals.Blend == ParticleBlend.Additive ? 1u : 0x0303u, 1, 0x0303);
            drawInstanced(0x0005, 0, 4, count);
            gl.Check("drawing completed resident particles");
        }
        return new(preparation, upload, Math.Max(0, Stopwatch.GetElapsedTime(submission).TotalMilliseconds - upload),
            changed ? (long)palette.Length * sizeof(uint) : 0, count, count, "OpenGL resident instanced discs", null);
    }

    public void Dispose()
    {
        if (disposed) { return; }
        if (gl.CanUseOwner)
        {
            if (vertexArray != 0) { gl.DeleteVertexArrays(1, ref vertexArray); }
            if (paletteBuffer != 0) { gl.DeleteBuffers(1, ref paletteBuffer); }
            if (paletteTexture != 0) { gl.DeleteTextures(1, ref paletteTexture); }
            if (program != 0) { gl.DeleteProgram(program); }
        }
        gl.Dispose();
        disposed = true;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void DrawInstancedCall(uint mode, int first, int vertices, int instances);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void AttributeDivisorCall(uint index, uint divisor);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void UniformDoubleVectorCall(int location, double x, double y, double z, double w);

    private const string VertexSource = """
        #version 430
        layout(location=0) in vec2 position;
        layout(location=1) in uint species;
        layout(location=2) in vec2 velocity;
        uniform dvec4 camera;
        uniform vec4 cornerScale;
        noperspective out vec2 corner;
        flat out uint particleSpecies;
        flat out vec2 particleVelocity;
        void main() {
            corner=vec2((gl_VertexID&1)==0?-1.0:1.0,(gl_VertexID&2)==0?-1.0:1.0);
            dvec2 centre=dvec2(position)*camera.xy+camera.zw;
            gl_Position=vec4(vec2(centre)+corner*cornerScale.xy,0.0,1.0);
            particleSpecies=species;
            particleVelocity=velocity;
        }
        """;
    private const string FragmentSource = """
        #version 430
        noperspective in vec2 corner;
        flat in uint particleSpecies;
        flat in vec2 particleVelocity;
        uniform samplerBuffer palette;
        uniform int paletteCount;
        uniform float radius;
        uniform vec4 visual;
        out vec4 colour;
        void main() {
            float coverage=clamp((1.0-length(corner))*radius+0.5,0.0,1.0);
            vec3 rgb=texelFetch(palette,int(particleSpecies%uint(paletteCount))).rgb;
            float speed=min(1.0,length(min(vec2(1.0),abs(particleVelocity)/visual.z)));
            if(visual.y>0.5 && visual.y<1.5) rgb*=0.2+0.8*speed;
            if(visual.y>1.5) {
                float angle=all(equal(particleVelocity,vec2(0.0)))?0.0:atan(particleVelocity.y,particleVelocity.x);
                float hue=visual.y<2.5?(1.0-speed)*2.0/3.0:(angle+3.14159265359)/6.28318530718;
                rgb=clamp(abs(fract(hue+vec3(0.0,2.0/3.0,1.0/3.0))*6.0-3.0)-1.0,0.0,1.0);
            }
            coverage*=visual.x;
            colour=vec4(rgb*coverage,coverage);
        }
        """;
}
