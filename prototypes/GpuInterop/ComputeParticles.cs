using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ParticleLife.GpuInterop;

/// <summary>Owns two resident particle buffers and renders completed compute output without CPU transfer.</summary>
/// <remarks>One MonoGame graphics thread owns stepping, rendering, capture and disposal. A submitted
/// compute stage reads only the completed input and writes a disjoint output. Publication swaps handles
/// after the visibility barrier. Rendering borrows that handle only during the immediately submitted draw.
/// The next reuse is ordered with a shader-storage barrier. No explicit CPU fence wait or particle readback occurs during normal
/// stepping/drawing. Explicit capture waits for a fence before copying owned bytes to the caller.</remarks>
internal sealed unsafe class ComputeParticles : IDisposable
{
    private readonly NativeGl gl;
    private readonly int count;
    private uint completed, scratch, vertexArray, computeProgram, drawProgram;
    private bool disposed;

    internal uint CompletedBuffer => completed;
    internal uint LastDrawnBuffer { get; private set; }
    internal int ReadbackCount { get; private set; }
    internal double LastCaptureMilliseconds { get; private set; }

    internal ComputeParticles(NativeGl gl, Particle[] initial)
    {
        if (initial.Length != Particle.Count || Marshal.SizeOf<Particle>() != 32)
        {
            throw new ArgumentException("The fixed interoperability fixture requires 4,096 32-byte particles.", nameof(initial));
        }
        this.gl = gl;
        count = initial.Length;
        using GlStateScope state = new(gl);
        try
        {
            computeProgram = gl.BuildProgram((NativeGl.ComputeShader, ComputeSource));
            drawProgram = gl.BuildProgram((NativeGl.VertexShader, VertexSource), (NativeGl.FragmentShader, FragmentSource));
            gl.GenBuffers(1, out completed);
            gl.GenBuffers(1, out scratch);
            gl.GenVertexArrays(1, out vertexArray);
            fixed (Particle* pointer = initial)
            {
                gl.BindBuffer(NativeGl.ShaderStorageBuffer, completed);
                gl.BufferData(NativeGl.ShaderStorageBuffer, count * sizeof(Particle), pointer, NativeGl.DynamicCopy);
                gl.BindBuffer(NativeGl.ShaderStorageBuffer, scratch);
                gl.BufferData(NativeGl.ShaderStorageBuffer, count * sizeof(Particle), null, NativeGl.DynamicCopy);
            }
            gl.Check("particle buffer construction");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Submits one fixed translation step and publishes its GPU-visible output.</summary>
    /// <remarks>Publication means subsequent commands in this same context can consume the result;
    /// it does not mean the CPU has waited for the GPU to finish. CPU observation requires Capture.</remarks>
    internal void Step()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        gl.Check("before compute submission");
        using GlStateScope state = new(gl);
        gl.MemoryBarrier(NativeGl.ShaderStorageBarrier);
        gl.UseProgram(computeProgram);
        gl.BindBufferBase(NativeGl.ShaderStorageBuffer, 0, completed);
        gl.BindBufferBase(NativeGl.ShaderStorageBuffer, 1, scratch);
        gl.DispatchCompute((uint)((count + 255) / 256), 1, 1);
        gl.MemoryBarrier(NativeGl.ShaderStorageBarrier | NativeGl.VertexAttribBarrier | NativeGl.BufferUpdateBarrier);
        gl.Check("compute output publication");
        (completed, scratch) = (scratch, completed);
    }

    /// <summary>Uses the very same completed storage-buffer object as a vertex buffer.</summary>
    internal void Draw()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using GlStateScope state = new(gl);
        gl.UseProgram(drawProgram);
        gl.BindVertexArray(vertexArray);
        gl.BindBuffer(NativeGl.ArrayBuffer, completed);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, NativeGl.Float, 0, sizeof(Particle), 0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribIPointer(1, 1, NativeGl.UnsignedInt, sizeof(Particle), 24);
        gl.Disable(NativeGl.Blend);
        gl.Disable(NativeGl.DepthTest);
        gl.Disable(NativeGl.ScissorTest);
        gl.Disable(NativeGl.CullFace);
        gl.Enable(NativeGl.ProgramPointSize);
        gl.DrawArrays(0, 0, count);
        LastDrawnBuffer = completed;
        gl.Check("drawing the compute storage buffer as vertices");
    }

    /// <summary>Captures an owned exact copy at the current submitted boundary, blocking only this explicit operation.</summary>
    /// <remarks>A five-second bounded fence wait prevents a failed device from hanging the smoke indefinitely.
    /// This synchronous proof is not the asynchronous production capture policy. Buffer-update visibility
    /// and a completion fence have different roles; neither substitutes for the other.</remarks>
    internal Particle[] Capture()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        long started = Stopwatch.GetTimestamp();
        using GlStateScope state = new(gl);
        gl.MemoryBarrier(NativeGl.BufferUpdateBarrier);
        nint fence = gl.FenceSync(0x9117, 0);
        if (fence == 0)
        {
            throw new InvalidOperationException("The GPU capture fence could not be created.");
        }
        try
        {
            uint result;
            do
            {
                result = gl.ClientWaitSync(fence, 1, 10_000_000);
                if (result == 0x911D)
                {
                    throw new InvalidOperationException("The GPU capture fence failed.");
                }
                if (Stopwatch.GetElapsedTime(started).TotalSeconds >= 5)
                {
                    throw new TimeoutException("The GPU did not finish the requested capture boundary within five seconds.");
                }
            } while (result == 0x911B);
            Particle[] capture = new Particle[count];
            gl.BindBuffer(NativeGl.ShaderStorageBuffer, completed);
            fixed (Particle* pointer = capture)
            {
                gl.GetBufferSubData(NativeGl.ShaderStorageBuffer, 0, count * sizeof(Particle), pointer);
            }
            gl.Check("explicit state capture");
            ReadbackCount++;
            LastCaptureMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return capture;
        }
        finally
        {
            gl.DeleteSync(fence);
        }
    }

    public void Dispose()
    {
        if (!disposed)
        {
            if (completed != 0)
            {
                gl.DeleteBuffers(1, ref completed);
            }
            if (scratch != 0)
            {
                gl.DeleteBuffers(1, ref scratch);
            }
            if (vertexArray != 0)
            {
                gl.DeleteVertexArrays(1, ref vertexArray);
            }
            if (computeProgram != 0)
            {
                gl.DeleteProgram(computeProgram);
            }
            if (drawProgram != 0)
            {
                gl.DeleteProgram(drawProgram);
            }
            disposed = true;
        }
    }

    private const string ComputeSource = """
        #version 430
        layout(local_size_x = 256) in;
        struct Particle { vec2 position; vec2 velocity; uvec2 identity; uint species; uint padding; };
        layout(std430, binding = 0) readonly buffer InputState { Particle inputState[]; };
        layout(std430, binding = 1) writeonly buffer OutputState { Particle outputState[]; };
        void main() {
            uint i = gl_GlobalInvocationID.x;
            if (i >= 4096u) return;
            Particle next = inputState[i];
            next.position += next.velocity * (1.0 / 64.0);
            outputState[i] = next;
        }
        """;

    private const string VertexSource = """
        #version 430
        layout(location = 0) in vec2 position;
        layout(location = 1) in uint species;
        flat out uint particleSpecies;
        void main() {
            gl_Position = vec4(position, 0.0, 1.0);
            gl_PointSize = 3.0;
            particleSpecies = species;
        }
        """;

    private const string FragmentSource = """
        #version 430
        flat in uint particleSpecies;
        out vec4 colour;
        void main() {
            const vec3 palette[4] = vec3[4](vec3(1,0,0), vec3(0,1,0), vec3(0,0,1), vec3(1,1,0));
            colour = vec4(palette[particleSpecies], 1.0);
        }
        """;
}
