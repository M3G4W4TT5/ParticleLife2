using System.Runtime.InteropServices;
using System.Text;

namespace ParticleLife.MonoGame.Rendering;

/// <summary>Owns a Windows GL entry-point reference for the existing MonoGame context, requiring draw APIs only.</summary>
/// <remarks>OpenGL 3.3 is sufficient. No compute, SSBO, capture or GPU-simulation capability is loaded
/// by this class. A future compute adapter can load its additional API only after explicit capability
/// selection. All pointers belong to the current context and graphics thread. Native calls use bounded
/// pinned data; no private MonoGame fields or native resources are reflected or taken over.</remarks>
internal sealed unsafe class OpenGlDrawApi : IDisposable
{
    internal const uint ArrayBuffer = 0x8892, ArrayBufferBinding = 0x8894, TextureBuffer = 0x8C2A;
    internal const uint CurrentProgram = 0x8B8D, VertexArrayBinding = 0x85B5, TextureBindingBuffer = 0x8C2C;
    internal const uint ActiveTextureName = 0x84E0, TextureZero = 0x84C0;
    internal const uint Blend = 0x0BE2, DepthTest = 0x0B71, ScissorTest = 0x0C11, CullFace = 0x0B44, ProgramPointSize = 0x8642;
    internal const uint ViewportName = 0x0BA2, ScissorBox = 0x0C10;
    private readonly nint library;
    private readonly ProcAddress getProcAddress;
    private readonly CurrentContext getCurrentContext;
    private readonly nint context;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private bool disposed;

    internal readonly GetStringCall GetString;
    internal readonly GetIntegerCall GetInteger;
    internal readonly GetFloatCall GetFloat;
    internal readonly GetErrorCall GetError;
    internal readonly GenObjectsCall GenBuffers, GenVertexArrays, GenTextures;
    internal readonly DeleteObjectsCall DeleteBuffers, DeleteVertexArrays, DeleteTextures;
    internal readonly BindObjectCall BindBuffer, BindTexture;
    internal readonly ObjectCall BindVertexArray, UseProgram, EnableVertexAttribArray, Enable, Disable, ActiveTexture;
    internal readonly BufferDataCall BufferData;
    internal readonly BufferSubDataCall BufferSubData;
    internal readonly TextureBufferCall TexBuffer;
    internal readonly CreateShaderCall CreateShader;
    internal readonly ShaderSourceCall ShaderSource;
    internal readonly ObjectCall CompileShader, DeleteShader, LinkProgram, DeleteProgram;
    internal readonly GetObjectCall GetShader, GetProgram;
    internal readonly GetLogCall GetShaderLog, GetProgramLog;
    internal readonly CreateProgramCall CreateProgram;
    internal readonly AttachShaderCall AttachShader;
    internal readonly VertexAttribCall VertexAttribPointer;
    internal readonly VertexAttribIntegerCall VertexAttribIPointer;
    internal readonly DrawArraysCall DrawArrays;
    internal readonly IsEnabledCall IsEnabled;
    internal readonly RectangleCall Viewport, Scissor;
    internal readonly BlendFunctionCall BlendFuncSeparate;
    internal readonly BlendEquationCall BlendEquationSeparate;
    internal readonly UniformLocationCall GetUniformLocation;
    internal readonly UniformFloatCall Uniform1f;
    internal readonly UniformIntegerCall Uniform1i;
    internal readonly UniformVectorCall Uniform4f;

    internal string Description { get; }
    internal float MinimumPointDiameter { get; }
    internal float MaximumPointDiameter { get; }
    internal int MaximumPaletteEntries { get; }
    internal int MaximumViewportWidth { get; }
    internal int MaximumViewportHeight { get; }

    /// <summary>Reports whether native cleanup is safe; a lost context must not replace the original failure.</summary>
    internal bool CanUseOwner => !disposed && Environment.CurrentManagedThreadId == ownerThread && getCurrentContext() == context;

    internal OpenGlDrawApi()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Native GL points currently use Windows/WGL; MonoGame bulk quads remain available.");
        }
        library = NativeLibrary.Load("opengl32.dll");
        try
        {
            getProcAddress = Marshal.GetDelegateForFunctionPointer<ProcAddress>(NativeLibrary.GetExport(library, "wglGetProcAddress"));
            getCurrentContext = Marshal.GetDelegateForFunctionPointer<CurrentContext>(NativeLibrary.GetExport(library, "wglGetCurrentContext"));
            context = getCurrentContext();
            if (context == 0)
            {
                throw new NotSupportedException("The MonoGame graphics thread has no current WGL context.");
            }
            GetString = Load<GetStringCall>("glGetString");
            string versionText = Marshal.PtrToStringAnsi(GetString(0x1F02)) ?? "unknown";
            string versionNumber = versionText.Split(' ')[0];
            if (!Version.TryParse(versionNumber, out Version? version) || version < new Version(3, 3))
            {
                throw new NotSupportedException($"Native bulk points require OpenGL 3.3; this context reports {versionText}.");
            }
            Description = "OpenGL bulk points (" + versionText + ")";
            GetInteger = Load<GetIntegerCall>("glGetIntegerv");
            GetFloat = Load<GetFloatCall>("glGetFloatv");
            GetError = Load<GetErrorCall>("glGetError");
            GenBuffers = Load<GenObjectsCall>("glGenBuffers");
            GenVertexArrays = Load<GenObjectsCall>("glGenVertexArrays");
            GenTextures = Load<GenObjectsCall>("glGenTextures");
            DeleteBuffers = Load<DeleteObjectsCall>("glDeleteBuffers");
            DeleteVertexArrays = Load<DeleteObjectsCall>("glDeleteVertexArrays");
            DeleteTextures = Load<DeleteObjectsCall>("glDeleteTextures");
            BindBuffer = Load<BindObjectCall>("glBindBuffer");
            BindTexture = Load<BindObjectCall>("glBindTexture");
            BindVertexArray = Load<ObjectCall>("glBindVertexArray");
            UseProgram = Load<ObjectCall>("glUseProgram");
            EnableVertexAttribArray = Load<ObjectCall>("glEnableVertexAttribArray");
            Enable = Load<ObjectCall>("glEnable");
            Disable = Load<ObjectCall>("glDisable");
            ActiveTexture = Load<ObjectCall>("glActiveTexture");
            BufferData = Load<BufferDataCall>("glBufferData");
            BufferSubData = Load<BufferSubDataCall>("glBufferSubData");
            TexBuffer = Load<TextureBufferCall>("glTexBuffer");
            CreateShader = Load<CreateShaderCall>("glCreateShader");
            ShaderSource = Load<ShaderSourceCall>("glShaderSource");
            CompileShader = Load<ObjectCall>("glCompileShader");
            DeleteShader = Load<ObjectCall>("glDeleteShader");
            LinkProgram = Load<ObjectCall>("glLinkProgram");
            DeleteProgram = Load<ObjectCall>("glDeleteProgram");
            GetShader = Load<GetObjectCall>("glGetShaderiv");
            GetProgram = Load<GetObjectCall>("glGetProgramiv");
            GetShaderLog = Load<GetLogCall>("glGetShaderInfoLog");
            GetProgramLog = Load<GetLogCall>("glGetProgramInfoLog");
            CreateProgram = Load<CreateProgramCall>("glCreateProgram");
            AttachShader = Load<AttachShaderCall>("glAttachShader");
            VertexAttribPointer = Load<VertexAttribCall>("glVertexAttribPointer");
            VertexAttribIPointer = Load<VertexAttribIntegerCall>("glVertexAttribIPointer");
            DrawArrays = Load<DrawArraysCall>("glDrawArrays");
            IsEnabled = Load<IsEnabledCall>("glIsEnabled");
            Viewport = Load<RectangleCall>("glViewport");
            Scissor = Load<RectangleCall>("glScissor");
            BlendFuncSeparate = Load<BlendFunctionCall>("glBlendFuncSeparate");
            BlendEquationSeparate = Load<BlendEquationCall>("glBlendEquationSeparate");
            GetUniformLocation = Load<UniformLocationCall>("glGetUniformLocation");
            Uniform1f = Load<UniformFloatCall>("glUniform1f");
            Uniform1i = Load<UniformIntegerCall>("glUniform1i");
            Uniform4f = Load<UniformVectorCall>("glUniform4f");
            float* pointRange = stackalloc float[2];
            GetFloat(0x846D, pointRange);
            MinimumPointDiameter = pointRange[0];
            MaximumPointDiameter = pointRange[1];
            MaximumPaletteEntries = Integer(0x8C2B);
            int* viewportRange = stackalloc int[2];
            GetInteger(0x0D3A, viewportRange);
            MaximumViewportWidth = viewportRange[0];
            MaximumViewportHeight = viewportRange[1];
            Check("loading native drawing capabilities");
        }
        catch
        {
            NativeLibrary.Free(library);
            throw;
        }
    }

    internal int Integer(uint name)
    {
        int result;
        GetInteger(name, &result);
        return result;
    }

    internal GlRectangle Rectangle(uint name)
    {
        int* result = stackalloc int[4];
        GetInteger(name, result);
        return new(result[0], result[1], result[2], result[3]);
    }

    internal void EnsureOwner()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Environment.CurrentManagedThreadId != ownerThread || getCurrentContext() != context)
        {
            throw new InvalidOperationException("The native renderer requires its original graphics thread and current context.");
        }
    }

    internal void Check(string operation)
    {
        EnsureOwner();
        uint error = GetError();
        if (error != 0)
        {
            throw new InvalidOperationException($"OpenGL error 0x{error:X} during {operation}.");
        }
    }

    internal uint BuildProgram(string vertexSource, string fragmentSource)
    {
        uint program = CreateProgram();
        try
        {
            AttachStage(program, 0x8B31, vertexSource);
            AttachStage(program, 0x8B30, fragmentSource);
            LinkProgram(program);
            GetProgram(program, 0x8B82, out int success);
            if (success == 0)
            {
                throw new InvalidOperationException("Particle program linking failed: " + ReadLog(program, GetProgramLog));
            }
            return program;
        }
        catch
        {
            DeleteProgram(program);
            throw;
        }
    }

    private void AttachStage(uint program, uint stage, string source)
    {
        uint shader = CreateShader(stage);
        try
        {
            byte[] sourceBytes = Encoding.UTF8.GetBytes(source + '\0');
            fixed (byte* bytes = sourceBytes)
            {
                byte* pointer = bytes;
                ShaderSource(shader, 1, &pointer, null);
            }
            CompileShader(shader);
            GetShader(shader, 0x8B81, out int success);
            if (success == 0)
            {
                throw new InvalidOperationException("Particle shader compilation failed: " + ReadLog(shader, GetShaderLog));
            }
            AttachShader(program, shader);
        }
        finally
        {
            DeleteShader(shader);
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

    internal T Load<T>(string name) where T : Delegate
    {
        nint address = getProcAddress(name);
        if (address == 0 || address == 1 || address == 2 || address == 3 || address == -1)
        {
            if (!NativeLibrary.TryGetExport(library, name, out address))
            {
                throw new NotSupportedException($"The current graphics context does not expose {name}.");
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
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GetIntegerCall(uint name, int* values);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GetFloatCall(uint name, float* values);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint GetErrorCall();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GenObjectsCall(int count, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void DeleteObjectsCall(int count, ref uint value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void BindObjectCall(uint target, uint value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void ObjectCall(uint value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void BufferDataCall(uint target, nint size, void* data, uint usage);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void BufferSubDataCall(uint target, nint offset, nint size, void* data);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void TextureBufferCall(uint target, uint format, uint buffer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint CreateShaderCall(uint type);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void ShaderSourceCall(uint shader, int count, byte** strings, int* lengths);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GetObjectCall(uint value, uint name, out int result);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GetLogCall(uint value, int capacity, out int length, byte* log);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint CreateProgramCall();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void AttachShaderCall(uint program, uint shader);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void VertexAttribCall(uint index, int count, uint type, byte normalized, int stride, nint offset);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void VertexAttribIntegerCall(uint index, int count, uint type, int stride, nint offset);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void DrawArraysCall(uint mode, int first, int count);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate byte IsEnabledCall(uint capability);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void RectangleCall(int x, int y, int width, int height);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void BlendFunctionCall(uint sourceRgb, uint destinationRgb, uint sourceAlpha, uint destinationAlpha);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void BlendEquationCall(uint rgb, uint alpha);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate int UniformLocationCall(uint program, [MarshalAs(UnmanagedType.LPStr)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void UniformFloatCall(int location, float value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void UniformIntegerCall(int location, int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void UniformVectorCall(int location, float x, float y, float z, float w);
}

/// <summary>Stores four native GL rectangle integers without heap allocation.</summary>
internal readonly record struct GlRectangle(int X, int Y, int Width, int Height);
