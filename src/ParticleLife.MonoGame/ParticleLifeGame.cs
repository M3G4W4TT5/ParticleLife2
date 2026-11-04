using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using ParticleLife.Viewer;

namespace ParticleLife.MonoGame;

/// <summary>Owns input and reusable GPU resources; reads completed simulation state sequentially.</summary>
/// <remarks>
/// MonoGame runs a variable presentation loop. A monotonic wall clock feeds the separate fixed-step
/// controller, avoiding MonoGame's elapsed-time clamp and catch-up loop. Input commands are queued
/// by window events and applied only at the start of Update. Draw observes spans only for its own
/// duration; it never stores a view or captures a per-frame snapshot. All operations use one thread.
/// </remarks>
internal sealed class ParticleLifeGame : Game
{
    private static readonly Color Background = new(12, 18, 27);
    private static readonly Color Panel = new(21, 29, 41);
    private static readonly Color Ink = new(222, 232, 241);
    private static readonly Color Muted = new(146, 164, 184);
    private static readonly Color Accent = new(100, 217, 198);
    private static readonly (string Label, Keys Key)[] HelpActions =
    [
        ("SPACE  PAUSE / RESUME", Keys.Space),
        ("N / RIGHT  ONE STEP, THEN PAUSE", Keys.N),
        ("R  RESET EXACT INITIAL EXPERIMENT", Keys.R),
        ("I  RESAMPLE POSITIONS; KEEP MATRIX", Keys.I),
        ("M  RANDOM MATRIX; KEEP INITIAL STATE", Keys.M),
        ("1  RANDOM PRESET: NEW STATE + MATRIX", Keys.D1),
        ("2  CHAIN PRESET: NEW STATE + MATRIX", Keys.D2),
        ("3  SNAKE PRESET: NEW STATE + MATRIX", Keys.D3),
        ("S  SAVE INITIAL EXPERIMENT", Keys.S),
        ("F5  RELOAD STARTUP EXPERIMENT FILE", Keys.F5),
        ("D  TOGGLE SAMPLED DIAGNOSTICS", Keys.D),
        ("F9  PRINT TIMINGS / ESC EXIT", Keys.F9)
    ];
    private readonly GraphicsDeviceManager graphics;
    private readonly PlaybackController controller;
    private readonly ViewerOptions options;
    private readonly Queue<Keys> commands = new();
    private readonly HashSet<Keys> heldKeys = [];
    private SpriteBatch batch = null!;
    private Texture2D pixel = null!;
    private Texture2D particle = null!;
    private BitmapFont font = null!;
    private RasterizerState clipped = null!;
    private string message;
    private string? inputPath;
    private bool showHelp;
    private ButtonState previousMouseButton;
    private bool wasActive;
    private long previousTimestamp;
    private double summaryElapsed, updateMilliseconds, peakUpdateMilliseconds;
    private int summaryFrames, summaryUpdates;
    private double framesPerSecond, meanUpdateMilliseconds, lastPeakUpdateMilliseconds;

    internal ParticleLifeGame(ExperimentDefinition experiment, ViewerOptions options, bool paused, string message)
    {
        this.options = options;
        this.message = message;
        inputPath = options.InputPath;
        controller = new PlaybackController(experiment, diagnosticEverySteps: options.SampleEvery);
        controller.SetPaused(paused);
        graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1280,
            PreferredBackBufferHeight = 800,
            SynchronizeWithVerticalRetrace = true,
            GraphicsProfile = GraphicsProfile.Reach
        };
        IsFixedTimeStep = false;
        IsMouseVisible = true;
        Window.AllowUserResizing = true;
        Window.Title = "Particle Life 2";
        Window.KeyDown += OnKeyDown;
        Window.KeyUp += OnKeyUp;
    }

    protected override void LoadContent()
    {
        batch = new SpriteBatch(GraphicsDevice);
        pixel = new Texture2D(GraphicsDevice, 1, 1);
        pixel.SetData(new[] { Color.White });
        particle = CreateParticleTexture();
        font = new BitmapFont(GraphicsDevice);
        clipped = new RasterizerState { CullMode = CullMode.None, ScissorTestEnable = true };
        Console.WriteLine($"Viewer: {GraphicsDevice.Adapter.Description}; {Environment.Version}; {Environment.OSVersion}");
        Console.WriteLine($"Experiment: {controller.Experiment.Name}; {controller.Experiment.InitialState.Count} particles; " +
            $"dt={controller.Experiment.TimeStep:G17}; seed={controller.Experiment.Generator.Seed}; diagnostics every {controller.DiagnosticEverySteps} steps.");
        Console.WriteLine($"Search requested: {ExperimentFile.GetNeighbourSearchVersion(controller.Experiment.Backend)}; " +
            $"startup override: {options.BackendOverride?.ToString() ?? "none"}; integrator: {ExperimentFile.IntegratorVersion}; model: {ExperimentFile.ModelVersion}.");
        Console.WriteLine($"S saves initial experiments under {options.SaveDirectory}. H shows controls; F9 prints timings.");
        previousTimestamp = Stopwatch.GetTimestamp();
        wasActive = IsActive;
        base.LoadContent();
    }

    /// <summary>Generates one premultiplied-alpha disc; visual size has no physical meaning.</summary>
    private Texture2D CreateParticleTexture()
    {
        const int side = 32;
        Color[] pixels = new Color[side * side];
        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
            {
                float distance = MathF.Sqrt((x - 15.5f) * (x - 15.5f) + (y - 15.5f) * (y - 15.5f));
                pixels[y * side + x] = Color.White * Math.Clamp(15.5f - distance, 0, 1);
            }
        }

        Texture2D texture = new(GraphicsDevice, side, side);
        texture.SetData(pixels);
        return texture;
    }

    protected override void Update(GameTime gameTime)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = Stopwatch.GetElapsedTime(previousTimestamp, now).TotalSeconds;
        previousTimestamp = now;
        bool skipPlayback = false;
        if (!IsActive)
        {
            if (wasActive)
            {
                controller.SetPaused(true);
                heldKeys.Clear();
                commands.Clear();
                SetMessage("Focus lost - paused. SPACE resumes.");
            }

            wasActive = false;
            previousMouseButton = Mouse.GetState().LeftButton;
            base.Update(gameTime);
            return;
        }

        if (!wasActive)
        {
            skipPlayback = true;
            wasActive = true;
        }

        QueueMouseCommand();

        while (commands.TryDequeue(out Keys key))
        {
            try
            {
                skipPlayback |= HandleCommand(key);
            }
            catch (Exception error) when (ViewerErrors.IsExpected(error))
            {
                // Loading/editing validate before replacement. A failed step leaves completed state valid.
                controller.SetPaused(true);
                SetMessage($"Error: {error.Message} Current experiment retained; paused.");
                skipPlayback = true;
            }
        }

        long started = Stopwatch.GetTimestamp();
        try
        {
            if (!skipPlayback)
            {
                controller.AdvanceFrame(elapsed);
            }
        }
        catch (Exception error) when (ViewerErrors.IsExpected(error))
        {
            controller.SetPaused(true);
            SetMessage($"Step failed: {error.Message} Last completed state retained; paused.");
        }

        double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        updateMilliseconds += milliseconds;
        peakUpdateMilliseconds = Math.Max(peakUpdateMilliseconds, milliseconds);
        summaryUpdates++;
        summaryElapsed += elapsed;
        if (summaryElapsed >= 1)
        {
            framesPerSecond = summaryFrames / summaryElapsed;
            meanUpdateMilliseconds = updateMilliseconds / summaryUpdates;
            lastPeakUpdateMilliseconds = peakUpdateMilliseconds;
            summaryElapsed = updateMilliseconds = peakUpdateMilliseconds = 0;
            summaryFrames = summaryUpdates = 0;
        }

        base.Update(gameTime);
        if (skipPlayback)
        {
            // Blocking experiment edits must not count as playback after their cleared backlog.
            previousTimestamp = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>Applies one edge-triggered command between completed steps; true suppresses this frame's playback.</summary>
    private bool HandleCommand(Keys key)
    {
        switch (key)
        {
            case Keys.Escape:
                if (showHelp)
                {
                    showHelp = false;
                    return false;
                }

                Exit();
                return true;
            case Keys.Space:
                controller.SetPaused(!controller.IsPaused);
                return true;
            case Keys.N:
            case Keys.Right:
                controller.SingleStep();
                return true;
            case Keys.R:
                controller.Reset();
                SetMessage("Reset: exact initial values restored; time and step zero.");
                return true;
            case Keys.I:
                Replace(ExperimentEditing.ResampleState(controller.Experiment, NewSeed()), "Resampled positions; zero velocities. Matrix, species and model retained.");
                return true;
            case Keys.M:
                Replace(ExperimentEditing.RandomizeMatrix(controller.Experiment, NewSeed()), "New random matrix; exact initial state retained. Restarted at time zero.");
                return true;
            case Keys.D1:
            case Keys.D2:
            case Keys.D3:
                PresetKind kind = key == Keys.D1 ? PresetKind.Random : key == Keys.D2 ? PresetKind.Chains : PresetKind.Snakes;
                ExperimentDefinition current = controller.Experiment;
                Replace(PresetGenerator.Create(kind, current.InitialState.Count, current.Matrix.SpeciesCount,
                    NewSeed(), current.Parameters, current.TimeStep, current.Backend), $"Selected {kind}: new state and matrix, same model and counts.");
                return true;
            case Keys.S:
                string path = Path.Combine(options.SaveDirectory, $"experiment-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.json");
                ExperimentStorage.SaveNew(path, controller.Experiment);
                SetMessage($"Saved INITIAL experiment: {path}");
                return false;
            case Keys.F5:
                if (inputPath is null)
                {
                    SetMessage("No input path. Launch with an experiment.json argument to load and reload a saved experiment.");
                    return false;
                }

                LoadExperiment(inputPath);
                return true;
            case Keys.D:
                controller.DiagnosticEverySteps = controller.DiagnosticEverySteps == 0 ? Math.Max(1, options.SampleEvery == 0 ? 144 : options.SampleEvery) : 0;
                return false;
            case Keys.H:
            case Keys.F1: showHelp = !showHelp; return false;
            case Keys.F9: PrintTimings(); return false;
            default: return false;
        }
    }

    private static int NewSeed() => RandomNumberGenerator.GetInt32(int.MaxValue);

    private void Replace(ExperimentDefinition experiment, string notice)
    {
        controller.ReplaceExperiment(experiment);
        SetMessage($"{notice} Seed {experiment.Generator.Seed}.");
    }

    private void LoadExperiment(string path)
    {
        ExperimentDefinition experiment = options.ApplyBackend(ExperimentFile.Load(path));
        controller.ReplaceExperiment(experiment);
        inputPath = path;
        SetMessage($"Loaded {path}. Exact initial state; time and step zero.");
    }

    private void SetMessage(string text)
    {
        message = text;
        Console.WriteLine(text);
    }

    private void OnKeyDown(object? sender, InputKeyEventArgs e)
    {
        if (heldKeys.Add(e.Key))
        {
            commands.Enqueue(e.Key);
        }
    }

    private void OnKeyUp(object? sender, InputKeyEventArgs e) => heldKeys.Remove(e.Key);

    /// <summary>Routes clickable controls through the same completed-step command boundary as keyboard input.</summary>
    private void QueueMouseCommand()
    {
        MouseState mouse = Mouse.GetState();
        bool clicked = mouse.LeftButton == ButtonState.Pressed && previousMouseButton == ButtonState.Released;
        previousMouseButton = mouse.LeftButton;
        if (!clicked)
        {
            return;
        }

        int width = GraphicsDevice.Viewport.Width, height = GraphicsDevice.Viewport.Height;
        if (new Rectangle(width - 142, 12, 130, 32).Contains(mouse.Position))
        {
            commands.Enqueue(Keys.Space);
        }
        else if (new Rectangle(width - 238, 12, 88, 32).Contains(mouse.Position))
        {
            commands.Enqueue(Keys.H);
        }
        else if (showHelp)
        {
            (Rectangle bounds, int scale, int row) = HelpLayout(width, height);
            int index = (mouse.Y - bounds.Y - 42) / row;
            if (mouse.Y >= bounds.Y + 42 && bounds.Contains(mouse.Position) && index >= 0 && index < HelpActions.Length)
            {
                commands.Enqueue(HelpActions[index].Key);
            }
        }
    }

    private static (Rectangle Bounds, int Scale, int Row) HelpLayout(int width, int height)
    {
        int scale = width >= 700 && height >= 470 ? 2 : 1;
        int row = scale == 2 ? 28 : 16;
        int boxWidth = Math.Min(width - 32, scale == 2 ? 570 : 320);
        int boxHeight = 52 + HelpActions.Length * row;
        return (new Rectangle((width - boxWidth) / 2, Math.Max(52, (height - boxHeight) / 2), boxWidth, boxHeight), scale, row);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Background);
        int width = GraphicsDevice.Viewport.Width, height = GraphicsDevice.Viewport.Height;
        if (width < 420 || height < 340)
        {
            batch.Begin(samplerState: SamplerState.PointClamp);
            font.Draw(batch, "ENLARGE WINDOW TO 420 X 340", new Vector2(12, 12), Ink, 1);
            batch.End();
            return;
        }

        bool wide = width >= 1050;
        Rectangle canvas = wide ? new Rectangle(16, 64, width - 400, height - 136)
            : new Rectangle(16, 64, width - 32, Math.Max(80, height - 330));
        Rectangle panel = wide ? new Rectangle(width - 368, 64, 352, height - 136)
            : new Rectangle(16, canvas.Bottom + 16, width - 32, height - canvas.Bottom - 88);
        DrawWorld(canvas);
        batch.Begin(samplerState: SamplerState.PointClamp);
        font.Draw(batch, width >= 700 ? "PARTICLE LIFE 2" : "PL2", new Vector2(20, 20), Ink, width >= 700 ? 3 : 2);
        batch.Draw(pixel, new Rectangle(width - 238, 12, 88, 32), Panel);
        font.Draw(batch, "HELP", new Vector2(width - 226, 22), Accent);
        batch.Draw(pixel, new Rectangle(width - 142, 12, 130, 32), Panel);
        font.Draw(batch, controller.IsPaused ? "PAUSED" : "PLAYING", new Vector2(width - 136, 24), Accent);
        batch.Draw(pixel, panel, Panel);
        DrawPanel(panel, wide);
        DrawWrapped(message, new Rectangle(20, height - 60, width - 40, 48), Muted, 1);
        if (showHelp)
        {
            (Rectangle bounds, int scale, int row) = HelpLayout(width, height);
            batch.Draw(pixel, bounds, new Color(31, 46, 62));
            font.Draw(batch, "CONTROLS - CLICK A ROW / H CLOSES", new Vector2(bounds.X + 12, bounds.Y + 16), Accent, scale);
            for (int i = 0; i < HelpActions.Length; i++)
            {
                if (i % 2 == 0)
                {
                    batch.Draw(pixel, new Rectangle(bounds.X + 6, bounds.Y + 40 + row * i, bounds.Width - 12, row), Panel);
                }

                font.Draw(batch, HelpActions[i].Label, new Vector2(bounds.X + 12, bounds.Y + 44 + row * i), Ink, scale);
            }
        }
        batch.End();
        summaryFrames++;
        base.Draw(gameTime);
    }

    /// <summary>Maps lengths with one scale; clipping hides pixels only and never removes particles.</summary>
    private void DrawWorld(Rectangle canvas)
    {
        ExperimentDefinition experiment = controller.Experiment;
        WorldTransform transform = new(experiment.Parameters.WorldWidth, experiment.Parameters.WorldHeight,
            canvas.X + 20, canvas.Y + 20, Math.Max(1, canvas.Width - 40), Math.Max(1, canvas.Height - 40));
        Rectangle world = new((int)transform.OffsetX, (int)transform.OffsetY, (int)transform.Width, (int)transform.Height);
        GraphicsDevice.ScissorRectangle = canvas;
        batch.Begin(samplerState: SamplerState.LinearClamp, rasterizerState: clipped);
        batch.Draw(pixel, world, new Color(15, 23, 34));
        for (int i = 0; i <= 10; i++)
        {
            Color line = i is 0 or 10 ? new Color(67, 85, 105) : new Color(24, 34, 47);
            batch.Draw(pixel, new Rectangle(world.X + world.Width * i / 10, world.Y, 1, world.Height), line);
            batch.Draw(pixel, new Rectangle(world.X, world.Y + world.Height * i / 10, world.Width, 1), line);
        }

        // The ref-struct view and its spans never escape this method or survive a step/reset.
        ParticleStateView state = controller.Observe();
        for (int i = 0; i < state.Count; i++)
        {
            Vector2d mapped = transform.ToScreen(state.Positions[i]);
            const float radius = 3.5f;
            if (!mapped.IsFinite || mapped.X < canvas.Left - radius || mapped.X > canvas.Right + radius
                || mapped.Y < canvas.Top - radius || mapped.Y > canvas.Bottom + radius)
            {
                continue;
            }

            batch.Draw(particle, new Vector2((float)mapped.X, (float)mapped.Y), null,
                ParticlePalette.ForSpecies(state.Species[i]), 0, new Vector2(16, 16), radius / 16, SpriteEffects.None, 0);
        }

        batch.End();
    }

    private void DrawPanel(Rectangle panel, bool wide)
    {
        ParticleStateView state = controller.Observe();
        ExperimentDefinition experiment = controller.Experiment;
        int x = panel.X + 16, y = panel.Y + 16;
        int scale = wide ? 2 : 1;
        int line = wide ? 24 : 16;
        int chars = Math.Max(1, (panel.Width - 32) / (6 * scale));
        void Label(string text, Color color)
        {
            if (y + 7 * scale <= panel.Bottom - 8)
            {
                font.Draw(batch, text, new Vector2(x, y), color, scale, chars);
            }

            y += line;
        }

        Label(experiment.Name, Accent);
        Label($"{state.Count} PARTICLES / {experiment.Matrix.SpeciesCount} SPECIES", Ink);
        Label($"T {state.Time:F4}  STEP {state.Step}", Ink);
        Label($"DT {experiment.TimeStep:G6} (REDUCED)", Muted);
        string requested = experiment.Backend == ForceBackend.UniformGrid ? "GRID" : "DIRECT";
        string actual = controller.LastExecution is not ForceExecution execution ? "UNSTEPPED"
            : execution.FallbackReason != GridFallbackReason.None ? "DIRECT FALLBACK"
            : execution.ActualBackend == ForceBackend.UniformGrid ? "GRID" : "DIRECT";
        Label($"SEARCH {requested} / {actual}", Accent);
        Label($"{framesPerSecond:F0} FPS / UPDATE {meanUpdateMilliseconds:F2} MS", Muted);
        Label($"DROPPED {controller.DroppedPlaybackSeconds:F3} S", controller.DroppedPlaybackSeconds > 0 ? new Color(245, 203, 100) : Muted);
        if (wide)
        {
            Label($"SEED {experiment.Generator.Seed?.ToString() ?? "MANUAL"}", Muted);
            y += 8;
        }

        DiagnosticSample? sample = controller.LatestDiagnostic;
        if (sample is null)
        {
            Label(controller.DiagnosticError is not null ? "MEASUREMENT FAILED" : controller.DiagnosticEverySteps == 0 ? "DIAGNOSTICS OFF [D]" : $"SAMPLE EVERY {controller.DiagnosticEverySteps} STEPS", Muted);
            if (controller.DiagnosticError is not null)
            {
                Label(controller.DiagnosticError, Muted);
            }
        }
        else
        {
            Label($"SAMPLED T {sample.Time:F3} / #{sample.Step}", Accent);
            Label($"SPEED {sample.MeanSpeed:F4} MAX {sample.MaxSpeed:F4}", Muted);
            Label($"CLUSTERS {sample.ClusterCount} MAX {sample.LargestCluster}", Muted);
            if (wide)
            {
                Label($"OVERLAPS {sample.OverlapPairs} / R {sample.ClusterDistance:G4}", Muted);
            }
        }

        if (wide)
        {
            y += 16;
            Label("HELP [H / F1] OR CLICK", Ink);
            Label("SPACE PAUSE   N STEP", Muted);
            Label("R RESET      I RESAMPLE", Muted);
            Label("M NEW RANDOM MATRIX", Muted);
            Label("1 RANDOM  2 CHAIN  3 SNAKE", Muted);
            Label("S SAVE INITIAL EXPERIMENT", Muted);
            Label("F5 RELOAD STARTUP FILE", Muted);
            Label("D DIAGNOSTICS   ESC EXIT", Muted);
        }
        else
        {
            Label("H / F1 OR CLICK HELP FOR ALL CONTROLS", Muted);
        }
    }

    private void DrawWrapped(string text, Rectangle bounds, Color color, int scale)
    {
        int count = Math.Max(1, bounds.Width / (6 * scale));
        for (int offset = 0, y = bounds.Y; offset < text.Length && y + 7 * scale <= bounds.Bottom; offset += count, y += 10 * scale)
        {
            font.Draw(batch, text.Substring(offset, Math.Min(count, text.Length - offset)), new Vector2(bounds.X, y), color, scale);
        }
    }

    private void PrintTimings() => Console.WriteLine($"Viewer sample (~1 wall second): {framesPerSecond:F1} FPS; " +
        $"controller update mean {meanUpdateMilliseconds:F3} ms, max {lastPeakUpdateMilliseconds:F3} ms (includes scheduled diagnostics); " +
        $"step {controller.Observe().Step}; dropped playback {controller.DroppedPlaybackSeconds:F6} s. CPU update excludes rendering/present. " +
        $"Search requested {controller.Experiment.Backend}; actual {controller.LastExecution?.ActualBackend.ToString() ?? "unstepped"}; " +
        $"fallback {controller.LastExecution?.FallbackReason.ToString() ?? "none"}; direct fallback steps {controller.DirectFallbackSteps}; " +
        $"coordinate range {controller.DirectFallbackSteps - controller.AccumulationFallbackSteps}; accumulation range {controller.AccumulationFallbackSteps}; " +
        $"candidates {controller.LastStatistics.CandidatePairs}; accepted {controller.LastStatistics.AcceptedPairs}.");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Window.KeyDown -= OnKeyDown;
            Window.KeyUp -= OnKeyUp;
            font?.Dispose();
            particle?.Dispose();
            pixel?.Dispose();
            clipped?.Dispose();
            batch?.Dispose();
        }

        base.Dispose(disposing);
    }
}
