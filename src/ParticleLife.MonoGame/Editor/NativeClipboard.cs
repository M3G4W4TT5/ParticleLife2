using System.Runtime.InteropServices;

namespace ParticleLife.MonoGame.Editor;

/// <summary>Uses the already bundled SDL clipboard for native text fields without a UI dependency.</summary>
/// <remarks>Called only on an explicit copy/paste gesture on the window thread. SDL returns owned
/// UTF-8 clipboard memory; every read frees it with SDL_free, including an empty/error result.
/// Failures retain the native explanation for the viewer; return details and the selected library
/// path are written only to the launch console. An error is reported without assuming its cause.
/// See https://wiki.libsdl.org/SDL2/SDL_GetClipboardText and SDL_SetClipboardText.</remarks>
internal static class NativeClipboard
{
    private static readonly Lazy<Functions> Api = new(() => new Functions());

    internal static string Read()
    {
        Functions api = Api.Value;
        api.ClearError();
        IntPtr pointer = api.GetText();
        try
        {
            // SDL returns an allocated empty string on failure as well as for an empty clipboard.
            // Inspect the freshly cleared native error before freeing its separately owned data.
            string error = api.ErrorText();
            if (pointer == IntPtr.Zero || error.Length != 0)
            {
                throw api.Failure("read", pointer == IntPtr.Zero ? "null buffer" : "native error", error);
            }
            return Marshal.PtrToStringUTF8(pointer) ?? string.Empty;
        }
        finally
        {
            api.Free(pointer);
        }
    }

    internal static void Write(string text)
    {
        Functions api = Api.Value;
        api.ClearError();
        int result = api.SetText(text);
        if (result < 0)
        {
            throw api.Failure("write", $"result {result}", api.ErrorText());
        }
    }

    private sealed class Functions
    {
        internal readonly GetTextFunction GetText;
        internal readonly SetTextFunction SetText;
        internal readonly FreeFunction Free;
        internal readonly ClearErrorFunction ClearError;
        private readonly GetTextFunction getError;
        private readonly string libraryPath;

        internal Functions()
        {
            string name = OperatingSystem.IsWindows() ? "SDL2.dll"
                : OperatingSystem.IsMacOS() ? "libSDL2-2.0.0.dylib" : "libSDL2-2.0.so.0";
            string local = Path.Combine(AppContext.BaseDirectory, name);
            if (!File.Exists(local))
            {
                string platform = OperatingSystem.IsWindows() ? "win" : "linux";
                string runtime = OperatingSystem.IsMacOS() ? "osx"
                    : $"{platform}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
                local = Path.Combine(AppContext.BaseDirectory, "runtimes", runtime, "native", name);
            }
            // The process retains this library reference for the lifetime of the native host.
            libraryPath = File.Exists(local) ? local : name;
            IntPtr library = NativeLibrary.Load(libraryPath);
            GetText = Marshal.GetDelegateForFunctionPointer<GetTextFunction>(NativeLibrary.GetExport(library, "SDL_GetClipboardText"));
            SetText = Marshal.GetDelegateForFunctionPointer<SetTextFunction>(NativeLibrary.GetExport(library, "SDL_SetClipboardText"));
            Free = Marshal.GetDelegateForFunctionPointer<FreeFunction>(NativeLibrary.GetExport(library, "SDL_free"));
            getError = Marshal.GetDelegateForFunctionPointer<GetTextFunction>(NativeLibrary.GetExport(library, "SDL_GetError"));
            ClearError = Marshal.GetDelegateForFunctionPointer<ClearErrorFunction>(NativeLibrary.GetExport(library, "SDL_ClearError"));
        }

        // SDL owns this thread-local error string; unlike clipboard text it must not be freed.
        internal string ErrorText() => Marshal.PtrToStringUTF8(getError()) ?? string.Empty;

        internal InvalidOperationException Failure(string operation, string result, string error)
        {
            string detail = string.IsNullOrWhiteSpace(error) ? "The system clipboard is unavailable." : error.Trim();
            Console.Error.WriteLine($"Clipboard {operation} failed (SDL {result}): {detail} SDL library: {libraryPath}");
            return new InvalidOperationException($"Clipboard {operation} failed: {detail}");
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetTextFunction();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetTextFunction([MarshalAs(UnmanagedType.LPUTF8Str)] string text);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeFunction(IntPtr pointer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ClearErrorFunction();
}
