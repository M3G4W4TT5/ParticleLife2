using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.State;
using ParticleLife.Viewer;

namespace ParticleLife.MonoGame.Rendering;

/// <summary>Checks CPU particle pixels and native state restoration in the actual DesktopGL render-target pipeline.</summary>
/// <remarks>This explicit diagnostic needs drawing capability only, never GPU compute. It borrows one
/// completed CPU publication, draws the default seven colours at the default radius between SpriteBatch
/// operations, and reads only its small test framebuffer. Replacing its paused CPU publisher with
/// another same-count, same-clock source must refresh both point and quad geometry exactly once.
/// Non-core contexts are exercised with
/// POINT_SPRITE initially disabled and enabled; explicit core contexts never touch that removed capability.
/// This framebuffer regression does not replace interactive editor acceptance.</remarks>
internal sealed class CpuRenderingChecksGame : Game
{
    private readonly string outputDirectory;
    private readonly List<object> evidence = [];
    private bool finished;
    internal int ExitCode { get; private set; } = 1;

    internal CpuRenderingChecksGame(string outputDirectory)
    {
        this.outputDirectory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(this.outputDirectory) && Directory.EnumerateFileSystemEntries(this.outputDirectory).Any())
        {
            throw new IOException("CPU rendering checks require a fresh or empty output directory.");
        }
        Directory.CreateDirectory(this.outputDirectory);
        _ = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 320,
            PreferredBackBufferHeight = 240,
            GraphicsProfile = GraphicsProfile.Reach,
            SynchronizeWithVerticalRetrace = false
        };
        IsFixedTimeStep = false;
        Window.Title = "Particle Life 2 - CPU rendering checks";
    }

    protected override void Draw(GameTime gameTime)
    {
        if (finished) { return; }
        finished = true;
        Exception? failure = null;
        try
        {
            CheckPixels();
            ExitCode = 0;
            Console.WriteLine("CPU_RENDERING_PASS");
        }
        catch (Exception exception)
        {
            failure = exception;
            Console.Error.WriteLine(exception);
        }
        finally
        {
            File.WriteAllText(Path.Combine(outputDirectory, "cpu-rendering-checks.json"),
                JsonSerializer.Serialize(new { Passed = ExitCode == 0, Error = failure?.ToString(), Evidence = evidence },
                    new JsonSerializerOptions { WriteIndented = true }));
            Exit();
        }
        base.Draw(gameTime);
    }

    private void CheckPixels()
    {
        Vector2d[] positions = new Vector2d[7];
        for (int i = 0; i < positions.Length; i++) { positions[i] = new((i + 1) / 8.0, i % 2 == 0 ? 0.25 : 0.75); }
        ParticleSnapshot state = new([1, 2, 3, 4, 5, 6, 7], positions, new Vector2d[7], [0, 1, 2, 3, 4, 5, 6]);
        ExperimentDefinition experiment = new("CPU render regression", new(), new(7, new double[49]), state,
            1.0 / 144, new("manual", "1", null, "CPU framebuffer regression"));
        PlaybackController playback = new(experiment);
        playback.SetPaused(true);
        using BackgroundExperiment owner = new(new ExperimentEditor(playback));
        using RenderFrameLease frame = owner.AcquireRenderFrame()
            ?? throw new InvalidOperationException("The initial completed CPU publication is missing.");
        using Texture2D white = new(GraphicsDevice, 1, 1);
        white.SetData(new[] { Color.White });
        using SpriteBatch sprites = new(GraphicsDevice);
        using RenderTarget2D target = new(GraphicsDevice, 320, 240, false, SurfaceFormat.Color, DepthFormat.None);
        using ParticleRenderer renderer = new(GraphicsDevice, white);
        using OpenGlDrawApi gl = new();
        using GlDrawStateScope originalState = new(gl);
        Rectangle clip = new(32, 24, 224, 192);
        WorldTransform transform = new(1, 1, clip.X, clip.Y, clip.Width, clip.Height);
        AppearanceSettings appearance = new();
        Color background = new(12, 20, 24);
        Color[] pixels = new Color[target.Width * target.Height];
        List<string> pixelFailures = [];
        evidence.Add(new
        {
            Case = "context",
            renderer.Description,
            ContextProfileMask = gl.Integer(0x9126),
            gl.RequiresPointSpriteEnable,
            InitialPointSpriteEnabled = gl.RequiresPointSpriteEnable ? gl.IsEnabled(OpenGlDrawApi.PointSprite) != 0 : (bool?)null
        });

        try
        {
            for (int pass = 0; pass < 2; pass++)
            {
                bool initiallyEnabled = pass != 0;
                GraphicsDevice.SetRenderTarget(target);
                GraphicsDevice.Clear(background);
                DrawMarker(new(4, 4, 8, 8), Color.White);
                if (gl.RequiresPointSpriteEnable)
                {
                    if (initiallyEnabled) { gl.Enable(OpenGlDrawApi.PointSprite); }
                    else { gl.Disable(OpenGlDrawApi.PointSprite); }
                }
                GlRectangle viewport = gl.Rectangle(OpenGlDrawApi.ViewportName);
                GlRectangle scissor = gl.Rectangle(OpenGlDrawApi.ScissorBox);
                renderer.Draw(frame, transform, clip, appearance);
                ParticleRenderMetrics metrics = renderer.LastMetrics;
                Require(metrics.Backend == "OpenGL bulk points" && metrics.SubmittedParticles == 7,
                    "The regression did not exercise all seven particles through native CPU bulk points: " + metrics.FallbackReason);
                Require(metrics.UploadedBytes == (pass == 0 ? 7 * (20 + sizeof(uint)) : 0),
                    "An unchanged completed publication was uploaded again or the initial payload was incomplete.");
                Require(gl.Rectangle(OpenGlDrawApi.ViewportName) == viewport && gl.Rectangle(OpenGlDrawApi.ScissorBox) == scissor,
                    "The native particle draw changed the caller's viewport or scissor rectangle.");
                if (gl.RequiresPointSpriteEnable)
                {
                    Require((gl.IsEnabled(OpenGlDrawApi.PointSprite) != 0) == initiallyEnabled,
                        "The CPU renderer did not restore the caller's POINT_SPRITE enable.");
                }
                gl.Check("checking CPU point state restoration");
                DrawMarker(new(300, 220, 8, 8), Color.Magenta);
                GraphicsDevice.SetRenderTarget(null);
                target.GetData(pixels);
                using (FileStream file = File.Create(Path.Combine(outputDirectory, $"cpu-points-{pass}.png")))
                {
                    target.SaveAsPng(file, target.Width, target.Height);
                }
                evidence.Add(new
                {
                    Case = "framebuffer-pixels",
                    Pass = pass,
                    WhiteMarker = pixels[7 * target.Width + 7].ToString(),
                    MagentaMarker = pixels[223 * target.Width + 303].ToString(),
                    ChangedPixels = pixels.Count(pixel => pixel != background),
                    FirstCentre = pixels[72 * target.Width + 72].ToString()
                });
                int previousFailures = pixelFailures.Count;
                for (int i = 0; i < positions.Length; i++)
                {
                    Vector2d centre = transform.ToScreen(positions[i]);
                    uint rgb = appearance.ColorForSpecies(i);
                    try
                    {
                        Match(pixels[(int)centre.Y * target.Width + (int)centre.X],
                            new Color((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb), "particle " + i);
                    }
                    catch (InvalidOperationException failure) { pixelFailures.Add(failure.Message); }
                }
                Match(pixels[7 * target.Width + 7], Color.White, "preceding SpriteBatch marker");
                Match(pixels[223 * target.Width + 303], Color.Magenta, "following SpriteBatch marker");
                Match(pixels[120 * target.Width + 280], background, "unoccupied background");
                evidence.Add(new
                {
                    Case = "default-cpu-pixels",
                    Pass = pass,
                    InitiallyEnabled = initiallyEnabled,
                    CheckedCentres = positions.Length,
                    MatchedCentres = positions.Length - (pixelFailures.Count - previousFailures),
                    frame.Step,
                    frame.Time,
                    Metrics = metrics
                });
            }
            if (pixelFailures.Count != 0)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, pixelFailures));
            }

            Vector2d[] replacementPositions = positions.Select(position => new Vector2d(position.X, 0.5)).ToArray();
            ParticleSnapshot replacementState = new(state.Ids, replacementPositions, state.Velocities, state.Species);
            ExperimentDefinition replacement = new("CPU publisher replacement", experiment.Parameters, experiment.Matrix,
                replacementState, experiment.TimeStep, experiment.Generator);
            PlaybackController replacementPlayback = new(replacement);
            replacementPlayback.SetPaused(true);
            using BackgroundExperiment replacementOwner = new(new ExperimentEditor(replacementPlayback));
            using RenderFrameLease replacementFrame = replacementOwner.AcquireRenderFrame()
                ?? throw new InvalidOperationException("The replacement CPU publication is missing.");
            using QuadParticleRenderer quads = new(GraphicsDevice, white);
            for (int backend = 0; backend < 2; backend++)
            {
                for (int pass = 0; pass < 3; pass++)
                {
                    RenderFrameLease current = pass == 0 ? frame : replacementFrame;
                    Vector2d[] expectedPositions = pass == 0 ? positions : replacementPositions;
                    GraphicsDevice.SetRenderTarget(target);
                    GraphicsDevice.Clear(background);
                    ParticleRenderMetrics metrics;
                    if (backend == 0)
                    {
                        renderer.Draw(current, transform, clip, appearance);
                        metrics = renderer.LastMetrics;
                    }
                    else { metrics = quads.Draw(current, transform, clip, appearance, 2.5f, "Explicit fallback regression"); }
                    GraphicsDevice.SetRenderTarget(null);
                    target.GetData(pixels);
                    using (FileStream file = File.Create(Path.Combine(outputDirectory, $"cpu-publisher-{backend}-{pass}.png")))
                    {
                        target.SaveAsPng(file, target.Width, target.Height);
                    }
                    bool uploadExpected = pass == 1 || backend == 1 && pass == 0;
                    Require((metrics.UploadedBytes > 0) == uploadExpected,
                        "A replacement CPU publisher retained stale geometry, or an unchanged publication was uploaded again.");
                    for (int i = 0; i < expectedPositions.Length; i++)
                    {
                        Vector2d centre = transform.ToScreen(expectedPositions[i]);
                        uint rgb = appearance.ColorForSpecies(i);
                        Match(pixels[(int)centre.Y * target.Width + (int)centre.X],
                            new Color((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb), "replacement particle " + i);
                        if (pass != 0)
                        {
                            Vector2d oldCentre = transform.ToScreen(positions[i]);
                            Match(pixels[(int)oldCentre.Y * target.Width + (int)oldCentre.X], background, "old publisher particle " + i);
                        }
                    }
                    evidence.Add(new
                    {
                        Case = "paused-cpu-publisher-replacement",
                        Pass = pass,
                        current.Sequence,
                        current.Count,
                        current.Step,
                        current.Time,
                        CheckedCentres = expectedPositions.Length,
                        Metrics = metrics
                    });
                }
            }
        }
        finally { GraphicsDevice.SetRenderTarget(null); }

        void DrawMarker(Rectangle bounds, Color colour)
        {
            sprites.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp,
                DepthStencilState.None, RasterizerState.CullNone);
            sprites.Draw(white, bounds, colour);
            sprites.End();
        }
    }

    private static void Match(Color actual, Color expected, string label)
    {
        Require(Math.Abs(actual.R - expected.R) <= 2 && Math.Abs(actual.G - expected.G) <= 2
            && Math.Abs(actual.B - expected.B) <= 2 && Math.Abs(actual.A - expected.A) <= 2,
            $"{label}: expected {expected}, observed {actual}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
