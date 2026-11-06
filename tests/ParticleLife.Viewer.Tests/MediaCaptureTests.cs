using System.Buffers.Binary;
using System.Text;

namespace ParticleLife.Viewer.Tests;

/// <summary>Checks media publication and caller/worker ownership without a graphics device.</summary>
public sealed class MediaCaptureTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "particlelife-media-" + Guid.NewGuid().ToString("N"));

    public MediaCaptureTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void BinaryArtifactsPublishOnlyAfterSuccessfulProductionAndProtectExistingFiles()
    {
        string path = Path.Combine(directory, "capture.bin");
        Assert.Throws<IOException>(() => MediaFile.SaveNew(path, stream =>
        {
            stream.Write([1, 2, 3]);
            Assert.False(File.Exists(path));
            throw new IOException("producer failed");
        }));
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFileSystemEntries(directory));

        MediaFile.SaveNew(path, stream => stream.Write([4, 5, 6]));
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(path));
        bool producerCalled = false;
        Assert.Throws<IOException>(() => MediaFile.SaveNew(path, _ => producerCalled = true));
        Assert.False(producerCalled);
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(directory));
    }

    [Fact]
    public async Task AnimationPublishesCompleteGifAfterFinishAndLeavesCallerBuffersOwnedByCaller()
    {
        const int width = 401, height = 2;
        string path = Path.Combine(directory, "animation.gif");
        AnimationCapture capture = new(path, width, height);
        byte[] first = new byte[width * height * 4];
        byte[] second = new byte[first.Length];
        for (int pixel = 0; pixel < width * height; pixel++)
        {
            first[pixel * 4] = (byte)pixel;
            first[pixel * 4 + 1] = (byte)(pixel >> 1);
            first[pixel * 4 + 2] = (byte)(255 - pixel % 256);
            first[pixel * 4 + 3] = 255;
            second[pixel * 4 + 1] = 255;
            second[pixel * 4 + 3] = 255;
        }
        Assert.True(capture.TryAddFrame(first, 12));
        Assert.True(capture.TryAddFrame(second, 12.1));
        Assert.False(File.Exists(path));
        // Reusing a readback buffer after TryAddFrame must be safe for its caller.
        Array.Fill(first, (byte)37);
        capture.Finish(12.35);
        Assert.False(capture.TryAddFrame(second, 12.4));
        await capture.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(2, capture.WrittenFrames);
        Assert.Equal(0, capture.SkippedFrames);
        Assert.All(first, value => Assert.Equal((byte)37, value));
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal("GIF89a", Encoding.ASCII.GetString(bytes, 0, 6));
        Assert.Equal(width, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6, 2)));
        Assert.Equal(height, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8, 2)));
        Assert.Equal((byte)0x3B, bytes[^1]);
        Assert.Equal(new[] { 10, 25 }, ReadDelays(bytes));
        Assert.Equal(new[] { path }, Directory.GetFiles(directory));
    }

    [Fact]
    public async Task AnimationQuantizesCumulativeTimeWithoutShorteningIrregularFourSecondCapture()
    {
        string path = Path.Combine(directory, "irregular.gif");
        AnimationCapture capture = new(path, 1, 1);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            for (int frame = 0; frame < 39; frame++)
            {
                double timestamp = 1000 + frame * 0.103 + (frame % 2 == 0 ? 0 : 0.001);
                Assert.True(capture.TryAddFrame([192, 128, 64, 255], timestamp));
                // Let the previous image finish before submitting again, so this isolates timestamp
                // quantization without relying on thread timing or intentionally dropping images.
                while (capture.WrittenFrames < frame)
                {
                    if (capture.Completion.IsCompleted) { await capture.Completion; }
                    await Task.Delay(1, deadline.Token);
                }
            }
        }
        finally
        {
            capture.Finish(1004);
            await capture.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        int[] delays = ReadDelays(File.ReadAllBytes(path));
        Assert.Equal(39, delays.Length);
        Assert.Equal(400, delays.Sum());
        Assert.Contains(11, delays);
        Assert.Equal(0, capture.SkippedFrames);
    }

    [Fact]
    public async Task AnimationPreservesUnequalExactCentisecondIntervals()
    {
        string path = Path.Combine(directory, "unequal.gif");
        AnimationCapture capture = new(path, 1, 1);
        Assert.True(capture.TryAddFrame([255, 0, 0, 255], 12));
        Assert.True(capture.TryAddFrame([0, 255, 0, 255], 12.07));
        capture.Finish(12.20);
        await capture.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(new[] { 7, 13 }, ReadDelays(File.ReadAllBytes(path)));
    }

    [Fact]
    public async Task EmptyAnimationFailsWithoutPublishingAndExistingDestinationRemainsIntact()
    {
        string path = Path.Combine(directory, "empty.gif");
        AnimationCapture empty = new(path, 2, 2);
        empty.Finish(1);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await empty.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFileSystemEntries(directory));

        File.WriteAllText(path, "existing artifact");
        AnimationCapture collision = new(path, 2, 2);
        collision.TryAddFrame(new byte[16], 0);
        collision.Finish(0.1);
        await Assert.ThrowsAsync<IOException>(async () => await collision.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.False(collision.TryAddFrame(new byte[16], 0.2));
        Assert.Equal("existing artifact", File.ReadAllText(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(directory));
    }

    [Fact]
    public void GifWriterRetainsStreamOwnershipAndRejectsMismatchedFramesBeforeAppending()
    {
        using MemoryStream stream = new();
        GifAnimationWriter writer = new(stream, 2, 1);
        long headerLength = stream.Length;
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.AppendFrame(new byte[4], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.AppendFrame(new byte[8], 0));
        Assert.Equal(headerLength, stream.Length);
        writer.AppendFrame([255, 0, 0, 255, 0, 0, 255, 255], 25);
        writer.Dispose();
        long completedLength = stream.Length;
        writer.Dispose();
        Assert.Equal(completedLength, stream.Length);
        Assert.True(stream.CanWrite);
        Assert.Equal((byte)0x3B, stream.ToArray()[^1]);
        Assert.Throws<ObjectDisposedException>(() => writer.AppendFrame(new byte[8], 1));
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private static int[] ReadDelays(byte[] bytes)
    {
        List<int> delays = [];
        int offset = 13 + ((bytes[10] & 0x80) != 0 ? 3 * (1 << ((bytes[10] & 7) + 1)) : 0);
        while (bytes[offset] != 0x3B)
        {
            if (bytes[offset] == 0x21)
            {
                if (bytes[offset + 1] == 0xF9)
                { delays.Add(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 4, 2))); }
                offset += 2;
            }
            else
            {
                Assert.Equal(0x2C, bytes[offset]);
                byte packed = bytes[offset + 9];
                offset += 10;
                if ((packed & 0x80) != 0) { offset += 3 * (1 << ((packed & 7) + 1)); }
                offset++; // LZW minimum code size.
            }
            while (bytes[offset] != 0) { offset += 1 + bytes[offset]; }
            offset++;
        }
        return delays.ToArray();
    }
}
