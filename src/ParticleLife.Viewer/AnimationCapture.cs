using System.Buffers;
using System.Threading.Channels;

namespace ParticleLife.Viewer;

/// <summary>Encodes a bounded queue of explicit canvas readbacks on a background writer.</summary>
/// <remarks>At most two queued frames and one pending frame are retained. Backpressure skips an
/// image rather than delaying simulation; actual timestamp gaps become GIF delays. The caller keeps
/// ownership of its readback buffer. GIF delay quantization follows cumulative elapsed time from the
/// first accepted image, so independently rounded frame intervals cannot shorten a recording. GIF's
/// one-centisecond minimum and maximum representable per-frame delay still bound individual intervals.
/// A final artifact is published only after a successful trailer.</remarks>
public sealed class AnimationCapture
{
    private readonly Channel<Frame> frames = Channel.CreateBounded<Frame>(new BoundedChannelOptions(2)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly int byteCount;
    private int written, skipped;
    private double endTimestamp;
    private bool finishing;

    /// <summary>Starts a protected GIF writer; no graphics calls occur on its worker.</summary>
    /// <param name="path">New destination; existing files are protected.</param><param name="width">Frame width.</param>
    /// <param name="height">Frame height.</param>
    public AnimationCapture(string path, int width, int height)
    {
        if (width < 1 || width > 1920 || height < 1 || height > 1920) { throw new ArgumentOutOfRangeException(nameof(width)); }
        Path = System.IO.Path.GetFullPath(path); Width = width; Height = height; byteCount = checked(width * height * 4);
        Completion = Task.Run(() =>
        {
            try { ExclusiveFileStorage.SaveBinary(Path, stream => Encode(stream)); }
            finally
            {
                frames.Writer.TryComplete();
                while (frames.Reader.TryRead(out Frame? abandoned)) { ArrayPool<byte>.Shared.Return(abandoned.Bytes); }
            }
        });
    }
    /// <summary>Gets the protected output path.</summary>
    public string Path { get; }
    /// <summary>Gets fixed frame width.</summary>
    public int Width { get; }
    /// <summary>Gets fixed frame height.</summary>
    public int Height { get; }
    /// <summary>Gets successful encoded frame count.</summary>
    public int WrittenFrames => Volatile.Read(ref written);
    /// <summary>Gets images skipped because the bounded writer queue was full.</summary>
    public int SkippedFrames => Volatile.Read(ref skipped);
    /// <summary>Gets the actual publication result; a fault means no final artifact was published.</summary>
    public Task Completion { get; }

    /// <summary>Copies a frame if capacity is available; rejection never changes simulation policy.</summary>
    /// <param name="rgba">Complete caller-owned readback bytes.</param><param name="timestamp">Monotonic presentation seconds.</param>
    /// <returns>Whether the frame entered the writer queue.</returns>
    public bool TryAddFrame(ReadOnlySpan<byte> rgba, double timestamp)
    {
        if (rgba.Length != byteCount || !double.IsFinite(timestamp)) { throw new ArgumentOutOfRangeException(nameof(rgba)); }
        if (finishing || Completion.IsCompleted) { return false; }
        byte[] copy = ArrayPool<byte>.Shared.Rent(byteCount);
        rgba.CopyTo(copy);
        if (frames.Writer.TryWrite(new Frame(copy, timestamp))) { return true; }
        ArrayPool<byte>.Shared.Return(copy); Interlocked.Increment(ref skipped); return false;
    }

    /// <summary>Finishes queued frames and publishes the animation asynchronously.</summary>
    /// <param name="timestamp">Actual end of the captured presentation interval.</param>
    public void Finish(double timestamp)
    {
        if (!double.IsFinite(timestamp)) { throw new ArgumentOutOfRangeException(nameof(timestamp)); }
        if (finishing) { return; }
        endTimestamp = timestamp; finishing = true; frames.Writer.TryComplete();
    }

    private void Encode(Stream stream)
    {
        Frame? pending = null;
        double? firstTimestamp = null;
        long encodedCentiseconds = 0;
        using GifAnimationWriter gif = new(stream, Width, Height);
        try
        {
            while (frames.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (frames.Reader.TryRead(out Frame? next))
                {
                    if (pending is Frame previous)
                    {
                        try { Write(previous, next.Timestamp); }
                        catch { ArrayPool<byte>.Shared.Return(next.Bytes); throw; }
                        ArrayPool<byte>.Shared.Return(previous.Bytes);
                    }
                    pending = next;
                }
            }
            if (pending is Frame last) { Write(last, endTimestamp); }
            if (written == 0) { throw new InvalidOperationException("No animation frames were captured."); }
        }
        finally
        {
            if (pending is Frame last) { ArrayPool<byte>.Shared.Return(last.Bytes); }
            frames.Writer.TryComplete();
            while (frames.Reader.TryRead(out Frame? abandoned)) { ArrayPool<byte>.Shared.Return(abandoned.Bytes); }
        }
        void Write(Frame frame, double until)
        {
            firstTimestamp ??= frame.Timestamp;
            double elapsedCentiseconds = Math.Round(Math.Max(0, until - firstTimestamp.Value) * 100);
            int delay = (int)Math.Clamp(elapsedCentiseconds - encodedCentiseconds, 1, ushort.MaxValue);
            gif.AppendFrame(frame.Bytes.AsSpan(0, byteCount), delay); Interlocked.Increment(ref written);
            encodedCentiseconds += delay;
        }
    }
    private sealed record Frame(byte[] Bytes, double Timestamp);
}

/// <summary>Publishes binary media through the same exclusive reservation used by scientific saves.</summary>
public static class MediaFile
{
    /// <summary>Writes a new artifact atomically after the supplied producer completes.</summary>
    /// <param name="path">New file path.</param><param name="write">Producer called outside numerical stepping.</param>
    public static void SaveNew(string path, Action<Stream> write)
    {
        ArgumentNullException.ThrowIfNull(write); ExclusiveFileStorage.SaveBinary(path, write);
    }
}
