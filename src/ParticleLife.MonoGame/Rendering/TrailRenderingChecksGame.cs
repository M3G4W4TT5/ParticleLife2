using System.Diagnostics;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ParticleLife.Viewer;

namespace ParticleLife.MonoGame.Rendering;

/// <summary>Checks actual trail accumulation, decay and lifetime through the production effect pipeline.</summary>
/// <remarks>Explicit, bounded graphics diagnostic only. Tiny offscreen images use synthetic presentation
/// time and no simulation. The sixty-second watchdog bounds host progress; a supervising process can
/// additionally terminate a blocked driver call. Framebuffer readbacks occur only at named checkpoints.</remarks>
internal sealed class TrailRenderingChecksGame : Game
{
    private readonly string outputDirectory;
    private readonly Stopwatch timeout = Stopwatch.StartNew();
    private readonly List<object> evidence = [];
    private IEnumerator<bool>? checks;
    private bool finished;
    internal int ExitCode { get; private set; } = 1;

    internal TrailRenderingChecksGame(string outputDirectory)
    {
        this.outputDirectory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(this.outputDirectory) && Directory.EnumerateFileSystemEntries(this.outputDirectory).Any())
        { throw new IOException("Trail checks require a fresh or empty output directory."); }
        Directory.CreateDirectory(this.outputDirectory);
        _ = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 320,
            PreferredBackBufferHeight = 240,
            GraphicsProfile = GraphicsProfile.Reach,
            SynchronizeWithVerticalRetrace = false
        };
        IsFixedTimeStep = false;
        InactiveSleepTime = TimeSpan.Zero;
        Window.Title = "Particle Life 2 - trail decay checks";
    }

    protected override void LoadContent() => checks = Verify().GetEnumerator();

    protected override void Update(GameTime gameTime)
    {
        if (finished) { Exit(); return; }
        try
        {
            if (timeout.Elapsed.TotalSeconds > 60) { throw new TimeoutException("Trail checks exceeded sixty seconds."); }
            long started = Stopwatch.GetTimestamp();
            do
            {
                if (!checks!.MoveNext())
                {
                    ExitCode = 0;
                    Finish(null);
                    Console.WriteLine("TRAIL_RENDERING_PASS");
                    break;
                }
            }
            while (Stopwatch.GetElapsedTime(started).TotalMilliseconds < 4);
        }
        catch (Exception failure) { Console.Error.WriteLine(failure); Finish(failure); }
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.SetRenderTarget(null);
        GraphicsDevice.Clear(Color.Black);
        base.Draw(gameTime);
    }

    private void Finish(Exception? failure)
    {
        finished = true;
        checks?.Dispose();
        File.WriteAllText(Path.Combine(outputDirectory, "trail-rendering-checks.json"),
            JsonSerializer.Serialize(new { Passed = ExitCode == 0, Error = failure?.ToString(), Evidence = evidence },
                new JsonSerializerOptions { WriteIndented = true }));
        Exit();
    }

    private IEnumerable<bool> Verify()
    {
        const int size = 32;
        using SpriteBatch sprites = new(GraphicsDevice);
        using WorldVisuals visuals = new(GraphicsDevice, sprites);
        using RenderTarget2D scene = new(GraphicsDevice, size, size, false, SurfaceFormat.Color, DepthFormat.None,
            0, RenderTargetUsage.PreserveContents);
        using Texture2D white = new(GraphicsDevice, 1, 1);
        white.SetData(new[] { Color.White });
        Color[] pixels = new Color[size * size];
        Color seed = new(192, 128, 64, 255);
        Rectangle seedBounds = new(3, 5, 7, 4);
        ParticleDisplayTransform mapping = new(1, 0, 0, size, size);
        long sequence = 0;

        foreach (double halfLife in new[] { 0.35, 8.0 })
        {
            AppearanceSettings appearance = new() { Visuals = new() { TrailHalfLifeSeconds = halfLife } };
            visuals.Clear();
            Render(appearance, mapping, sequence++, 0, paint: true);
            Require(visuals.RetainedTrailBytes == size * size * 32, "Trail history is missing or has the wrong payload size.");
            Match(Read($"seed-{halfLife}"), seed, 0, "initial premultiplied pixel");
            MatchFootprint(seed, 0, "initial off-centre footprint");
            int halfSteps = (int)Math.Round(halfLife * 144);
            int fadeSteps = (int)Math.Ceiling(12 * halfLife * 144);
            for (int step = 1; step <= fadeSteps; step++)
            {
                Render(appearance, mapping, sequence++, 1.0 / 144, paint: false);
                if (step == halfSteps)
                {
                    double retention = Math.Pow(0.5, step / 144.0 / halfLife);
                    Color expected = new((int)Math.Round(seed.R * retention), (int)Math.Round(seed.G * retention),
                        (int)Math.Round(seed.B * retention), (int)Math.Round(seed.A * retention));
                    Color actual = Read($"half-life-{halfLife}");
                    Match(actual, expected, 2, "one half-life");
                    MatchFootprint(expected, 2, "attenuated off-centre footprint");
                    evidence.Add(new
                    {
                        Case = "half-life",
                        HalfLife = halfLife,
                        FramesPerSecond = 144,
                        Steps = step,
                        Expected = expected.ToString(),
                        Actual = actual.ToString(),
                        VerifiedFootprintPixels = pixels.Length,
                        visuals.RetainedTrailBytes
                    });
                }
                yield return false;
            }
            Color faded = Read($"faded-{halfLife}");
            Match(faded, Color.Transparent, 0, "twelve half-lives leave no visible residue");
            MatchFootprint(Color.Transparent, 0, "fully faded image");
            evidence.Add(new { Case = "faded", HalfLife = halfLife, Steps = fadeSteps, Actual = faded.ToString() });

            Render(appearance, mapping, sequence++, 0, paint: true);
            Color beforePause = Read($"before-pause-{halfLife}");
            for (int paused = 0; paused < 10; paused++)
            {
                Render(appearance, mapping, sequence - 1, 0, paint: false);
                yield return false;
            }
            Match(Read($"paused-{halfLife}"), beforePause, 0, "unchanged paused publication");
            MatchFootprint(beforePause, 0, "paused footprint");
            Render(appearance, mapping with { OriginX = 1 }, sequence - 1, 0, paint: false);
            Match(Read($"camera-reset-{halfLife}"), Color.Transparent, 0, "camera change clears history");
            MatchFootprint(Color.Transparent, 0, "camera-reset image");
            evidence.Add(new { Case = "pause-and-camera", HalfLife = halfLife, PausedUnchanged = true, CameraCleared = true });
        }

        AppearanceSettings glowOnly = new() { Visuals = new() { GlowStrength = 0.2 } };
        Render(glowOnly, mapping, sequence++, 0, paint: true);
        Require(visuals.RetainedTrailBytes == 0, "Glow-only presentation retained disabled trail history.");
        GraphicsDevice.SetRenderTarget(scene);
        Require(!visuals.Begin(scene, new AppearanceSettings()), "Disabled effects unexpectedly redirected drawing.");
        Require(visuals.RetainedTrailBytes == 0, "Disabled effects retained trail history.");
        evidence.Add(new { Case = "history-release", GlowOnlyAndDisabledBytes = visuals.RetainedTrailBytes });
        GraphicsDevice.SetRenderTarget(null);

        void Render(AppearanceSettings appearance, ParticleDisplayTransform transform, long completedSequence, double seconds, bool paint)
        {
            GraphicsDevice.SetRenderTarget(scene);
            GraphicsDevice.Clear(Color.Transparent);
            Require(visuals.Begin(scene, appearance), visuals.TrailUnavailableReason ?? "The optional layer was not created.");
            if (paint)
            {
                sprites.Begin(blendState: BlendState.Opaque, samplerState: SamplerState.PointClamp);
                sprites.Draw(white, seedBounds, seed);
                sprites.End();
            }
            visuals.Complete(scene, scene.Bounds, completedSequence, transform, appearance, seconds);
        }

        Color Read(string name)
        {
            GraphicsDevice.SetRenderTarget(null);
            scene.GetData(pixels);
            using FileStream file = File.Create(Path.Combine(outputDirectory, name + ".png"));
            scene.SaveAsPng(file, size, size);
            return pixels[seedBounds.Center.Y * size + seedBounds.Center.X];
        }

        void MatchFootprint(Color expectedInside, int tolerance, string label)
        {
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Color expected = seedBounds.Contains(x, y) ? expectedInside : Color.Transparent;
                    Match(pixels[y * size + x], expected, tolerance, $"{label} at ({x}, {y})");
                }
            }
        }
    }

    private static void Match(Color actual, Color expected, int tolerance, string label)
    {
        Require(Math.Abs(actual.R - expected.R) <= tolerance && Math.Abs(actual.G - expected.G) <= tolerance
            && Math.Abs(actual.B - expected.B) <= tolerance && Math.Abs(actual.A - expected.A) <= tolerance,
            $"{label}: expected {expected}, observed {actual}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
