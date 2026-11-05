using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using ParticleLife.MonoGame.Editor;
using ParticleLife.MonoGame.Rendering;
using ParticleLife.Viewer;

namespace ParticleLife.MonoGame;

/// <summary>Owns native input/rendering while a dedicated CPU owner advances completed simulation states.</summary>
/// <remarks>Window events enqueue input. Update submits bounded commands without waiting for numerics.
/// Draw leases only published complete CPU buffers. A single world scale preserves geometry on resize.</remarks>
internal sealed class ParticleLifeGame : Game
{
    private readonly GraphicsDeviceManager graphics;
    private readonly SessionPlayback controller;
    private readonly EditorSession editor;
    private readonly LiveExperimentEditor surface;
    private readonly HelpLegend legend;
    private readonly ViewerOptions options;
    private readonly List<UiInputEvent> input = [];
    private readonly HashSet<Keys> heldKeys = [];
    private readonly Keys[] polledKeys = new Keys[256];
    private KeyboardState previousKeyboard;
    private SpriteBatch batch = null!;
    private Texture2D pixel = null!;
    private Texture2D particle = null!;
    private ParticleRenderer? particleRenderer;
    private Texture2D? splash;
    private double splashElapsed;
    private bool splashPresented;
    private RenderTarget2D? scene;
    private RenderTarget2D? softenedScene;
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
    private Rectangle worldBounds, gridBounds, editorBounds, modalBounds, legendBounds;
    private float inspectorWidth = 350;
    private float inspectorProgress;
    private bool statsExpanded = true;
    private float statsProgress = 1;
    private bool resizingInspector;
    private double messageUntil;

    internal ParticleLifeGame(ExperimentEditor editor, ViewerOptions options, string message)
    {
        this.options = options;
        this.message = message;
        messageUntil = TimestampSeconds + 5;
        this.editor = new EditorSession(editor, runInBackground: true);
        try
        {
            inspectorProgress = this.editor.Appearance.ShowEditor ? 1 : 0;
            controller = this.editor.Playback;
            this.editor.Failed += failure => SetMessage($"Operation failed: {failure}. Last completed state retained.");
            surface = new LiveExperimentEditor(this.editor, options.SaveDirectory, SetMessage, PrintTimings);
            legend = new HelpLegend(CloseLegend, SetMessage);
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
        catch
        {
            this.editor.Dispose();
            throw;
        }
    }

    protected override void LoadContent()
    {
        batch = new SpriteBatch(GraphicsDevice);
        pixel = new Texture2D(GraphicsDevice, 1, 1);
        pixel.SetData(new[] { Color.White });
        particle = CreateParticleTexture();
        particleRenderer = new ParticleRenderer(GraphicsDevice, particle);
        using (Stream stream = typeof(ParticleLifeGame).Assembly.GetManifestResourceStream(
            "ParticleLife.MonoGame.Assets.Branding.splash.png")
            ?? throw new InvalidOperationException("The startup artwork is missing."))
        {
            splash = Texture2D.FromStream(GraphicsDevice, stream);
        }
        font = new BitmapFont(GraphicsDevice);
        canvas = new UiCanvas(batch, pixel, font);
        clipped = new RasterizerState { CullMode = CullMode.None, ScissorTestEnable = true };
        Console.WriteLine($"Viewer: {GraphicsDevice.Adapter.Description}; {Environment.Version}; {Environment.OSVersion}");
        Console.WriteLine($"Particle renderer: {particleRenderer.Description}; CPU drawing is independent of compute availability.");
        Console.WriteLine($"Experiment: {controller.Observe().ExperimentName}; {controller.Observe().Count} particles; dt={editor.Configuration.TimeStep:G17}; " +
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
        editor.Pump();
        long now = Stopwatch.GetTimestamp();
        double elapsed = Stopwatch.GetElapsedTime(previousTimestamp, now).TotalSeconds;
        previousTimestamp = now;
        MouseState mouse = Mouse.GetState();
        if (!IsActive)
        {
            if (wasActive)
            {
                surface.LoseFocus();
                legend.LoseFocus();
                resizingInspector = false;
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
        if (UpdateSplash(skipPlayback ? 0 : elapsed, mouse))
        {
            base.Update(gameTime);
            return;
        }
        inspectorProgress = AdvanceReveal(inspectorProgress, editor.Appearance.ShowEditor, elapsed);
        statsProgress = AdvanceReveal(statsProgress, statsExpanded, elapsed);
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
        long editorStarted = Stopwatch.GetTimestamp();
        try
        {
            if (editor.Appearance.ShowHelp && !surface.IsModal)
            {
                legend.Update(legendBounds, uiScale, mouse, input, CloseLegend);
                headerClick = true;
            }
            else
            {
                // Let the editor validate an outgoing draft before header actions may mutate state.
                surface.Update(editorBounds, modalBounds, uiScale, mouse, input);
                skipPlayback |= surface.SuppressPlayback;
                if (editor.Appearance.ShowHelp)
                {
                    legend.PrepareForOpen(mouse.LeftButton == ButtonState.Pressed);
                }
                if (clicked && !surface.IsModal && ToastVisible && ToastCloseBounds.Contains(mouse.Position))
                {
                    messageUntil = 0;
                    headerClick = true;
                }
                if (clicked && !headerClick && !surface.IsModal && !surface.CapturesKeyboard)
                {
                    headerClick = HandleHeaderClick(mouse.Position, ref skipPlayback);
                    Rectangle panel = Physical(editorBounds);
                    resizingInspector = editor.Appearance.ShowEditor && GraphicsDevice.Viewport.Width >= 960
                        && new Rectangle(panel.X - 7, panel.Y + 12, 10, panel.Height - 24).Contains(mouse.Position);
                }
                if (resizingInspector)
                {
                    if (mouse.LeftButton == ButtonState.Released)
                    {
                        resizingInspector = false;
                    }
                    else
                    {
                        inspectorWidth = Math.Clamp((GraphicsDevice.Viewport.Width - mouse.X - 14) / uiScale, 350, 600);
                    }
                }
            }
            if (!capturedBefore && !surface.CapturesKeyboard && !surface.ConsumedKeyboard && !headerClick && !resizingInspector)
            {
                foreach (UiInputEvent entry in input)
                {
                    if (!entry.IsText)
                    {
                        skipPlayback |= HandleShortcut(entry.Key, entry.Control);
                        if (surface.CapturesKeyboard || editor.Appearance.ShowHelp)
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
                EditorPublication state = controller.Observe();
                SimulationConfiguration configuration = state.Configuration;
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

    /// <summary>Consumes startup frames without advancing the experiment or delivering editor input.</summary>
    /// <remarks>The splash lasts 2.5 active wall seconds after its first draw and may be dismissed
    /// with a fresh key or left click. Its final frame is consumed too: the dismissal cannot trigger
    /// an editor action. The left button must be released before exposing widgets, whose own
    /// click history has not observed the splash. The next playback frame sees only its own elapsed time. Focus loss
    /// continues to use the normal pause policy. The GPU texture is released when no longer needed.</remarks>
    private bool UpdateSplash(double elapsed, MouseState mouse)
    {
        if (splash is null)
        {
            return false;
        }
        PollKeyboard();
        bool clicked = mouse.LeftButton == ButtonState.Released && previousMouseButton == ButtonState.Pressed;
        bool dismiss = splashPresented && (clicked || input.Any(entry => !entry.IsText));
        if (splashPresented)
        {
            splashElapsed += elapsed;
        }
        input.Clear();
        previousMouseButton = mouse.LeftButton;
        if ((dismiss || splashElapsed >= 2.5) && mouse.LeftButton == ButtonState.Released)
        {
            splash.Dispose();
            splash = null;
            messageUntil = TimestampSeconds + 5;
            previousTimestamp = Stopwatch.GetTimestamp();
        }
        return true;
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
                SetMessage(controller.IsPaused ? "Pause queued. The current step will finish before playback pauses." : "Resume queued. Playback restarts without a wall-time backlog.");
                return true;
            case Keys.N:
            case Keys.Right: controller.SingleStep(); return true;
            case Keys.R: surface.Reset(); return true;
            case Keys.I: surface.Resample(); return true;
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
                    surface.ReloadExperiment(options.InputPath, options.BackendOverride, options.WorkerOverride, options.SampleEveryOverride);
                }
                return true;
            case Keys.D: surface.ToggleDiagnostics(); return false;
            case Keys.H:
            case Keys.F1: ToggleLegend(); return false;
            case Keys.F9: return false;
            default: return false;
        }
    }

    private void ToggleLegend()
    {
        surface.LoseFocus();
        if (!editor.Appearance.ShowHelp)
        {
            legend.PrepareForOpen(Mouse.GetState().LeftButton == ButtonState.Pressed);
        }
        editor.SetAppearance(editor.Appearance with { ShowHelp = !editor.Appearance.ShowHelp });
    }

    private void CloseLegend() => editor.SetAppearance(editor.Appearance with { ShowHelp = false });

    /// <summary>Returns physical workspace-control hit rectangles shared by drawing and input.</summary>
    private Rectangle HeaderButton(int index)
    {
        int width = GraphicsDevice.Viewport.Width;
        int start = Math.Clamp(gridBounds.X + 10, worldBounds.X + 6, Math.Max(worldBounds.X + 6, worldBounds.Right - 120));
        int top = Math.Clamp(gridBounds.Y + 10, worldBounds.Y + 6, Math.Max(worldBounds.Y + 6, worldBounds.Bottom - 38));
        int inspectorTop = width >= 960 ? 60 : 70 + Math.Max(66, (int)((GraphicsDevice.Viewport.Height - 76) * 0.36));
        return index switch
        {
            0 => new Rectangle(start, top, 34, 30),
            1 => new Rectangle(start + 38, top, 34, 30),
            2 => new Rectangle(width - 48, inspectorTop + 2, 30, 28),
            3 => new Rectangle(18, GraphicsDevice.Viewport.Height - 44, 30, 28),
            4 => new Rectangle(start + 76, top, 34, 30),
            _ => new Rectangle(18, 65, 28, 28)
        };
    }

    private bool HandleHeaderClick(Point point, ref bool skipPlayback)
    {
        if (HeaderButton(0).Contains(point))
        {
            controller.SetPaused(!controller.IsPaused);
            skipPlayback = true;
        }
        else if (HeaderButton(1).Contains(point))
        {
            controller.SingleStep();
            skipPlayback = true;
        }
        else if (HeaderButton(2).Contains(point))
        {
            surface.ToggleVisibility();
        }
        else if (HeaderButton(3).Contains(point))
        {
            ToggleLegend();
        }
        else if (HeaderButton(4).Contains(point))
        {
            surface.Reset();
            skipPlayback = true;
        }
        else if (HeaderButton(5).Contains(point))
        {
            statsExpanded = !statsExpanded;
        }
        else
        {
            return false;
        }
        return true;
    }

    /// <summary>Animates presentation-only pane visibility over 0.2 seconds without advancing simulation time.</summary>
    private static float AdvanceReveal(float current, bool expanded, double elapsed)
    {
        float movement = (float)Math.Min(elapsed, 0.05) / 0.2f;
        return expanded ? Math.Min(1, current + movement) : Math.Max(0, current - movement);
    }

    private static float SmoothReveal(float progress) => progress * progress * (3 - 2 * progress);
    private int StatsRailWidth => GraphicsDevice.Viewport.Width >= 700 ? 132 : 102;

    /// <summary>Places the floating inspector while reserving an isotropically mapped visible world.</summary>
    /// <remarks>Panel widths are presentation pixels. Resizing never changes physical coordinates.</remarks>
    private void Layout()
    {
        int width = GraphicsDevice.Viewport.Width;
        int height = GraphicsDevice.Viewport.Height;
        bool wide = width >= 960;
        uiScale = (float)Math.Min(editor.Appearance.UiScale,
            wide ? Math.Max(0.75, width * 0.46 / 350) : Math.Max(0.5, (width - 28) / 350.0));
        int railWidth = (int)(StatsRailWidth * SmoothReveal(statsProgress));
        worldBounds = new Rectangle(railWidth + 24, 60, Math.Max(1, width - railWidth - 38), Math.Max(1, height - 76));
        float reveal = SmoothReveal(inspectorProgress);
        if (wide)
        {
            int panelWidth = Math.Min((int)(inspectorWidth * uiScale), (int)(width * 0.46));
            int visibleWidth = (int)((panelWidth + 14) * reveal);
            editorBounds = Logical(new Rectangle(width - visibleWidth, 60, panelWidth, height - 76));
            worldBounds.Width = Math.Max(1, width - railWidth - 38 - visibleWidth);
        }
        else
        {
            int worldHeight = Math.Max(66, (int)((height - 76) * 0.36));
            worldBounds.Height -= (int)((worldBounds.Height - worldHeight) * reveal);
            int paneWidth = width - 28;
            editorBounds = Logical(new Rectangle(14 + (int)(width * (1 - reveal)), 70 + worldHeight, paneWidth,
                Math.Max(80, height - worldHeight - 124)));
        }
        int modalWidth = Math.Min(width - 28, (int)(800 * uiScale));
        modalBounds = Logical(new Rectangle((width - modalWidth) / 2, 60, modalWidth, Math.Max(120, height - 76)));
        int legendWidth = Math.Min(width - 28, (int)(560 * uiScale));
        int legendHeight = Math.Min(height - 76, (int)(720 * uiScale));
        legendBounds = Logical(new Rectangle((width - legendWidth) / 2, 60, legendWidth, Math.Max(120, legendHeight)));
        legend.Arrange(legendBounds);
        WorldTransform transform = CreateWorldTransform();
        gridBounds = new Rectangle((int)transform.OffsetX, (int)transform.OffsetY,
            Math.Max(1, (int)transform.Width), Math.Max(1, (int)transform.Height));
    }

    private Rectangle Logical(Rectangle screen) => new((int)(screen.X / uiScale), (int)(screen.Y / uiScale),
        Math.Max(1, (int)(screen.Width / uiScale)), Math.Max(1, (int)(screen.Height / uiScale)));

    private Rectangle Physical(Rectangle logical) => Rectangle.Intersect(new Rectangle((int)(logical.X * uiScale),
        (int)(logical.Y * uiScale), Math.Max(1, (int)(logical.Width * uiScale)), Math.Max(1, (int)(logical.Height * uiScale))), GraphicsDevice.Viewport.Bounds);

    /// <summary>Reuses presentation targets; only a window resize reallocates GPU storage.</summary>
    /// <remarks>The small bilinearly sampled target softens scene detail under panels. It never
    /// feeds into simulation state or changes particle rendering outside the control layer.</remarks>
    private void EnsureSceneTargets(int width, int height)
    {
        if (scene?.Width == width && scene.Height == height)
        {
            return;
        }
        scene?.Dispose();
        softenedScene?.Dispose();
        scene = new RenderTarget2D(GraphicsDevice, width, height, false, SurfaceFormat.Color, DepthFormat.None);
        softenedScene = new RenderTarget2D(GraphicsDevice, Math.Max(1, width / 8), Math.Max(1, height / 8),
            false, SurfaceFormat.Color, DepthFormat.None);
    }

    protected override void Draw(GameTime gameTime)
    {
        long started = Stopwatch.GetTimestamp();
        int width = GraphicsDevice.Viewport.Width;
        int height = GraphicsDevice.Viewport.Height;
        if (splash is not null)
        {
            GraphicsDevice.Clear(new Color(3, 40, 57));
            float scale = Math.Min((float)width / splash.Width, (float)height / splash.Height);
            int drawWidth = Math.Max(1, (int)(splash.Width * scale));
            int drawHeight = Math.Max(1, (int)(splash.Height * scale));
            Rectangle bounds = new((width - drawWidth) / 2, (height - drawHeight) / 2, drawWidth, drawHeight);
            batch.Begin(blendState: BlendState.Opaque, samplerState: SamplerState.PointClamp);
            batch.Draw(splash, bounds, Color.White);
            batch.End();
            splashPresented = true;
            base.Draw(gameTime);
            return;
        }
        GraphicsDevice.Clear(UiCanvas.Background);
        if (width < 420 || height < 340)
        {
            batch.Begin(samplerState: SamplerState.LinearClamp);
            canvas.Text("Enlarge window to 420 x 340", 14, 14);
            batch.End();
            return;
        }
        Layout();
        EnsureSceneTargets(width, height);
        GraphicsDevice.SetRenderTarget(scene);
        GraphicsDevice.Clear(UiCanvas.Background);
        DrawWorld();
        GraphicsDevice.SetRenderTarget(softenedScene);
        batch.Begin(blendState: BlendState.Opaque, samplerState: SamplerState.LinearClamp);
        batch.Draw(scene!, new Rectangle(0, 0, softenedScene!.Width, softenedScene.Height), Color.White);
        batch.End();
        GraphicsDevice.SetRenderTarget(null);
        batch.Begin(blendState: BlendState.Opaque, samplerState: SamplerState.LinearClamp);
        batch.Draw(scene!, GraphicsDevice.Viewport.Bounds, Color.White);
        batch.End();

        batch.Begin(samplerState: SamplerState.LinearClamp);
        canvas.Text(width >= 620 ? "Particle Life 2" : "PL2", 20, 17, UiCanvas.Ink, size: 2.1f);
        Rectangle transport = TransportBounds;
        float transportOpacity = TransportOpacity;
        canvas.Rounded(transport, UiCanvas.Background * (0.9f * transportOpacity), 6);
        canvas.RoundedBorder(transport, UiCanvas.Muted * (0.25f * transportOpacity), 6);
        for (int index = 0; index < 6; index++)
        {
            if (index == 2)
            {
                continue;
            }
            DrawHeaderControl(index);
        }
        DrawStatus();
        batch.End();
        if (inspectorProgress > 0)
        {
            DrawPanel(surface.PanelBounds, surface.PanelBody, surface.DrawChrome, surface.DrawBody, surface.DrawOverlay);
            batch.Begin(samplerState: SamplerState.LinearClamp);
            if (width >= 960 && inspectorProgress > 0.95f)
            {
                Rectangle panel = Physical(editorBounds);
                canvas.Rounded(new Rectangle(panel.X - 5, panel.Center.Y - 22, 2, 44), UiCanvas.Muted * (resizingInspector ? 0.9f : 0.35f), 1);
            }
            batch.End();
        }
        batch.Begin(samplerState: SamplerState.LinearClamp);
        DrawHeaderControl(2);
        DrawNotification();
        batch.End();
        if (surface.IsModal || editor.Appearance.ShowHelp)
        {
            batch.Begin(samplerState: SamplerState.LinearClamp);
            batch.Draw(softenedScene!, GraphicsDevice.Viewport.Bounds, Color.White);
            canvas.Fill(GraphicsDevice.Viewport.Bounds, Color.Black * 0.35f);
            batch.End();
            if (surface.IsModal)
            {
                DrawPanel(surface.BrowserBounds, surface.BrowserBody, surface.DrawBrowserChrome, surface.DrawBrowserBody);
            }
            else
            {
                DrawPanel(legend.Bounds, legend.Body, legend.DrawChrome, legend.DrawBody);
            }
        }
        summaryFrames++;
        renderTotal += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        base.Draw(gameTime);
    }

    private void DrawPanel(Rectangle panelBounds, Rectangle body, Action<UiCanvas> chrome,
        Action<UiCanvas> content, Action<UiCanvas>? overlay = null)
    {
        Rectangle physical = Physical(panelBounds);
        if (physical.Width <= 0 || physical.Height <= 0)
        {
            return;
        }
        batch.Begin(samplerState: SamplerState.LinearClamp);
        Rectangle source = new(physical.X * softenedScene!.Width / scene!.Width,
            physical.Y * softenedScene.Height / scene.Height,
            Math.Max(1, physical.Width * softenedScene.Width / scene.Width),
            Math.Max(1, physical.Height * softenedScene.Height / scene.Height));
        batch.Draw(softenedScene, physical, source, Color.White);
        batch.End();
        GraphicsDevice.ScissorRectangle = physical;
        batch.Begin(samplerState: SamplerState.LinearClamp, rasterizerState: clipped, transformMatrix: Matrix.CreateScale(uiScale));
        chrome(canvas);
        batch.End();
        GraphicsDevice.ScissorRectangle = Physical(body);
        batch.Begin(samplerState: SamplerState.LinearClamp, rasterizerState: clipped, transformMatrix: Matrix.CreateScale(uiScale));
        content(canvas);
        batch.End();
        if (overlay is not null)
        {
            GraphicsDevice.ScissorRectangle = physical;
            batch.Begin(samplerState: SamplerState.LinearClamp, rasterizerState: clipped, transformMatrix: Matrix.CreateScale(uiScale));
            overlay(canvas);
            batch.End();
        }
    }

    private WorldTransform CreateWorldTransform() => new(editor.Publication.Configuration.Parameters.WorldWidth,
        editor.Publication.Configuration.Parameters.WorldHeight, worldBounds.X, worldBounds.Y,
        Math.Max(1, worldBounds.Width), Math.Max(1, worldBounds.Height));

    private void DrawWorld()
    {
        using RenderFrameLease? state = editor.AcquireRenderFrame();
        if (state is null) { return; }
        WorldTransform transform = new(state.Configuration.Parameters.WorldWidth, state.Configuration.Parameters.WorldHeight,
            worldBounds.X, worldBounds.Y, Math.Max(1, worldBounds.Width), Math.Max(1, worldBounds.Height));
        Rectangle world = new((int)transform.OffsetX, (int)transform.OffsetY,
            Math.Max(1, (int)transform.Width), Math.Max(1, (int)transform.Height));
        batch.Begin(samplerState: SamplerState.LinearClamp);
        // A quiet full-window field sits beneath the floating control layer.
        for (int x = worldBounds.X; x < GraphicsDevice.Viewport.Width; x += 48)
        {
            canvas.Fill(new Rectangle(x, 60, 1, GraphicsDevice.Viewport.Height - 76), new Color(16, 25, 29));
        }
        for (int y = 60; y < GraphicsDevice.Viewport.Height - 16; y += 48)
        {
            canvas.Fill(new Rectangle(worldBounds.X, y, GraphicsDevice.Viewport.Width - worldBounds.X - 14, 1), new Color(16, 25, 29));
        }
        canvas.Fill(world, new Color(12, 20, 24));
        for (int index = 0; index <= 10; index++)
        {
            Color line = index is 0 or 10 ? new Color(36, 53, 59) : new Color(19, 31, 36);
            canvas.Fill(new Rectangle(world.X + world.Width * index / 10, world.Y, 1, world.Height), line);
            canvas.Fill(new Rectangle(world.X, world.Y + world.Height * index / 10, world.Width, 1), line);
        }
        batch.End();
        GraphicsDevice.ScissorRectangle = Rectangle.Intersect(worldBounds, GraphicsDevice.Viewport.Bounds);
        particleRenderer!.Draw(state, transform, worldBounds, editor.Appearance);
    }

    /// <summary>Groups playback controls inside the grid, with more transparent material while idle.</summary>
    private Rectangle TransportBounds => new(HeaderButton(0).X - 2, HeaderButton(0).Y - 1, 114, 32);

    private float TransportOpacity => TransportBounds.Contains(Mouse.GetState().Position) ? 1 : 0.4f;

    /// <summary>Draws transport and pane affordances with the same hit bounds used by input.</summary>
    private void DrawHeaderControl(int index)
    {
        Rectangle bounds = HeaderButton(index);
        bool hovered = bounds.Contains(Mouse.GetState().Position);
        if (index is 2 or 3 or 5)
        {
            canvas.Glass(bounds, 5);
        }
        if (hovered)
        {
            canvas.Rounded(bounds, UiCanvas.Accent * 0.12f, 5);
        }
        Color tint = index == 0 ? UiCanvas.Ink : UiCanvas.Muted;
        if (index is 0 or 1 or 4)
        {
            tint *= TransportOpacity;
        }
        Vector2 center = new(bounds.Center.X, bounds.Center.Y);
        if (index == 0 && !controller.IsPaused)
        {
            canvas.Rounded(new Rectangle(bounds.Center.X - 5, bounds.Center.Y - 6, 3, 12), tint, 1);
            canvas.Rounded(new Rectangle(bounds.Center.X + 2, bounds.Center.Y - 6, 3, 12), tint, 1);
        }
        else if (index is 0 or 1)
        {
            Vector2 a = center + new Vector2(-5, -6);
            Vector2 b = center + new Vector2(-5, 6);
            Vector2 c = center + new Vector2(5, 0);
            canvas.Line(a, b, tint, 1.5f);
            canvas.Line(b, c, tint, 1.5f);
            canvas.Line(c, a, tint, 1.5f);
            if (index == 1)
            {
                canvas.Line(center + new Vector2(8, -6), center + new Vector2(8, 6), tint, 1.5f);
            }
        }
        else if (index == 2)
        {
            canvas.RoundedBorder(new Rectangle(bounds.Center.X - 8, bounds.Center.Y - 6, 16, 13), tint, 2);
            canvas.Line(center + new Vector2(3, -6), center + new Vector2(3, 6), tint);
            float direction = editor.Appearance.ShowEditor ? 1 : -1;
            canvas.Line(center + new Vector2(-2 - direction * 2, -2), center + new Vector2(-2 + direction * 2, 0), tint);
            canvas.Line(center + new Vector2(-2 + direction * 2, 0), center + new Vector2(-2 - direction * 2, 2), tint);
        }
        else if (index == 3)
        {
            canvas.TextCentered("?", bounds, UiCanvas.Ink, 1.6f);
        }
        else if (index == 4)
        {
            Vector2 previous = center + new Vector2(-5, -3);
            for (int segment = 1; segment <= 16; segment++)
            {
                float angle = MathF.PI * (1.17f + segment * 1.65f / 16);
                Vector2 next = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 6;
                canvas.Line(previous, next, tint, 1.5f);
                previous = next;
            }
            canvas.Line(center + new Vector2(-5, -3), center + new Vector2(-5, -7), tint, 1.5f);
            canvas.Line(center + new Vector2(-5, -3), center + new Vector2(-1, -3), tint, 1.5f);
        }
        else
        {
            canvas.RoundedBorder(new Rectangle(bounds.Center.X - 8, bounds.Center.Y - 6, 16, 13), tint, 2);
            canvas.Line(center + new Vector2(-3, -6), center + new Vector2(-3, 6), tint);
            float direction = statsExpanded ? -1 : 1;
            canvas.Line(center + new Vector2(2 - direction * 2, -2), center + new Vector2(2 + direction * 2, 0), tint);
            canvas.Line(center + new Vector2(2 + direction * 2, 0), center + new Vector2(2 - direction * 2, 2), tint);
        }
        if (hovered && index != 3)
        {
            string label = index switch
            {
                0 => controller.IsPaused ? "Resume" : "Pause",
                1 => "Single step",
                2 => editor.Appearance.ShowEditor ? "Close inspector" : "Open inspector",
                4 => "Reset experiment",
                _ => statsExpanded ? "Collapse statistics" : "Expand statistics"
            };
            int tooltipWidth = (int)canvas.Measure(label, 1.35f) + 16;
            Rectangle tooltip = new(Math.Clamp(bounds.Center.X - tooltipWidth / 2, 8, GraphicsDevice.Viewport.Width - tooltipWidth - 8),
                bounds.Bottom + 5, tooltipWidth, 24);
            canvas.Glass(tooltip, 4);
            canvas.TextCentered(label, tooltip, UiCanvas.Ink, 1.35f);
        }
    }

    /// <summary>Places measurements in the reserved left rail, never over the particle grid.</summary>
    private void DrawStatus()
    {
        if (statsProgress <= 0)
        {
            return;
        }
        EditorPublication state = controller.Observe();
        float opacity = SmoothReveal(statsProgress);
        int offset = (int)(StatsRailWidth * (1 - opacity));
        int x = 22 - offset;
        int railWidth = StatsRailWidth - 12;
        int y = 110;
        if (editor.Appearance.ShowOverlay && railWidth > 60)
        {
            canvas.Rounded(new Rectangle(x, y + 5, 5, 5), (controller.IsPaused ? UiCanvas.Muted : UiCanvas.Accent) * opacity, 2);
            canvas.Text(editor.HasPendingCommands ? "Queued" : state.IsPaused ? "Paused" : "Running", x + 12, y, UiCanvas.Ink * opacity, size: 1.4f);
            y += worldBounds.Height < 200 ? 24 : 42;
            Metric("Particles", state.Count.ToString("N0"));
            Metric("Simulation time", state.Time.ToString("F3"));
            Metric("Completed steps", state.Step.ToString("N0"));
            Metric("Species", editor.Configuration.Matrix.SpeciesCount.ToString());
            Metric("Frame rate", $"{framesPerSecond:F0} fps");
            Metric("CPU steps", $"{state.StepsPerSecond:F0} /s");
            Metric("Achieved speed", $"{state.SimulationSpeed:F2}x");
            Metric("Timestep", $"{editor.Configuration.TimeStep:G4}");
            Metric("Playback", $"{controller.PlaybackSpeed:G3}x");
        }
        if (editor.Appearance.ShowDiagnostics && controller.LatestDiagnostic is DiagnosticSample sample && y + 130 < worldBounds.Bottom)
        {
            canvas.Fill(new Rectangle(x, y, Math.Max(1, railWidth - 8), 1), UiCanvas.Muted * (0.2f * opacity));
            y += 18;
            Metric("Mean speed", sample.MeanSpeed.ToString("F3"));
            Metric("Clusters", sample.ClusterCount.ToString());
            canvas.TextFit($"Sample t {sample.Time:F3}", new Rectangle(x, y, railWidth, 18), UiCanvas.Muted * opacity, 1.25f);
        }
        void Metric(string label, string value)
        {
            if (y + 46 > worldBounds.Bottom - 8)
            {
                return;
            }
            canvas.TextFit(label, new Rectangle(x, y, railWidth, 18), UiCanvas.Muted * opacity, 1.35f);
            canvas.TextFit(value, new Rectangle(x, y + 19, railWidth, 27), UiCanvas.Ink * opacity, 2.0f);
            y += 66;
        }
    }

    private bool ToastVisible => !string.IsNullOrEmpty(message) && TimestampSeconds < messageUntil + 0.35;

    /// <summary>Anchors transient messages to the visible canvas edge, following inspector motion.</summary>
    private Rectangle ToastBounds
    {
        get
        {
            int width = Math.Min(320, GraphicsDevice.Viewport.Width - 40);
            int right = GraphicsDevice.Viewport.Width >= 960 ? worldBounds.Right : GraphicsDevice.Viewport.Width - 14;
            int bottom = GraphicsDevice.Viewport.Height - 16;
            if (GraphicsDevice.Viewport.Width < 960 && inspectorProgress > 0.05f)
            {
                bottom = worldBounds.Bottom - 4;
            }
            int height = Math.Clamp(30 + NotificationLines(message, width - 44) * 18, 60, 156);
            return new Rectangle(Math.Max(14, right - width), Math.Max(56, bottom - height), width, height);
        }
    }

    private Rectangle ToastCloseBounds => new(ToastBounds.Right - 27, ToastBounds.Y + 5, 22, 22);

    /// <summary>Counts wrapped notification lines with the same proportional advances as the text renderer.</summary>
    private static int NotificationLines(string text, int width)
    {
        int lines = 1;
        int start = 0;
        int lastSpace = -1;
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == ' ')
            {
                lastSpace = index;
            }
            if (text[index] == '\n' || BitmapFont.Measure(text.AsSpan(start, index - start + 1)) > width)
            {
                lines++;
                start = text[index] == '\n' ? index + 1 : lastSpace >= start ? lastSpace + 1 : index;
                lastSpace = -1;
            }
        }
        return lines;
    }

    private void DrawNotification()
    {
        if (!ToastVisible)
        {
            return;
        }
        float opacity = (float)Math.Clamp((messageUntil + 0.35 - TimestampSeconds) / 0.35, 0, 1);
        Rectangle bounds = ToastBounds;
        canvas.Rounded(new Rectangle(bounds.X, bounds.Y + 3, bounds.Width, bounds.Height), Color.Black * (0.25f * opacity), 7);
        canvas.Rounded(bounds, UiCanvas.Background * (0.96f * opacity), 7);
        canvas.RoundedBorder(bounds, UiCanvas.Muted * (0.35f * opacity), 7);
        canvas.Wrapped(message, new Rectangle(bounds.X + 14, bounds.Y + 14, bounds.Width - 44, bounds.Height - 24), UiCanvas.Ink * opacity);
        Rectangle close = ToastCloseBounds;
        Vector2 center = new(close.Center.X, close.Center.Y);
        canvas.Line(center + new Vector2(-3, -3), center + new Vector2(3, 3), UiCanvas.Muted * opacity, 1.5f);
        canvas.Line(center + new Vector2(-3, 3), center + new Vector2(3, -3), UiCanvas.Muted * opacity, 1.5f);
    }

    private static double TimestampSeconds => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private void SetMessage(string text)
    {
        message = text;
        messageUntil = TimestampSeconds + 5;
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
        $"playback submission mean {meanController:F3} ms, max {peakController:F3} ms; " +
        $"CPU {editor.Publication.StepsPerSecond:F1} steps/s, achieved {editor.Publication.SimulationSpeed:F3} simulation seconds/wall second; " +
        $"editor update mean {meanEditor:F3} ms; render submission mean {meanRender:F3} ms (excludes present); " +
        $"editor visible {editor.Appearance.ShowEditor}; applied scientific edits {lastSummaryEdits}; step {controller.Observe().Step}; dropped playback {controller.DroppedPlaybackSeconds:F6} s; " +
        $"search requested {editor.Configuration.Backend}; actual {controller.LastExecution?.ActualBackend.ToString() ?? "unstepped"}; " +
        $"workers {editor.Configuration.WorkerCount}/{controller.LastExecution?.ActualWorkerCount.ToString() ?? "unstepped"}; " +
        $"fallback {controller.LastExecution?.FallbackReason.ToString() ?? "none"}; direct fallback steps {controller.DirectFallbackSteps}; " +
        $"accumulation fallback steps {controller.AccumulationFallbackSteps}; candidates {controller.LastStatistics.CandidatePairs}; accepted {controller.LastStatistics.AcceptedPairs}.");
        if (particleRenderer is not null)
        {
            ParticleRenderMetrics metrics = particleRenderer.LastMetrics;
            Console.WriteLine($"Last particle draw ({metrics.Backend}): preparation {metrics.PreparationMilliseconds:F3} ms; " +
                $"upload call {metrics.UploadMilliseconds:F3} ms / {metrics.UploadedBytes:N0} bytes; " +
                $"submission/state {metrics.SubmissionMilliseconds:F3} ms; submitted {metrics.SubmittedParticles:N0}/{metrics.InputCount:N0}; " +
                $"fallback {metrics.FallbackReason ?? "none"}. Excludes GPU completion and present.");
        }
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
            editor.Dispose();
            Window.KeyDown -= OnKeyDown;
            Window.KeyUp -= OnKeyUp;
            Window.TextInput -= OnTextInput;
            scene?.Dispose();
            softenedScene?.Dispose();
            font?.Dispose();
            splash?.Dispose();
            particleRenderer?.Dispose();
            particle?.Dispose();
            pixel?.Dispose();
            clipped?.Dispose();
            batch?.Dispose();
        }
        base.Dispose(disposing);
    }
}
