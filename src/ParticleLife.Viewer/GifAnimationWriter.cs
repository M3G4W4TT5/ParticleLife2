using System.Buffers.Binary;
using System.Text;

namespace ParticleLife.Viewer;

/// <summary>Writes bounded full-frame GIF89a animation with a fixed RGB 3-3-2 palette.</summary>
/// <remarks>Uses nine-bit LZW literal blocks, clearing the dictionary every 200 pixels to keep the
/// code width fixed. This deliberately favours bounded encoding work over file compression. Frames
/// carry their actual presentation delay, independent of numerical steps. Format: GIF89a sections
/// 18–23 and Appendix F, https://www.w3.org/Graphics/GIF/spec-gif89a.txt . The stream remains caller-owned.</remarks>
public sealed class GifAnimationWriter : IDisposable
{
    private readonly Stream stream;
    private readonly int width, height;
    private readonly byte[] block = new byte[255];
    private int blockLength, bitCount;
    private uint bits;
    private bool disposed;

    /// <summary>Writes an infinitely looping animation header and palette to a writable stream.</summary>
    /// <param name="stream">Caller-owned writable output.</param><param name="width">Width from 1 through 1920.</param>
    /// <param name="height">Height from 1 through 1920.</param>
    public GifAnimationWriter(Stream stream, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite || width < 1 || width > 1920 || height < 1 || height > 1920)
        { throw new ArgumentOutOfRangeException(nameof(width), "GIF dimensions must be 1..1920 and the stream writable."); }
        this.stream = stream; this.width = width; this.height = height;
        stream.Write("GIF89a"u8); Write16(width); Write16(height);
        stream.Write([0xF7, 0, 0]);
        for (int i = 0; i < 256; i++)
        { stream.WriteByte((byte)((i >> 5) * 255 / 7)); stream.WriteByte((byte)(((i >> 2) & 7) * 255 / 7)); stream.WriteByte((byte)((i & 3) * 255 / 3)); }
        stream.Write([0x21, 0xFF, 11]); stream.Write(Encoding.ASCII.GetBytes("NETSCAPE2.0"));
        stream.Write([3, 1, 0, 0, 0]);
    }

    /// <summary>Appends a complete opaque frame, quantizing RGB while preserving its supplied delay.</summary>
    /// <param name="rgba">Exactly width times height RGBA bytes; alpha is ignored for opaque canvas captures.</param>
    /// <param name="delayCentiseconds">Display delay from one through 65,535 hundredths of a second.</param>
    public void AppendFrame(ReadOnlySpan<byte> rgba, int delayCentiseconds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (rgba.Length != checked(width * height * 4) || delayCentiseconds < 1 || delayCentiseconds > ushort.MaxValue)
        { throw new ArgumentOutOfRangeException(nameof(rgba), "Frame dimensions or display delay are invalid."); }
        stream.Write([0x21, 0xF9, 4, 4]); Write16(delayCentiseconds); stream.Write([0, 0]);
        stream.WriteByte(0x2C); Write16(0); Write16(0); Write16(width); Write16(height); stream.Write([0, 8]);
        blockLength = bitCount = 0; bits = 0;
        Code(256);
        int literals = 0;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            if (literals == 200) { Code(256); literals = 0; }
            Code((rgba[i] & 0xE0) | ((rgba[i + 1] & 0xE0) >> 3) | (rgba[i + 2] >> 6));
            literals++;
        }
        Code(257);
        if (bitCount > 0) { Byte((byte)bits); }
        FlushBlock(); stream.WriteByte(0);
    }

    private void Code(int code)
    {
        bits |= (uint)code << bitCount; bitCount += 9;
        while (bitCount >= 8) { Byte((byte)bits); bits >>= 8; bitCount -= 8; }
    }
    private void Byte(byte value)
    {
        block[blockLength++] = value;
        if (blockLength == block.Length) { FlushBlock(); }
    }
    private void FlushBlock()
    {
        if (blockLength == 0) { return; }
        stream.WriteByte((byte)blockLength); stream.Write(block, 0, blockLength); blockLength = 0;
    }
    private void Write16(int value)
    {
        Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)value); stream.Write(bytes);
    }
    /// <summary>Writes the trailer once without closing the caller's stream.</summary>
    public void Dispose()
    {
        if (!disposed) { stream.WriteByte(0x3B); disposed = true; }
    }
}
