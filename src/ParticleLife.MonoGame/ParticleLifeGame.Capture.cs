using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ParticleLife.MonoGame.Editor;
using ParticleLife.Viewer;

namespace ParticleLife.MonoGame;

internal sealed partial class ParticleLifeGame
{
    private RenderTarget2D? mediaTarget;
    private byte[] mediaReadback = [];
    private AnimationCapture? animation;
    private bool animationFinishing;
    private double animationEnd, nextAnimationFrame, capturePeakMilliseconds;
    private int animationFps, cadenceSkipped;

    /// <summary>Reads pixels only for a deliberate PNG/GIF request, after rendering the completed canvas.</summary>
    private void ProcessMediaCapture()
    {
        VisualToolsControls tools = surface.VisualTools;
        if (animation is null && !tools.AnimationRequested && !tools.ScreenshotRequested && !tools.StopAnimationRequested) { return; }
        double now = TimestampSeconds;
        try
        {
            if (animation is not null && animation.Completion.IsCompleted)
            {
                animation.Completion.GetAwaiter().GetResult();
                tools.CaptureStatus = $"Saved {Path.GetFileName(animation.Path)}; {animation.Width} x {animation.Height}, "
                    + $"{animation.WrittenFrames} frames, {animation.SkippedFrames + cadenceSkipped} skipped. "
                    + $"Peak readback/copy {capturePeakMilliseconds:F2} ms. Frame delays retain elapsed presentation time.";
                SetMessage(tools.CaptureStatus);
                animation = null; animationFinishing = false;
            }
            if (tools.AnimationRequested)
            {
                tools.AnimationRequested = false;
                if (animation is not null) { throw new InvalidOperationException("Finish the current GIF before starting another."); }
                (int width, int height) = CaptureDimensions(tools.CaptureWidth);
                if ((long)width * height * tools.CaptureFramesPerSecond * tools.CaptureSeconds > 80_000_000)
                { throw new InvalidOperationException("This GIF exceeds the 80 million pixel recording budget. Choose a smaller width, lower frame rate or shorter duration."); }
                animation = new AnimationCapture(NewMediaPath("gif"), width, height);
                animationFps = tools.CaptureFramesPerSecond;
                animationEnd = now + tools.CaptureSeconds;
                nextAnimationFrame = now; animationFinishing = false; cadenceSkipped = 0; capturePeakMilliseconds = 0;
            }
            if (animation is not null && !animationFinishing && (tools.StopAnimationRequested || now >= animationEnd))
            {
                animation.Finish(Math.Min(now, animationEnd)); animationFinishing = true;
            }
            tools.StopAnimationRequested = false;
            if (tools.ScreenshotRequested)
            {
                tools.ScreenshotRequested = false;
                (int width, int height) = CaptureDimensions(tools.CaptureWidth);
                long started = Stopwatch.GetTimestamp();
                DrawMediaCanvas(width, height);
                string path = NewMediaPath("png");
                MediaFile.SaveNew(path, stream => mediaTarget!.SaveAsPng(stream, width, height));
                tools.CaptureStatus = $"Saved {Path.GetFileName(path)} ({width} x {height}); capture/PNG write {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms.";
                SetMessage(tools.CaptureStatus);
            }
            if (animation is not null && !animationFinishing && now >= nextAnimationFrame)
            {
                cadenceSkipped += Math.Max(0, (int)Math.Floor((now - nextAnimationFrame) * animationFps));
                nextAnimationFrame = now + 1d / animationFps;
                long started = Stopwatch.GetTimestamp();
                DrawMediaCanvas(animation.Width, animation.Height);
                int length = checked(animation.Width * animation.Height * 4);
                if (mediaReadback.Length != length) { mediaReadback = new byte[length]; }
                mediaTarget!.GetData(mediaReadback);
                animation.TryAddFrame(mediaReadback, now);
                capturePeakMilliseconds = Math.Max(capturePeakMilliseconds, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            if (animation is not null)
            {
                tools.CaptureStatus = $"GIF {(animationFinishing ? "finishing" : "recording")}: {animation.Width} x {animation.Height}, "
                    + $"{animation.WrittenFrames} frames, {animation.SkippedFrames + cadenceSkipped} skipped; "
                    + $"peak readback/copy {capturePeakMilliseconds:F2} ms. Fixed numerical timestep retained.";
            }
        }
        catch (Exception exception) when (ViewerErrors.IsExpected(exception))
        {
            tools.CaptureStatus = "Capture error: " + exception.Message;
            SetMessage(tools.CaptureStatus);
            if (animation?.Completion.IsCompleted == true) { animation = null; animationFinishing = false; }
            else if (animation is not null) { animation.Finish(now); animationFinishing = true; }
        }
        finally { GraphicsDevice.SetRenderTarget(scene); }
    }

    private (int Width, int Height) CaptureDimensions(int requestedWidth)
    {
        double scale = Math.Min((double)requestedWidth / Math.Max(1, worldBounds.Width), 1920d / Math.Max(1, worldBounds.Height));
        return (Math.Clamp((int)Math.Round(worldBounds.Width * scale), 1, 1920),
            Math.Clamp((int)Math.Round(worldBounds.Height * scale), 1, 1920));
    }

    private string NewMediaPath(string extension) => Path.Combine(options.SaveDirectory,
        $"canvas-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..6]}.{extension}");

    /// <summary>Letterboxes resized canvases into the fixed recording resolution before explicit readback.</summary>
    private void DrawMediaCanvas(int width, int height)
    {
        if (mediaTarget?.Width != width || mediaTarget.Height != height)
        {
            mediaTarget?.Dispose();
            mediaTarget = new RenderTarget2D(GraphicsDevice, width, height, false, SurfaceFormat.Color, DepthFormat.None);
        }
        GraphicsDevice.SetRenderTarget(mediaTarget);
        GraphicsDevice.Clear(UiCanvas.Background);
        double scale = Math.Min((double)width / worldBounds.Width, (double)height / worldBounds.Height);
        int drawWidth = Math.Max(1, (int)Math.Round(worldBounds.Width * scale));
        int drawHeight = Math.Max(1, (int)Math.Round(worldBounds.Height * scale));
        Rectangle destination = new((width - drawWidth) / 2, (height - drawHeight) / 2, drawWidth, drawHeight);
        batch.Begin(blendState: BlendState.Opaque, samplerState: SamplerState.LinearClamp);
        batch.Draw(scene!, destination, worldBounds, Color.White);
        batch.End();
        GraphicsDevice.SetRenderTarget(scene);
    }

    private void FinishMediaCapture()
    {
        if (animation is not null)
        {
            animation.Finish(Math.Min(TimestampSeconds, animationEnd));
            try { animation.Completion.GetAwaiter().GetResult(); }
            catch (Exception exception) when (ViewerErrors.IsExpected(exception))
            { Console.Error.WriteLine("Animation could not be published: " + exception.Message); }
        }
        mediaTarget?.Dispose();
    }
}
