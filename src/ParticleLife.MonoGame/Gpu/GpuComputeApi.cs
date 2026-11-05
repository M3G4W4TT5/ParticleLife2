using System.Runtime.InteropServices;
using System.Text;
using ParticleLife.MonoGame.Rendering;

namespace ParticleLife.MonoGame.Gpu;

/// <summary>Loads optional OpenGL 4.3 compute, synchronization and explicit-copy functions on the graphics owner.</summary>
/// <remarks>CPU renderer construction never loads this surface. All resources share the existing
/// MonoGame context; no secondary device, shared-context handoff or external compute buffer exists.</remarks>
internal sealed unsafe class GpuComputeApi : IDisposable
{
    internal const uint StorageBuffer = 0x90D2, StorageBinding = 0x90D3;
    internal const uint CopyReadBuffer = 0x8F36, CopyWriteBuffer = 0x8F37;
    internal const uint StorageBarrier = 0x2000, VertexBarrier = 0x1, BufferUpdateBarrier = 0x200;
    internal readonly OpenGlDrawApi Gl;
    internal readonly BindBufferBaseCall BindBufferBase;
    internal readonly BindBufferRangeCall BindBufferRange;
    internal readonly GetIndexedCall GetIndexed;
    internal readonly GetIndexed64Call GetIndexed64;
    internal readonly GetInteger64Call GetInteger64;
    internal readonly DispatchCall DispatchCompute;
    internal readonly BarrierCall MemoryBarrier;
    internal readonly GetBufferCall GetBufferSubData;
    internal readonly CopyBufferCall CopyBufferSubData;
    internal readonly FenceCall FenceSync;
    internal readonly WaitCall ClientWaitSync;
    internal readonly DeleteSyncCall DeleteSync;
    internal readonly UniformIntegerVectorCall Uniform4i;
    internal readonly UniformFloatVectorCall Uniform2f;
    internal readonly ObjectCall Flush;
    internal readonly long MaximumStorageBytes;
    internal readonly int MaximumDispatchGroups;

    internal GpuComputeApi()
    {
        Gl = new OpenGlDrawApi();
        try
        {
            int major = Gl.Integer(0x821B), minor = Gl.Integer(0x821C);
            if (major < 4 || (major == 4 && minor < 3))
            {
                throw new NotSupportedException("GPU simulation requires OpenGL 4.3 compute support; CPU simulation remains available.");
            }
            BindBufferBase = Gl.Load<BindBufferBaseCall>("glBindBufferBase");
            BindBufferRange = Gl.Load<BindBufferRangeCall>("glBindBufferRange");
            GetIndexed = Gl.Load<GetIndexedCall>("glGetIntegeri_v");
            GetIndexed64 = Gl.Load<GetIndexed64Call>("glGetInteger64i_v");
            GetInteger64 = Gl.Load<GetInteger64Call>("glGetInteger64v");
            DispatchCompute = Gl.Load<DispatchCall>("glDispatchCompute");
            MemoryBarrier = Gl.Load<BarrierCall>("glMemoryBarrier");
            GetBufferSubData = Gl.Load<GetBufferCall>("glGetBufferSubData");
            CopyBufferSubData = Gl.Load<CopyBufferCall>("glCopyBufferSubData");
            FenceSync = Gl.Load<FenceCall>("glFenceSync");
            ClientWaitSync = Gl.Load<WaitCall>("glClientWaitSync");
            DeleteSync = Gl.Load<DeleteSyncCall>("glDeleteSync");
            Uniform4i = Gl.Load<UniformIntegerVectorCall>("glUniform4i");
            Uniform2f = Gl.Load<UniformFloatVectorCall>("glUniform2f");
            Flush = Gl.Load<ObjectCall>("glFlush");
            GetInteger64(0x90DE, out MaximumStorageBytes);
            GetIndexed(0x91BE, 0, out MaximumDispatchGroups);
            if (Gl.Integer(0x90DD) < 8)
            {
                throw new NotSupportedException("GPU simulation requires eight shader-storage binding points.");
            }
            Gl.Check("loading optional GPU execution support");
        }
        catch
        {
            Gl.Dispose();
            throw;
        }
    }

    internal uint CreateBuffer(nint bytes, void* data = null)
    {
        if (bytes <= 0 || bytes > MaximumStorageBytes)
        {
            throw new NotSupportedException($"The requested GPU buffer ({bytes} bytes) exceeds this device's per-buffer storage limit.");
        }
        Gl.GenBuffers(1, out uint buffer);
        try
        {
            Gl.BindBuffer(StorageBuffer, buffer);
            Gl.BufferData(StorageBuffer, bytes, data, 0x88EA);
            Gl.Check("allocating a resident buffer");
            return buffer;
        }
        catch
        {
            if (buffer != 0)
            {
                Gl.DeleteBuffers(1, ref buffer);
            }
            throw;
        }
    }

    internal uint BuildComputeProgram(string source)
    {
        uint shader = Gl.CreateShader(0x91B9), program = 0;
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(source + '\0');
            fixed (byte* pointer = bytes)
            {
                byte* address = pointer;
                Gl.ShaderSource(shader, 1, &address, null);
            }
            Gl.CompileShader(shader);
            Gl.GetShader(shader, 0x8B81, out int compiled);
            if (compiled == 0)
            {
                throw new InvalidOperationException("GPU kernel compilation failed: " + Log(shader, Gl.GetShaderLog));
            }
            program = Gl.CreateProgram();
            Gl.AttachShader(program, shader);
            Gl.LinkProgram(program);
            Gl.GetProgram(program, 0x8B82, out int linked);
            if (linked == 0)
            {
                throw new InvalidOperationException("GPU kernel linking failed: " + Log(program, Gl.GetProgramLog));
            }
            return program;
        }
        catch
        {
            if (program != 0)
            {
                Gl.DeleteProgram(program);
            }
            throw;
        }
        finally
        {
            Gl.DeleteShader(shader);
        }
    }

    /// <summary>Polls without a positive timeout; fences distinguish device completion from memory visibility.</summary>
    internal bool IsComplete(nint fence)
    {
        uint result = ClientWaitSync(fence, 1, 0);
        if (result == 0x911D)
        {
            throw new InvalidOperationException("The GPU completion fence failed. Continue only from a valid captured state.");
        }
        return result is 0x911A or 0x911C;
    }

    internal nint Fence()
    {
        nint fence = FenceSync(0x9117, 0);
        if (fence == 0)
        {
            Gl.Check("creating a GPU completion fence");
            throw new InvalidOperationException("The device could not create a completion fence.");
        }
        Flush();
        return fence;
    }

    private static string Log(uint handle, OpenGlDrawApi.GetLogCall call)
    {
        byte[] bytes = new byte[8192];
        fixed (byte* pointer = bytes)
        {
            call(handle, bytes.Length, out int length, pointer);
            return Encoding.UTF8.GetString(bytes, 0, Math.Max(0, length));
        }
    }

    public void Dispose() => Gl.Dispose();

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void BindBufferBaseCall(uint target, uint index, uint buffer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void BindBufferRangeCall(uint target, uint index, uint buffer, nint offset, nint size);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GetIndexedCall(uint name, uint index, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GetIndexed64Call(uint name, uint index, out long value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GetInteger64Call(uint name, out long value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void DispatchCall(uint x, uint y, uint z);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void BarrierCall(uint bits);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GetBufferCall(uint target, nint offset, nint size, void* data);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void CopyBufferCall(uint readTarget, uint writeTarget, nint readOffset, nint writeOffset, nint size);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate nint FenceCall(uint condition, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint WaitCall(nint sync, uint flags, ulong timeout);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void DeleteSyncCall(nint sync);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void UniformIntegerVectorCall(int location, int x, int y, int z, int w);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void UniformFloatVectorCall(int location, float x, float y);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void ObjectCall();
}

/// <summary>Preserves whole/ranged SSBO bindings and copy targets around one bounded compute submission.</summary>
/// <remarks>Eight saved slots are fixed inline storage, not a per-stage heap allocation. The scope never
/// touches vertex/texture/framebuffer state. It restores generic bindings after indexed bindings.</remarks>
internal unsafe ref struct GpuComputeScope
{
    private readonly GpuComputeApi api;
    private readonly int program, storage, copyRead, copyWrite;
    private fixed int buffers[8];
    private fixed long starts[8];
    private fixed long sizes[8];

    internal GpuComputeScope(GpuComputeApi api)
    {
        this.api = api;
        api.Gl.EnsureOwner();
        program = api.Gl.Integer(OpenGlDrawApi.CurrentProgram);
        storage = api.Gl.Integer(GpuComputeApi.StorageBinding);
        copyRead = api.Gl.Integer(GpuComputeApi.CopyReadBuffer);
        copyWrite = api.Gl.Integer(GpuComputeApi.CopyWriteBuffer);
        for (uint index = 0; index < 8; index++)
        {
            api.GetIndexed(GpuComputeApi.StorageBinding, index, out int buffer);
            api.GetIndexed64(0x90D4, index, out long start);
            api.GetIndexed64(0x90D5, index, out long size);
            buffers[index] = buffer;
            starts[index] = start;
            sizes[index] = size;
        }
    }

    public void Dispose()
    {
        api.Gl.UseProgram((uint)program);
        for (uint index = 0; index < 8; index++)
        {
            if (buffers[index] != 0 && sizes[index] > 0)
            {
                api.BindBufferRange(GpuComputeApi.StorageBuffer, index, (uint)buffers[index], (nint)starts[index], (nint)sizes[index]);
            }
            else
            {
                api.BindBufferBase(GpuComputeApi.StorageBuffer, index, (uint)buffers[index]);
            }
        }
        api.Gl.BindBuffer(GpuComputeApi.StorageBuffer, (uint)storage);
        api.Gl.BindBuffer(GpuComputeApi.CopyReadBuffer, (uint)copyRead);
        api.Gl.BindBuffer(GpuComputeApi.CopyWriteBuffer, (uint)copyWrite);
    }
}
