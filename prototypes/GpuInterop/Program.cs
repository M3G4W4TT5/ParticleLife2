using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace ParticleLife.GpuInterop;

/// <summary>Runs the bounded Windows/OpenGL interoperability experiment independently of the application.</summary>
internal static class Program
{
    internal static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--cpu-only")
            {
                Particle[] state = Particle.CreateInitial();
                for (int step = 0; step < 120; step++)
                {
                    Particle.AdvanceCpu(state);
                }
                string[] nativeGraphicsModules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                    .Select(module => module.ModuleName)
                    .Where(name => name.Equals("opengl32.dll", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("SDL2.dll", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("OpenAL32.dll", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (nativeGraphicsModules.Length != 0)
                {
                    throw new InvalidOperationException("CPU-only startup loaded native graphics modules.");
                }
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    status = "pass",
                    mode = "cpu-only",
                    count = state.Length,
                    steps = 120,
                    nativeGraphicsModules,
                    stateSha256 = Hash(state)
                }));
                return 0;
            }
            if (args.Length != 2 || args[0] != "--smoke")
            {
                Console.WriteLine("GPU interoperability prototype (not a particle-life solver).\n"
                    + "  --cpu-only             Run without a window or native graphics initialization.\n"
                    + "  --smoke OUTPUT_DIR     Run 120 fixed steps, check shared-buffer drawing and capture.\n"
                    + "Windows/OpenGL 4.3+ required for --smoke. Escape cancels without passing.");
                return args.Length == 0 || args is ["--help"] ? 0 : 2;
            }
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("The GPU adapter is currently Windows/WGL only; --cpu-only remains available.");
            }
            string outputDirectory = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(outputDirectory);
            using InteropGame game = new(outputDirectory);
            game.Run();
            return game.Passed ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    internal static string Hash(Particle[] particles) =>
        Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(particles.AsSpan())));
}

/// <summary>Defines the exact 32-byte std430/vertex format used only by this feasibility experiment.</summary>
/// <remarks>Positions are 2D clip coordinates. Velocity is clip distance per simulation second.
/// Identity is an exact unsigned 64-bit integer split into low/high words, never a float.
/// Padding is initialized and copied so complete byte comparisons are meaningful.</remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct Particle
{
    internal float X;
    internal float Y;
    internal float VelocityX;
    internal float VelocityY;
    internal uint IdentityLow;
    internal uint IdentityHigh;
    internal uint Species;
    internal uint Padding;

    internal const int Count = 4096;
    internal const float Timestep = 1f / 64;

    internal static Particle[] CreateInitial()
    {
        Particle[] particles = new Particle[Count];
        for (int index = 0; index < particles.Length; index++)
        {
            particles[index] = new Particle
            {
                X = (index % 64 - 31.5f) / 48,
                Y = (index / 64 - 31.5f) / 48,
                VelocityX = 1f / 32,
                VelocityY = -1f / 64,
                IdentityLow = (uint)index + 100,
                IdentityHigh = 0x12345678,
                Species = (uint)(index % 4)
            };
        }
        return particles;
    }

    /// <summary>Advances the reference translation rule from each particle's previous position.</summary>
    /// <remarks>The timestep is a power of two, so velocity multiplication is exact in this fixture.
    /// This deliberately simple rule isolates memory layout and synchronization, not force parity.</remarks>
    internal static void AdvanceCpu(Particle[] particles)
    {
        for (int index = 0; index < particles.Length; index++)
        {
            particles[index].X += particles[index].VelocityX * Timestep;
            particles[index].Y += particles[index].VelocityY * Timestep;
        }
    }
}
