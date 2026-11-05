using System.Runtime.InteropServices;
using System.Text;

namespace ParticleLife.GpuInterop;

/// <summary>Loads the small OpenGL surface required by the prototype from the current WGL context.</summary>
/// <remarks>Construction and every call must occur on MonoGame's graphics thread with its context current.
/// No private MonoGame handles, shared second context, or external compute allocation is involved.
/// Unsafe pointers are bounded pinned buffers used only for synchronous native API calls.</remarks>
internal sealed unsafe class NativeGl : IDisposable
{
    internal const uint ArrayBuffer = 0x8892, ArrayBufferBinding = 0x8894, ShaderStorageBuffer = 0x90D2;
    internal const uint ShaderStorageBinding = 0x90D3, CurrentProgram = 0x8B8D, VertexArrayBinding = 0x85B5;
    internal const uint VertexShader = 0x8B31, FragmentShader = 0x8B30, ComputeShader = 0x91B9;
    internal const uint Float = 0x1406, UnsignedInt = 0x1405, DynamicCopy = 0x88EA;
    internal const uint ShaderStorageBarrier = 0x2000, VertexAttribBarrier = 0x1, BufferUpdateBarrier = 0x200;
    internal const uint Blend = 0xBE2, DepthTest = 0xB71, ScissorTest = 0xC11, CullFace = 0xB44, ProgramPointSize = 0x8642;
    private readonly nint library;
    private readonly ProcAddress getProcAddress;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly nint context;
    private readonly CurrentContext getCurrentContext;
    private bool disposed;

    internal readonly GetStringCall GetString;
    internal readonly GetIntegerCall GetInteger;
    internal readonly GetIntegerIndexedCall GetIntegerIndexed;
    internal readonly GetErrorCall GetError;
    internal readonly GenObjectsCall GenBuffers, GenVertexArrays;
    internal readonly DeleteObjectsCall DeleteBuffers, DeleteVertexArrays;
    internal readonly BindObjectCall BindBuffer;
    internal readonly BindVertexArrayCall BindVertexArray;
    internal readonly BufferDataCall BufferData;
    internal readonly GetBufferSubDataCall GetBufferSubData;
    internal readonly BindBufferBaseCall BindBufferBase;
    internal readonly CreateShaderCall CreateShader;
    internal readonly ShaderSourceCall ShaderSource;
    internal readonly ObjectCall CompileShader, DeleteShader, LinkProgram, DeleteProgram, UseProgram, EnableVertexAttribArray;
    internal readonly GetObjectCall GetShader, GetProgram;
    internal readonly GetLogCall GetShaderLog, GetProgramLog;
    internal readonly CreateProgramCall CreateProgram;
    internal readonly AttachShaderCall AttachShader;
    internal readonly DispatchCall DispatchCompute;
    internal readonly BarrierCall MemoryBarrier;
    internal readonly VertexAttribCall VertexAttribPointer;
    internal readonly VertexAttribIntegerCall VertexAttribIPointer;
    internal readonly DrawArraysCall DrawArrays;
    internal readonly IsEnabledCall IsEnabled;
    internal readonly ObjectCall Enable, Disable;
    internal readonly FenceCall FenceSync;
    internal readonly WaitCall ClientWaitSync;
    internal readonly DeleteSyncCall DeleteSync;
    internal readonly ReadPixelsCall ReadPixels;

    internal NativeGl()
    {
        library = NativeLibrary.Load("opengl32.dll");
        getProcAddress = Marshal.GetDelegateForFunctionPointer<ProcAddress>(NativeLibrary.GetExport(library, "wglGetProcAddress"));
        getCurrentContext = Marshal.GetDelegateForFunctionPointer<CurrentContext>(NativeLibrary.GetExport(library, "wglGetCurrentContext"));
        context = getCurrentContext();
        if (context == 0)
        {
            throw new InvalidOperationException("MonoGame has no current WGL context on the caller thread.");
        }
        GetString = Load<GetStringCall>("glGetString");
        GetInteger = Load<GetIntegerCall>("glGetIntegerv");
        GetIntegerIndexed = Load<GetIntegerIndexedCall>("glGetIntegeri_v");
        GetError = Load<GetErrorCall>("glGetError");
        GetInteger(0x821B, out int major);
        GetInteger(0x821C, out int minor);
        if (major < 4 || (major == 4 && minor < 3))
        {
            throw new NotSupportedException($"OpenGL 4.3+ is required; current context is {String(0x1F02)}. CPU remains supported.");
        }
        GenBuffers = Load<GenObjectsCall>("glGenBuffers");
        DeleteBuffers = Load<DeleteObjectsCall>("glDeleteBuffers");
        GenVertexArrays = Load<GenObjectsCall>("glGenVertexArrays");
        DeleteVertexArrays = Load<DeleteObjectsCall>("glDeleteVertexArrays");
        BindBuffer = Load<BindObjectCall>("glBindBuffer");
        BindVertexArray = Load<BindVertexArrayCall>("glBindVertexArray");
        BufferData = Load<BufferDataCall>("glBufferData");
        GetBufferSubData = Load<GetBufferSubDataCall>("glGetBufferSubData");
        BindBufferBase = Load<BindBufferBaseCall>("glBindBufferBase");
        CreateShader = Load<CreateShaderCall>("glCreateShader");
        ShaderSource = Load<ShaderSourceCall>("glShaderSource");
        CompileShader = Load<ObjectCall>("glCompileShader");
        DeleteShader = Load<ObjectCall>("glDeleteShader");
        GetShader = Load<GetObjectCall>("glGetShaderiv");
        GetShaderLog = Load<GetLogCall>("glGetShaderInfoLog");
        CreateProgram = Load<CreateProgramCall>("glCreateProgram");
        AttachShader = Load<AttachShaderCall>("glAttachShader");
        LinkProgram = Load<ObjectCall>("glLinkProgram");
        DeleteProgram = Load<ObjectCall>("glDeleteProgram");
        UseProgram = Load<ObjectCall>("glUseProgram");
        GetProgram = Load<GetObjectCall>("glGetProgramiv");
        GetProgramLog = Load<GetLogCall>("glGetProgramInfoLog");
        DispatchCompute = Load<DispatchCall>("glDispatchCompute");
        MemoryBarrier = Load<BarrierCall>("glMemoryBarrier");
        EnableVertexAttribArray = Load<ObjectCall>("glEnableVertexAttribArray");
        VertexAttribPointer = Load<VertexAttribCall>("glVertexAttribPointer");
        VertexAttribIPointer = Load<VertexAttribIntegerCall>("glVertexAttribIPointer");
        DrawArrays = Load<DrawArraysCall>("glDrawArrays");
        IsEnabled = Load<IsEnabledCall>("glIsEnabled");
        Enable = Load<ObjectCall>("glEnable");
        Disable = Load<ObjectCall>("glDisable");
        FenceSync = Load<FenceCall>("glFenceSync");
        ClientWaitSync = Load<WaitCall>("glClientWaitSync");
        DeleteSync = Load<DeleteSyncCall>("glDeleteSync");
        ReadPixels = Load<ReadPixelsCall>("glReadPixels");
        Check("loading OpenGL entry points");
    }

    internal string String(uint name) => Marshal.PtrToStringAnsi(GetString(name)) ?? "unknown";

    internal void Check(string operation)
    {
        if (Environment.CurrentManagedThreadId != ownerThread || getCurrentContext() != context)
        {
            throw new InvalidOperationException("OpenGL ownership moved to another thread or context.");
        }
        uint error = GetError();
        if (error != 0)
        {
            throw new InvalidOperationException($"OpenGL error 0x{error:X} during {operation}.");
        }
    }

    internal uint BuildProgram(params (uint Type, string Source)[] stages)
    {
        uint program = CreateProgram();
        try
        {
            foreach ((uint type, string source) in stages)
            {
                uint shader = CreateShader(type);
                try
                {
                    byte[] utf8 = Encoding.UTF8.GetBytes(source + '\0');
                    fixed (byte* bytes = utf8)
                    {
                        byte* address = bytes;
                        ShaderSource(shader, 1, &address, null);
                    }
                    CompileShader(shader);
                    GetShader(shader, 0x8B81, out int success);
                    if (success == 0)
                    {
                        throw new InvalidOperationException("Shader compilation: " + ReadLog(shader, GetShaderLog));
                    }
                    AttachShader(program, shader);
                }
                finally
                {
                    DeleteShader(shader);
                }
            }
            LinkProgram(program);
            GetProgram(program, 0x8B82, out int linked);
            if (linked == 0)
            {
                throw new InvalidOperationException("Program linking: " + ReadLog(program, GetProgramLog));
            }
            return program;
        }
        catch
        {
            DeleteProgram(program);
            throw;
        }
    }

    private static string ReadLog(uint handle, GetLogCall call)
    {
        byte[] bytes = new byte[8192];
        fixed (byte* pointer = bytes)
        {
            call(handle, bytes.Length, out int length, pointer);
            return Encoding.UTF8.GetString(bytes, 0, Math.Max(0, length));
        }
    }

    private T Load<T>(string name) where T : Delegate
    {
        nint address = getProcAddress(name);
        if (address == 0 || address == 1 || address == 2 || address == 3 || address == -1)
        {
            if (!NativeLibrary.TryGetExport(library, name, out address))
            {
                throw new NotSupportedException($"The current OpenGL context does not expose {name}.");
            }
        }
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    public void Dispose()
    {
        if (!disposed)
        {
            NativeLibrary.Free(library);
            disposed = true;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint ProcAddress([MarshalAs(UnmanagedType.LPStr)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint CurrentContext();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate nint GetStringCall(uint name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GetIntegerCall(uint name, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GetIntegerIndexedCall(uint name, uint index, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint GetErrorCall();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GenObjectsCall(int count, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void DeleteObjectsCall(int count, ref uint value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void BindObjectCall(uint target, uint value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void BindVertexArrayCall(uint value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void BufferDataCall(uint target, nint size, void* data, uint usage);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GetBufferSubDataCall(uint target, nint offset, nint size, void* data);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void BindBufferBaseCall(uint target, uint index, uint value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint CreateShaderCall(uint type);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void ShaderSourceCall(uint shader, int count, byte** strings, int* lengths);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void ObjectCall(uint value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GetObjectCall(uint value, uint name, out int result);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GetLogCall(uint value, int capacity, out int length, byte* log);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint CreateProgramCall();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void AttachShaderCall(uint program, uint shader);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void DispatchCall(uint x, uint y, uint z);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void BarrierCall(uint barriers);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void VertexAttribCall(uint index, int count, uint type, byte normalized, int stride, nint offset);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void VertexAttribIntegerCall(uint index, int count, uint type, int stride, nint offset);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void DrawArraysCall(uint mode, int first, int count);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate byte IsEnabledCall(uint capability);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate nint FenceCall(uint condition, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint WaitCall(nint sync, uint flags, ulong timeout);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void DeleteSyncCall(nint sync);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void ReadPixelsCall(int x, int y, int width, int height, uint format, uint type, void* data);
}
