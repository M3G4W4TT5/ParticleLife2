using System.Diagnostics;
using System.Globalization;

namespace ParticleLife.MonoGame;

/// <summary>Retains bounded native presentation measurements without allocating per frame.</summary>
/// <remarks>Input latency runs from the start of an update containing observed keyboard/button/drag
/// input to Present returning. It includes application work and presentation wait, but excludes OS
/// event delivery and cannot measure display scanout or physical input-to-photon latency. F10 starts
/// a fresh comparison window; quantiles are nearest-rank over at most 1,024 samples per series.</remarks>
internal sealed class PresentationTimings
{
    private readonly Samples frames = new(), drawing = new(), presentation = new(), input = new();
    private long previousPresent, pendingInput;

    internal void ObserveInput(long updateStarted)
    {
        if (pendingInput == 0) { pendingInput = updateStarted; }
    }

    internal void ForgetPendingInput() => pendingInput = 0;
    internal void RecordDraw(double milliseconds) => drawing.Add(milliseconds);

    internal void RecordPresent(long completed, double presentMilliseconds)
    {
        if (previousPresent != 0) { frames.Add(Stopwatch.GetElapsedTime(previousPresent, completed).TotalMilliseconds); }
        previousPresent = completed;
        presentation.Add(presentMilliseconds);
        if (pendingInput != 0)
        {
            input.Add(Stopwatch.GetElapsedTime(pendingInput, completed).TotalMilliseconds);
            pendingInput = 0;
        }
    }

    internal void Reset()
    {
        frames.Reset(); drawing.Reset(); presentation.Reset(); input.Reset();
        previousPresent = pendingInput = 0;
    }

    internal string Describe() => $"Native timing window (up to 1024 samples; F10 resets): frame interval {frames.Describe()}; "
        + $"draw CPU {drawing.Describe()}; Present CPU wait {presentation.Describe()}; observed-input update to Present return {input.Describe()}. "
        + "Input is an application responsiveness proxy, excluding OS delivery/scanout and later queued simulation-command completion.";

    /// <summary>Summarizes the same retained samples for a short visible result, without resetting the window.</summary>
    internal string DescribeSummary() => $"Frame {frames.DescribePercentile()}; input proxy {input.DescribePercentile()}.";

    private sealed class Samples
    {
        private readonly double[] values = new double[1024];
        private int count, next;
        internal void Add(double value)
        {
            values[next] = value;
            next = (next + 1) % values.Length;
            count = Math.Min(count + 1, values.Length);
        }
        internal void Reset() => count = next = 0;
        internal string DescribePercentile()
        {
            if (count == 0) { return "has no samples"; }
            double[] sorted = values.AsSpan(0, count).ToArray();
            Array.Sort(sorted);
            return string.Create(CultureInfo.InvariantCulture,
                $"p95={sorted[(int)Math.Ceiling(count * 0.95) - 1]:F3} ms, n={count}");
        }
        internal string Describe()
        {
            if (count == 0) { return "no samples"; }
            double[] sorted = values.AsSpan(0, count).ToArray();
            Array.Sort(sorted);
            return string.Create(CultureInfo.InvariantCulture,
                $"n={count}, p50={sorted[(int)Math.Ceiling(count * 0.5) - 1]:F3} ms, p95={sorted[(int)Math.Ceiling(count * 0.95) - 1]:F3} ms, max={sorted[^1]:F3} ms");
        }
    }
}
