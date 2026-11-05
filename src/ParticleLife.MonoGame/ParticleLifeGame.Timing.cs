using System.Diagnostics;

namespace ParticleLife.MonoGame;

internal sealed partial class ParticleLifeGame
{
    private readonly PresentationTimings nativeTimings = new();

    /// <summary>Separately records the native Present call, including any driver/vsync wait.</summary>
    protected override void EndDraw()
    {
        long started = Stopwatch.GetTimestamp();
        base.EndDraw();
        long completed = Stopwatch.GetTimestamp();
        nativeTimings.RecordPresent(completed, Stopwatch.GetElapsedTime(started, completed).TotalMilliseconds);
    }
}
