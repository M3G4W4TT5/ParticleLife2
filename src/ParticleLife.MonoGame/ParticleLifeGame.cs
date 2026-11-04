using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using ParticleLife.MonoGame.Editor;
using ParticleLife.Viewer;

namespace ParticleLife.MonoGame;

/// <summary>Owns native input/rendering and reads completed simulation state sequentially.</summary>
/// <remarks>The separate editor owns controls; the graphics-free controller owns playback. Window
/// events only enqueue input. Update applies it between completed steps. Draw retains no observation
/// beyond its call and never copies state. A single world scale preserves geometry on resize.</remarks>
internal sealed class ParticleLifeGame : Game
{
    private readonly GraphicsDeviceManager graphics;
    private readonly PlaybackController controller;
    private readonly ExperimentEditor editor;
    private readonly LiveExperimentEditor surface;
    private readonly ViewerOptions options;
    private readonly List<UiInputEvent> input = [];
    private readonly HashSet<Keys> heldKeys = [];
    private readonly Keys[] polledKeys = new Keys[256];
    private KeyboardState previousKeyboard;
    private SpriteBatch batch = null!;
    private Texture2D pixel = null!;
    private Texture2D particle = null!;
    private BitmapFont font = null!;
    private UiCanvas canvas = null!;
    private RasterizerState clipped = null!;
    private string message;
    private bool wasActive;
    private long previousTimestamp;
    private double summaryElapsed;
    private double controllerTotal, controllerPeak, editorTotal, renderTotal;
    private int summaryFrames, summaryUpdates;
    private int observedEvents, summaryEdits, lastSummaryEdits;
    private double framesPerSecond, meanController, peakController, meanEditor, meanRender;
    private EditedTimingSample? lastEditedSample;
    private ButtonState previousMouseButton;
    private float uiScale = 1;
    private Rectangle worldBounds, graphBounds, editorBounds, modalBounds;

    internal ParticleLifeGame(ExperimentDefinition experiment, ViewerOptions options, bool paused, string message)
    {
        this.options = options;
        this.message = message;
        controller = new PlaybackController(experiment, diagnosticEverySteps: options.SampleEvery);
        controller.SetPaused(paused);
        editor = new ExperimentEditor(controller);
        surface = new LiveExperimentEditor(editor, options.SaveDirectory, SetMessage, PrintTimings);
        graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1360,
            PreferredBackBufferHeight = 900,
            SynchronizeWithVerticalRetrace = true,
            GraphicsProfile = GraphicsProfile.Reach
        };
        IsFixedTimeStep = false;
        IsMouseVisible = true;
        Window.AllowUserResizing = true;
        Window.Title = "Particle Life 2 - Live Experiment Editor";
        Window.KeyDown += OnKeyDown;
        Window.KeyUp += OnKeyUp;
        Window.TextInput += OnTextInput;
    }

    protected override void LoadContent()
    {
        batch = new SpriteBatch(GraphicsDevice);
        pixel = new Texture2D(GraphicsDevice, 1, 1);
        pixel.SetData(new[] { Color.White });
        particle = CreateParticleTexture();
        font = new BitmapFont(GraphicsDevice);
        canvas = new UiCanvas(batch, pixel, font);
        clipped = new RasterizerState { CullMode = CullMode.None, ScissorTestEnable = true };
        Console.WriteLine($"Viewer: {GraphicsDevice.Adapter.Description}; {Environment.Version}; {Environment.OSVersion}");
        Console.WriteLine($"Experiment: {controller.Experiment.Name}; {controller.Observe().Count} particles; dt={editor.Configuration.TimeStep:G17}; " +
            $"seed={editor.Generation.Seed}; diagnostics every {controller.DiagnosticEverySteps} steps.");
        Console.WriteLine($"Search requested: {editor.Configuration.Backend}; workers: {editor.Configuration.WorkerCount}; " +
            $"startup override: {options.BackendOverride?.ToString() ?? "none"}; integrator: {ExperimentFile.IntegratorVersion}; model: {ExperimentFile.ModelVersion}.");
        Console.WriteLine($"FILES / S opens named save/load workflows under {options.SaveDirectory}. E toggles editor; F9 prints timings.");
        previousTimestamp = Stopwatch.GetTimestamp();
        wasActive = IsActive;
        base.LoadContent();
    }

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
        MouseState mouse = Mouse.GetState();
        if (!IsActive)
        {
            if (wasActive)
            {
                surface.LoseFocus();
                controller.SetPaused(true);
                heldKeys.Clear();
                SetMessage("Focus lost - paused. Resume explicitly when ready.");
            }
            input.Clear();
            wasActive = false;
            previousMouseButton = mouse.LeftButton;
            base.Update(gameTime);
            return;
        }
        bool skipPlayback = !wasActive;
        wasActive = true;
        PollKeyboard();
        if (input.Any(entry => !entry.IsText && entry.Key == Keys.F9))
        {
            // Measurement logging changes no simulation/UI value and stays available during a drag.
            PrintTimings();
        }
        Layout();
        bool capturedBefore = surface.CapturesKeyboard;
        bool clicked = mouse.LeftButton == ButtonState.Pressed && previousMouseButton == ButtonState.Released;
        previousMouseButton = mouse.LeftButton;
        bool headerClick = false;
        if (clicked && !surface.IsModal)
        {
            int width = GraphicsDevice.Viewport.Width;
            if (new Rectangle(width - 136, 14, 124, 30).Contains(mouse.Position))
            {
                controller.SetPaused(!controller.IsPaused);
                SetMessage(controller.IsPaused ? "Paused. Edit any rule or use Step to advance once." : "Playing. Live rule edits preserve the current particles and clock.");
                skipPlayback = headerClick = true;
            }
            else if (new Rectangle(width - 268, 14, 124, 30).Contains(mouse.Position))
            {
                surface.ToggleVisibility();
                headerClick = true;
            }
            else if (width >= 760 && new Rectangle(width - 400, 14, 124, 30).Contains(mouse.Position))
            {
                PrintTimings();
                headerClick = true;
            }
        }
        long editorStarted = Stopwatch.GetTimestamp();
        try
        {
            surface.Update(editorBounds, modalBounds, uiScale, mouse, input);
            skipPlayback |= surface.SuppressPlayback;
            if (!capturedBefore && !surface.CapturesKeyboard && !surface.ConsumedKeyboard && !headerClick)
            {
                foreach (UiInputEvent entry in input)
                {
                    if (!entry.IsText)
                    {
                        skipPlayback |= HandleShortcut(entry.Key, entry.Control);
                        if (surface.CapturesKeyboard)
                        {
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception exception) when (ViewerErrors.IsExpected(exception))
        {
            controller.SetPaused(true);
            SetMessage($"Editor error: {exception.Message}. Current completed state retained; paused.");
            skipPlayback = true;
        }
        editorTotal += Stopwatch.GetElapsedTime(editorStarted).TotalMilliseconds;
        int currentEvents = editor.SessionEventCount;
        summaryEdits += Math.Max(0, currentEvents - observedEvents);
        observedEvents = currentEvents;
        input.Clear();
        long started = Stopwatch.GetTimestamp();
        try
        {
            if (!skipPlayback && !editor.IsReplaying)
            {
                controller.AdvanceFrame(elapsed);
            }
        }
        catch (Exception exception) when (ViewerErrors.IsExpected(exception))
        {
            controller.SetPaused(true);
            SetMessage($"Step failed: {exception.Message}. Last completed state retained; paused.");
        }
        double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        controllerTotal += milliseconds;
        controllerPeak = Math.Max(controllerPeak, milliseconds);
        summaryUpdates++;
        summaryElapsed += elapsed;
        if (summaryElapsed >= 1)
        {
            framesPerSecond = summaryFrames / summaryElapsed;
            meanController = controllerTotal / summaryUpdates;
            peakController = controllerPeak;
            meanEditor = editorTotal / summaryUpdates;
            meanRender = renderTotal / Math.Max(1, summaryFrames);
            lastSummaryEdits = summaryEdits;
            if (summaryEdits > 0)
            {
                ParticleStateView state = controller.Observe();
                SimulationConfiguration configuration = editor.Configuration;
                lastEditedSample = new EditedTimingSample(DateTimeOffset.UtcNow, framesPerSecond, meanController,
                    peakController, meanEditor, meanRender, summaryEdits, state.Step, state.Time, state.Count,
                    configuration.Matrix.SpeciesCount, configuration.TimeStep, configuration.Parameters.Cutoff,
                    configuration.Backend, configuration.WorkerCount, controller.LastExecution?.ActualBackend,
                    controller.LastExecution?.ActualWorkerCount, controller.DroppedPlaybackSeconds, editor.Appearance.ShowEditor);
            }
            summaryElapsed = controllerTotal = controllerPeak = editorTotal = renderTotal = 0;
            summaryFrames = summaryUpdates = summaryEdits = 0;
        }
        if (skipPlayback)
        {
            previousTimestamp = Stopwatch.GetTimestamp();
        }
        base.Update(gameTime);
    }

    private bool HandleShortcut(Keys key, bool control)
    {
        if (control && key == Keys.Z)
        {
            editor.Undo();
            return false;
        }
        if (control && key == Keys.Y)
        {
            editor.Redo();
            return false;
        }
        switch (key)
        {
            case Keys.Escape: Exit(); return true;
            case Keys.Space:
                controller.SetPaused(!controller.IsPaused);
                SetMessage(controller.IsPaused ? "Paused. Edit any rule or use Step to advance once." : "Playing. Live rule edits preserve the current particles and clock.");
                return true;
            case Keys.N:
            case Keys.Right: controller.SingleStep(); return true;
            case Keys.R: surface.Reset(); return true;
            case Keys.I: editor.ResampleParticles(); SetMessage("Resampled live particles using the generation recipe; rules and clock retained."); return true;
            case Keys.M: surface.RandomizeMatrix(); return false;
            case Keys.D1: surface.Template(PresetKind.Random); return false;
            case Keys.D2: surface.Template(PresetKind.Chains); return false;
            case Keys.D3: surface.Template(PresetKind.Snakes); return false;
            case Keys.S:
            case Keys.O: surface.OpenFiles(); return true;
            case Keys.E: surface.ToggleVisibility(); return false;
            case Keys.F5:
                if (options.InputPath is null)
                {
                    surface.OpenFiles();
                }
                else
                {
                    surface.ReloadExperiment(options.InputPath, options.BackendOverride, options.WorkerOverride);
                }
                return true;
            case Keys.D: controller.DiagnosticEverySteps = controller.DiagnosticEverySteps == 0 ? Math.Max(1, options.SampleEvery) : 0; return false;
            case Keys.H:
            case Keys.F1: editor.SetAppearance(editor.Appearance with { ShowHelp = !editor.Appearance.ShowHelp }); return false;
            case Keys.F9: return false;
            default: return false;
        }
    }

    /// <summary>Fits a wide side panel or a compact stacked editor without altering physical dimensions.</summary>
    private void Layout()
    {
        int width = GraphicsDevice.Viewport.Width;
        int height = GraphicsDevice.Viewport.Height;
        bool wide = width >= 960;
        uiScale = (float)Math.Min(editor.Appearance.UiScale, wide ? Math.Max(0.5, width * 0.52 / 440) : Math.Max(0.5, (width - 24) / 440.0));
        graphBounds = Rectangle.Empty;
        if (!editor.Appearance.ShowEditor)
        {
            worldBounds = new Rectangle(12, 62, Math.Max(1, width - 24), Math.Max(1, height - 124));
        }
        else if (wide)
        {
            int panelWidth = Math.Min((int)(500 * uiScale), (int)(width * 0.52));
            Rectangle screenPanel = new(width - panelWidth - 12, 62, panelWidth, Math.Max(160, height - 124));
            editorBounds = Logical(screenPanel);
            worldBounds = new Rectangle(12, 62, Math.Max(1, width - panelWidth - 36), Math.Max(1, height - 124));
            if (height >= 650)
            {
                int graphHeight = Math.Min(210, height / 4);
                graphBounds = new Rectangle(worldBounds.X, worldBounds.Bottom - graphHeight, worldBounds.Width, graphHeight);
                worldBounds.Height -= graphHeight + 12;
            }
        }
        else
        {
            int available = Math.Max(120, height - 124);
            int worldHeight = Math.Max(60, (int)(available * (height < 600 ? 0.30 : 0.38)));
            worldBounds = new Rectangle(12, 62, Math.Max(1, width - 24), worldHeight);
            editorBounds = Logical(new Rectangle(12, worldBounds.Bottom + 10, Math.Max(1, width - 24), Math.Max(40, available - worldHeight - 10)));
        }
        int modalWidth = Math.Min(width - 24, (int)(900 * uiScale));
        modalBounds = Logical(new Rectangle((width - modalWidth) / 2, 24, modalWidth, Math.Max(180, height - 48)));
    }

    private Rectangle Logical(Rectangle screen) => new((int)(screen.X / uiScale), (int)(screen.Y / uiScale),
        Math.Max(1, (int)(screen.Width / uiScale)), Math.Max(1, (int)(screen.Height / uiScale)));

    private Rectangle Physical(Rectangle logical) => Rectangle.Intersect(new Rectangle((int)(logical.X * uiScale),
        (int)(logical.Y * uiScale), Math.Max(1, (int)(logical.Width * uiScale)), Math.Max(1, (int)(logical.Height * uiScale))), GraphicsDevice.Viewport.Bounds);

    protected override void Draw(GameTime gameTime)
    {
        long started = Stopwatch.GetTimestamp();
        GraphicsDevice.Clear(new Color(10, 15, 23));
        int width = GraphicsDevice.Viewport.Width;
        int height = GraphicsDevice.Viewport.Height;
        if (width < 420 || height < 340)
        {
            batch.Begin(samplerState: SamplerState.PointClamp);
            font.Draw(batch, "ENLARGE WINDOW TO 420 X 340", new Vector2(12, 12), UiCanvas.Ink, 1);
            batch.End();
            return;
        }
        Layout();
        DrawWorld();
        batch.Begin(samplerState: SamplerState.PointClamp);
        canvas.Text(width > 620 ? "PARTICLE LIFE 2" : "PL2", 18, 22, UiCanvas.Ink, size: width > 620 ? 2 : 1.5f);
        if (width >= 760)
        {
            canvas.Fill(new Rectangle(width - 400, 14, 124, 30), UiCanvas.Surface);
            canvas.Text("TIMINGS", width - 381, 24, UiCanvas.Muted, size: 1.4f);
        }
        canvas.Fill(new Rectangle(width - 268, 14, 124, 30), UiCanvas.Surface);
        canvas.Text("EDITOR [E]", width - 259, 24, UiCanvas.Accent, size: 1.4f);
        canvas.Fill(new Rectangle(width - 136, 14, 124, 30), UiCanvas.Surface);
        canvas.Text(controller.IsPaused ? "PAUSED" : "PLAYING", width - 123, 24, UiCanvas.Accent);
        if (graphBounds != Rectangle.Empty)
        {
            surface.Graph.Draw(canvas, graphBounds);
        }
        canvas.Wrapped(message, new Rectangle(18, height - 49, width - 36, 43), UiCanvas.Muted);
        batch.End();
        if (editor.Appearance.ShowEditor)
        {
            DrawPanel(surface.PanelBounds, surface.PanelBody, surface.DrawChrome, surface.DrawBody);
        }
        if (surface.IsModal)
        {
            batch.Begin();
            canvas.Fill(GraphicsDevice.Viewport.Bounds, Color.Black * 0.65f);
            batch.End();
            DrawPanel(surface.BrowserBounds, surface.BrowserBody, surface.DrawBrowserChrome, surface.DrawBrowserBody);
        }
        summaryFrames++;
        renderTotal += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        base.Draw(gameTime);
    }

    private void DrawPanel(Rectangle panelBounds, Rectangle body, Action<UiCanvas> chrome, Action<UiCanvas> content)
    {
        GraphicsDevice.ScissorRectangle = Physical(panelBounds);
        batch.Begin(samplerState: SamplerState.PointClamp, rasterizerState: clipped, transformMatrix: Matrix.CreateScale(uiScale));
        chrome(canvas);
        batch.End();
        GraphicsDevice.ScissorRectangle = Physical(body);
        batch.Begin(samplerState: SamplerState.PointClamp, rasterizerState: clipped, transformMatrix: Matrix.CreateScale(uiScale));
        content(canvas);
        batch.End();
    }

    private void DrawWorld()
    {
        SimulationConfiguration configuration = editor.Configuration;
        WorldTransform transform = new(configuration.Parameters.WorldWidth, configuration.Parameters.WorldHeight,
            worldBounds.X + 16, worldBounds.Y + 16, Math.Max(1, worldBounds.Width - 32), Math.Max(1, worldBounds.Height - 32));
        Rectangle world = new((int)transform.OffsetX, (int)transform.OffsetY, Math.Max(1, (int)transform.Width), Math.Max(1, (int)transform.Height));
        GraphicsDevice.ScissorRectangle = Rectangle.Intersect(worldBounds, GraphicsDevice.Viewport.Bounds);
        batch.Begin(samplerState: SamplerState.LinearClamp, rasterizerState: clipped);
        canvas.Fill(world, new Color(14, 21, 31));
        for (int index = 0; index <= 10; index++)
        {
            Color line = index is 0 or 10 ? new Color(55, 72, 88) : new Color(22, 31, 42);
            canvas.Fill(new Rectangle(world.X + world.Width * index / 10, world.Y, 1, world.Height), line);
            canvas.Fill(new Rectangle(world.X, world.Y + world.Height * index / 10, world.Width, 1), line);
        }
        ParticleStateView state = controller.Observe();
        float radius = (float)Math.Min(editor.Appearance.ParticleRadius, Math.Max(worldBounds.Width, worldBounds.Height));
        for (int index = 0; index < state.Count; index++)
        {
            Vector2d mapped = transform.ToScreen(state.Positions[index]);
            if (!mapped.IsFinite || mapped.X < worldBounds.Left - radius || mapped.X > worldBounds.Right + radius
                || mapped.Y < worldBounds.Top - radius || mapped.Y > worldBounds.Bottom + radius)
            {
                continue;
            }
            uint rgb = editor.Appearance.ColorForSpecies(state.Species[index]);
            Color colour = new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            batch.Draw(particle, new Vector2((float)mapped.X, (float)mapped.Y), null, colour, 0,
                new Vector2(16, 16), radius / 16, SpriteEffects.None, 0);
        }
        batch.End();
        batch.Begin(samplerState: SamplerState.PointClamp);
        if (editor.Appearance.ShowOverlay && worldBounds.Height >= 160)
        {
            string actual = controller.LastExecution?.ActualBackend.ToString() ?? "UNSTEPPED";
            string text = $"T {state.Time:F4}  STEP {state.Step}  N {state.Count}\n" +
                $"DT {configuration.TimeStep:G5}  SPEED {controller.PlaybackSpeed:G3}X  EVENTS {editor.SessionEventCount}\n" +
                $"{configuration.Backend}/{configuration.WorkerCount} -> {actual}/{controller.LastExecution?.ActualWorkerCount.ToString() ?? "-"}\n" +
                $"{framesPerSecond:F0} FPS  UPDATE {meanController:F2} MS  UI {meanEditor:F2} MS  DROP {controller.DroppedPlaybackSeconds:F2}S";
            canvas.Fill(new Rectangle(worldBounds.X + 8, worldBounds.Y + 6, Math.Min(worldBounds.Width - 16, 610), 67), UiCanvas.Background * 0.88f);
            font.Draw(batch, text, new Vector2(worldBounds.X + 14, worldBounds.Y + 12), UiCanvas.Muted, 1.25f);
        }
        else if (editor.Appearance.ShowOverlay)
        {
            canvas.Text($"T {state.Time:F3}  STEP {state.Step}  N {state.Count}", worldBounds.X + 8,
                worldBounds.Y + 5, UiCanvas.Muted, Math.Max(1, (worldBounds.Width - 16) / 6), 1);
        }
        if (worldBounds.Height >= 160 && editor.Appearance.ShowDiagnostics && controller.LatestDiagnostic is DiagnosticSample sample)
        {
            canvas.Text($"SAMPLE T {sample.Time:F3} / #{sample.Step}  SPEED {sample.MeanSpeed:F3}  CLUSTERS {sample.ClusterCount}",
                worldBounds.X + 14, worldBounds.Bottom - 37, UiCanvas.Accent, Math.Max(1, (worldBounds.Width - 28) / 7), 1.15f);
        }
        if (editor.Appearance.ShowHelp && worldBounds.Height >= 160)
        {
            canvas.Text("SPACE PLAY  N STEP  R RESET  E EDITOR  S FILES  CTRL+Z/Y UNDO/REDO", worldBounds.X + 14,
                worldBounds.Bottom - 18, UiCanvas.Muted, Math.Max(1, (worldBounds.Width - 28) / 6), 1);
        }
        batch.End();
    }

    private void SetMessage(string text)
    {
        message = text;
        Console.WriteLine(text);
    }

    private void OnKeyDown(object? sender, InputKeyEventArgs args)
    {
        if (heldKeys.Add(args.Key))
        {
            QueueKey(args.Key);
        }
    }
    private void OnKeyUp(object? sender, InputKeyEventArgs args)
    {
        heldKeys.Remove(args.Key);
    }
    private void OnTextInput(object? sender, TextInputEventArgs args)
    {
        KeyboardState current = Keyboard.GetState();
        // DesktopGL can deliver a character without an accompanying managed KeyDown
        // (including native text injection). Use its documented key while preventing
        // duplicate commands and ordinary held-letter auto-repeat from advancing twice.
        if (args.Key != Keys.None && !input.Any(entry => !entry.IsText && entry.Key == args.Key)
            && (!heldKeys.Contains(args.Key) || current.IsKeyUp(args.Key) || args.Key == Keys.Back))
        {
            QueueKey(args.Key);
        }
        (bool control, bool shift) = Modifiers(current);
        input.Add(UiInputEvent.Text(args.Character, control, shift));
    }

    private (bool Control, bool Shift) Modifiers(KeyboardState current) => (
        heldKeys.Contains(Keys.LeftControl) || heldKeys.Contains(Keys.RightControl)
            || current.IsKeyDown(Keys.LeftControl) || current.IsKeyDown(Keys.RightControl),
        heldKeys.Contains(Keys.LeftShift) || heldKeys.Contains(Keys.RightShift)
            || current.IsKeyDown(Keys.LeftShift) || current.IsKeyDown(Keys.RightShift));

    private void QueueKey(Keys key)
    {
        (bool control, bool shift) = Modifiers(Keyboard.GetState());
        input.Add(UiInputEvent.KeyPress(key, control, shift));
    }

    /// <summary>Supplements platform key events with edge detection from MonoGame's keyboard state.</summary>
    /// <remarks>Polling routes observed key edges through the same widget route and supplements
    /// buffered events; it cannot recover a complete press/release between observations. Per-frame
    /// deduplication prevents an event plus a polled edge from producing two commands. Reuses storage.</remarks>
    private void PollKeyboard()
    {
        KeyboardState current = Keyboard.GetState();
        int count = current.GetPressedKeyCount();
        current.GetPressedKeys(polledKeys);
        for (int index = 0; index < count; index++)
        {
            Keys key = polledKeys[index];
            if (!previousKeyboard.IsKeyDown(key) && !input.Any(entry => !entry.IsText && entry.Key == key))
            {
                QueueKey(key);
            }
        }
        previousKeyboard = current;
    }

    private void PrintTimings()
    {
        Console.WriteLine($"Viewer sample (~1 wall second): {framesPerSecond:F1} FPS; " +
        $"controller update mean {meanController:F3} ms, max {peakController:F3} ms (includes scheduled diagnostics); " +
        $"editor update mean {meanEditor:F3} ms; render submission mean {meanRender:F3} ms (excludes present); " +
        $"editor visible {editor.Appearance.ShowEditor}; applied scientific edits {lastSummaryEdits}; step {controller.Observe().Step}; dropped playback {controller.DroppedPlaybackSeconds:F6} s; " +
        $"search requested {editor.Configuration.Backend}; actual {controller.LastExecution?.ActualBackend.ToString() ?? "unstepped"}; " +
        $"workers {editor.Configuration.WorkerCount}/{controller.LastExecution?.ActualWorkerCount.ToString() ?? "unstepped"}; " +
        $"fallback {controller.LastExecution?.FallbackReason.ToString() ?? "none"}; direct fallback steps {controller.DirectFallbackSteps}; " +
        $"accumulation fallback steps {controller.AccumulationFallbackSteps}; candidates {controller.LastStatistics.CandidatePairs}; accepted {controller.LastStatistics.AcceptedPairs}.");
        if (lastEditedSample is EditedTimingSample sample)
        {
            Console.WriteLine($"Last edited sample (~1 wall second, ended {sample.EndedUtc:O}): {sample.FramesPerSecond:F1} FPS; " +
                $"controller mean {sample.ControllerMean:F3} ms, max {sample.ControllerPeak:F3} ms; editor mean {sample.EditorMean:F3} ms; " +
                $"render submission mean {sample.RenderMean:F3} ms; applied scientific edits {sample.Edits}; " +
                $"endpoint step {sample.Step}, time {sample.Time:G17}, particles {sample.Particles}, species {sample.Species}; " +
                $"dt {sample.TimeStep:G17}, cutoff {sample.Cutoff:G17}, search {sample.RequestedBackend}/{sample.ActualBackend?.ToString() ?? "unstepped"}, " +
                $"workers {sample.RequestedWorkers}/{sample.ActualWorkers?.ToString() ?? "unstepped"}; editor visible {sample.EditorVisible}; " +
                $"dropped playback {sample.DroppedPlayback:G17} s. Captured metadata belongs to this completed sample, not the current display.");
        }
    }

    /// <summary>One retained completed timing window with actual edits, outside the numerical hot loop.</summary>
    private readonly record struct EditedTimingSample(DateTimeOffset EndedUtc, double FramesPerSecond,
        double ControllerMean, double ControllerPeak, double EditorMean, double RenderMean, int Edits,
        long Step, double Time, int Particles, int Species, double TimeStep, double Cutoff,
        ForceBackend RequestedBackend, int RequestedWorkers, ForceBackend? ActualBackend, int? ActualWorkers,
        double DroppedPlayback, bool EditorVisible);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            editor.CancelSessionReplay();
            controller.Dispose();
            Window.KeyDown -= OnKeyDown;
            Window.KeyUp -= OnKeyUp;
            Window.TextInput -= OnTextInput;
            font?.Dispose();
            particle?.Dispose();
            pixel?.Dispose();
            clipped?.Dispose();
            batch?.Dispose();
        }
        base.Dispose(disposing);
    }
}
