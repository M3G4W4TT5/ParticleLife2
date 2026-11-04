using System.Runtime.InteropServices;

namespace ParticleLife.MonoGame.Editor;

/// <summary>Uses the already bundled SDL clipboard for native text fields without a UI dependency.</summary>
/// <remarks>Called only on an explicit copy/paste gesture on the window thread. SDL returns owned
/// UTF-8 clipboard memory; every read frees it with SDL_free, including an empty/error result.
/// See https://wiki.libsdl.org/SDL2/SDL_GetClipboardText and SDL_SetClipboardText.</remarks>
internal static class NativeClipboard
{
    private static readonly Lazy<Functions> Api = new(() => new Functions());

    internal static string Read()
    {
        IntPtr pointer = Api.Value.GetText();
        try
        {
            return Marshal.PtrToStringUTF8(pointer) ?? string.Empty;
        }
        finally
        {
            Api.Value.Free(pointer);
        }
    }

    internal static void Write(string text)
    {
        if (Api.Value.SetText(text) < 0)
        {
            throw new InvalidOperationException("The system clipboard is unavailable.");
        }
    }

    private sealed class Functions
    {
        internal readonly GetTextFunction GetText;
        internal readonly SetTextFunction SetText;
        internal readonly FreeFunction Free;

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
            IntPtr library = NativeLibrary.Load(File.Exists(local) ? local : name);
            GetText = Marshal.GetDelegateForFunctionPointer<GetTextFunction>(NativeLibrary.GetExport(library, "SDL_GetClipboardText"));
            SetText = Marshal.GetDelegateForFunctionPointer<SetTextFunction>(NativeLibrary.GetExport(library, "SDL_SetClipboardText"));
            Free = Marshal.GetDelegateForFunctionPointer<FreeFunction>(NativeLibrary.GetExport(library, "SDL_free"));
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetTextFunction();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetTextFunction([MarshalAs(UnmanagedType.LPUTF8Str)] string text);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeFunction(IntPtr pointer);
}
