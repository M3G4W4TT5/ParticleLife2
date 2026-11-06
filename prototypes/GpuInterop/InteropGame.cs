using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace ParticleLife.GpuInterop;

/// <summary>Hosts the native interoperability proof in the same DesktopGL profile as the application.</summary>
/// <remarks>The only simulated rule is constant-velocity translation. The fixture is deliberately small;
/// timings are smoke observations, not particle-life performance measurements. All GPU calls execute
/// during Draw/LoadContent/UnloadContent on MonoGame's owner thread.</remarks>
internal sealed class InteropGame : Game
{
    private readonly string outputDirectory;
    private readonly GraphicsDeviceManager graphics;
    private readonly Particle[] expected = Particle.CreateInitial();
    private readonly Stopwatch lifetime = Stopwatch.StartNew();
    private NativeGl? gl;
    private ComputeParticles? particles;
    private SpriteBatch? batch;
    private Texture2D? pixel;
    private int completedSteps;
    private bool restored;
    private double passTime;
    private string capturedHash = string.Empty;
    private double stateCaptureMilliseconds;
    private uint capturedBuffer;
    private int originalParticlePixelChecks;
    private int finalParticlePixelChecks;
    private double frameReadbackMilliseconds;

    internal bool Passed { get; private set; }

    internal InteropGame(string outputDirectory)
    {
        this.outputDirectory = outputDirectory;
        graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 960,
            PreferredBackBufferHeight = 640,
            GraphicsProfile = GraphicsProfile.Reach,
            SynchronizeWithVerticalRetrace = true
        };
        IsFixedTimeStep = false;
        IsMouseVisible = true;
        Window.Title = "P1 GPU interop — running 4,096 particles / 120 fixed steps";
    }

    protected override void LoadContent()
    {
        batch = new SpriteBatch(GraphicsDevice);
        pixel = new Texture2D(GraphicsDevice, 1, 1);
        pixel.SetData(new[] { Color.White });
        gl = new NativeGl();
        Console.WriteLine($"OpenGL: {gl.String(0x1F02)}; vendor: {gl.String(0x1F00)}; renderer: {gl.String(0x1F01)}");
        particles = new ComputeParticles(gl, expected);
    }

    protected override void Update(GameTime gameTime)
    {
        if (Keyboard.GetState().IsKeyDown(Keys.Escape))
        {
            Exit();
        }
        if (Passed && lifetime.Elapsed.TotalSeconds - passTime >= 45)
        {
            Exit();
        }
        if (!Passed && lifetime.Elapsed.TotalSeconds > 30)
        {
            throw new TimeoutException("The bounded interoperability smoke exceeded 30 seconds.");
        }
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        NativeGl native = gl ?? throw new InvalidOperationException("Graphics were not initialized.");
        ComputeParticles deviceParticles = particles ?? throw new InvalidOperationException("Particles were not initialized.");
        if (completedSteps < 120)
        {
            deviceParticles.Step();
            Particle.AdvanceCpu(expected);
            completedSteps++;
        }
        GraphicsDevice.Clear(Color.Black);
        DrawMarker(new Rectangle(16, 16, 48, 48), Color.Magenta);
        deviceParticles.Draw();
        DrawMarker(new Rectangle(16, GraphicsDevice.Viewport.Height - 64, 48, 48), Color.Cyan);
        native.Check("MonoGame draw after native pass");

        if (completedSteps == 120 && !Passed)
        {
            VerifyBoundary(native, deviceParticles);
        }
        base.Draw(gameTime);
        GC.KeepAlive(graphics);
    }

    private void DrawMarker(Rectangle rectangle, Color colour)
    {
        batch!.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.PointClamp,
            DepthStencilState.None, RasterizerState.CullNone);
        batch.Draw(pixel!, rectangle, colour);
        batch.End();
    }

    private void VerifyBoundary(NativeGl native, ComputeParticles deviceParticles)
    {
        if (deviceParticles.CompletedBuffer != deviceParticles.LastDrawnBuffer)
        {
            throw new InvalidOperationException("The rendered object was not the completed compute buffer.");
        }
        byte[] rgba = ReadFramebuffer(native);
        int matchedParticles = VerifyPixels(rgba);
        if (!restored)
        {
            if (deviceParticles.ReadbackCount != 0)
            {
                throw new InvalidOperationException("Normal frames unexpectedly downloaded particle state.");
            }
            originalParticlePixelChecks = matchedParticles;
            Particle[] capture = deviceParticles.Capture();
            stateCaptureMilliseconds = deviceParticles.LastCaptureMilliseconds;
            capturedHash = Program.Hash(capture);
            if (capturedHash != Program.Hash(expected))
            {
                throw new InvalidOperationException("GPU capture differs from the CPU fixture in positions, velocities, identities, species or padding.");
            }
            capturedBuffer = deviceParticles.CompletedBuffer;
            string capturePath = Path.Combine(outputDirectory, "capture.bin");
            using (FileStream stream = new(capturePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(MemoryMarshal.AsBytes(capture.AsSpan()));
            }
            // Restore actual persisted bytes into fresh device buffers. No further numerical step occurs.
            byte[] stored = File.ReadAllBytes(capturePath);
            Particle[] loaded = MemoryMarshal.Cast<byte, Particle>(stored).ToArray();
            ComputeParticles replacement = new(native, loaded);
            try
            {
                if (Program.Hash(replacement.Capture()) != capturedHash)
                {
                    throw new InvalidOperationException("Restoring captured GPU bytes changed their stored precision or membership.");
                }
            }
            catch
            {
                replacement.Dispose();
                throw;
            }
            particles = replacement;
            deviceParticles.Dispose();
            restored = true;
        }
        else
        {
            finalParticlePixelChecks = matchedParticles;
            SaveFramebuffer(rgba);
            native.Check("saving framebuffer evidence");
            object report = new
            {
                status = "pass",
                fixture = "constant-velocity-2d-interop/1",
                purpose = "buffer-sharing proof; not particle-life solver or performance benchmark",
                platform = Environment.OSVersion.ToString(),
                runtime = Environment.Version.ToString(),
                monoGame = typeof(Game).Assembly.GetName().Version!.ToString(),
                graphicsProfile = GraphicsDevice.GraphicsProfile.ToString(),
                api = native.String(0x1F02),
                vendor = native.String(0x1F00),
                device = native.String(0x1F01),
                shadingLanguage = native.String(0x8B8C),
                particleCount = Particle.Count,
                completedSteps,
                timestep = Particle.Timestep,
                simulatedTime = completedSteps * Particle.Timestep,
                particleStrideBytes = Marshal.SizeOf<Particle>(),
                residentParticleBufferBytes = 2 * Particle.Count * Marshal.SizeOf<Particle>(),
                capturedBuffer,
                restoredComputeBuffer = deviceParticles.CompletedBuffer,
                restoredVertexBuffer = deviceParticles.LastDrawnBuffer,
                originalParticlePixelChecks,
                finalParticlePixelChecks,
                monoGameBeforeAndAfterPixelChecks = "pass",
                particleReadbacksDuring120NormalFrames = 0,
                explicitParticleReadbacks = 2,
                stateCaptureMilliseconds,
                frameReadbackMilliseconds,
                expectedSha256 = Program.Hash(expected),
                capturedSha256 = capturedHash,
                restoredSha256 = capturedHash,
                captureFormat = "prototype raw little-endian 32-byte records; positions/velocities float32; identity low/high uint32; species/padding uint32",
                synchronization = "same-context SSBO and vertex visibility barriers; bounded explicit capture fence; no per-frame CPU fence wait",
                captureFile = "capture.bin",
                screenshotFile = "framebuffer.png"
            };
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            using (FileStream stream = new(Path.Combine(outputDirectory, "result.json"), FileMode.CreateNew))
            using (StreamWriter writer = new(stream))
            {
                writer.Write(json);
            }
            Console.WriteLine(json);
            Passed = true;
            passTime = lifetime.Elapsed.TotalSeconds;
            Window.Title = "P1 GPU interop — PASS: shared buffer, capture/restore, MonoGame drawing (closes in 45 s)";
        }
    }

    private unsafe byte[] ReadFramebuffer(NativeGl native)
    {
        int width = GraphicsDevice.Viewport.Width;
        int height = GraphicsDevice.Viewport.Height;
        byte[] bytes = new byte[width * height * 4];
        long started = Stopwatch.GetTimestamp();
        fixed (byte* pointer = bytes)
        {
            // Four bytes per pixel preserve the host's default four-byte pack alignment.
            native.ReadPixels(0, 0, width, height, 0x1908, 0x1401, pointer);
        }
        frameReadbackMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        native.Check("explicit framebuffer evidence capture");
        return bytes;
    }

    private int VerifyPixels(byte[] rgba)
    {
        int width = GraphicsDevice.Viewport.Width;
        int height = GraphicsDevice.Viewport.Height;
        if (!Matches(rgba, width, 32, height - 33, 255, 0, 255)
            || !Matches(rgba, width, 32, 32, 0, 255, 255))
        {
            throw new InvalidOperationException("MonoGame pixels before/after native drawing were incorrect; GL state restoration failed.");
        }
        int matches = 0;
        foreach (Particle particle in expected)
        {
            int x = (int)((particle.X + 1) * width / 2);
            int y = (int)((particle.Y + 1) * height / 2);
            (int r, int g, int b) = particle.Species switch
            {
                0 => (255, 0, 0),
                1 => (0, 255, 0),
                2 => (0, 0, 255),
                _ => (255, 255, 0)
            };
            if (Matches(rgba, width, x, y, r, g, b))
            {
                matches++;
            }
        }
        if (matches != expected.Length)
        {
            throw new InvalidOperationException($"Only {matches}/{expected.Length} expected GPU particle centres rendered correctly.");
        }
        return matches;
    }

    private static bool Matches(byte[] rgba, int width, int x, int y, int r, int g, int b)
    {
        int offset = (y * width + x) * 4;
        return Math.Abs(rgba[offset] - r) <= 1 && Math.Abs(rgba[offset + 1] - g) <= 1
            && Math.Abs(rgba[offset + 2] - b) <= 1;
    }

    private void SaveFramebuffer(byte[] rgba)
    {
        int width = GraphicsDevice.Viewport.Width;
        int height = GraphicsDevice.Viewport.Height;
        Color[] topDown = new Color[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int source = ((height - 1 - y) * width + x) * 4;
                topDown[y * width + x] = new Color(rgba[source], rgba[source + 1], rgba[source + 2], rgba[source + 3]);
            }
        }
        using Texture2D image = new(GraphicsDevice, width, height);
        image.SetData(topDown);
        using FileStream stream = new(Path.Combine(outputDirectory, "framebuffer.png"), FileMode.CreateNew);
        image.SaveAsPng(stream, width, height);
    }

    protected override void UnloadContent()
    {
        particles?.Dispose();
        gl?.Dispose();
        pixel?.Dispose();
        batch?.Dispose();
        base.UnloadContent();
    }
}
