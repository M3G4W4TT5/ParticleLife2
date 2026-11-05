using System.Buffers.Binary;
using System.IO.Compression;

namespace ParticleLife.Core.State;

/// <summary>Stores an immutable exact state as independently compressed chunks for undo checkpoints.</summary>
/// <remarks>Each chunk contains at most 4096 fixed 44-byte records: Int64 identity, four binary64
/// components and Int32 species, all little-endian. Brotli Fastest is used only when smaller than the
/// raw chunk. Time and step remain exact scalar metadata. Packing/restoring allocates and belongs to
/// explicit editing/capture operations; no decompression occurs during normal numerical advancement.
/// This in-memory representation has no external untrusted decoder or platform-dependent struct layout.</remarks>
public sealed class CompressedParticleSnapshot
{
    private const int ChunkParticles = 4096;
    private const int RecordBytes = 44;
    private readonly Chunk[] chunks;

    private CompressedParticleSnapshot(ParticleSnapshot state, Chunk[] chunks)
    {
        Count = state.Count; Time = state.Time; Step = state.Step;
        this.chunks = chunks;
        StorageBytes = chunks.Sum(chunk => (long)chunk.Bytes.Length);
    }

    /// <summary>Gets the exact stored population.</summary>
    public int Count { get; }
    /// <summary>Gets the stored simulation time.</summary>
    public double Time { get; }
    /// <summary>Gets the stored completed-step count.</summary>
    public long Step { get; }
    /// <summary>Gets retained compressed/raw payload bytes, excluding small array/object headers.</summary>
    public long StorageBytes { get; }

    /// <summary>Copies actual bit patterns into bounded independently compressed chunks.</summary>
    /// <param name="state">Immutable source; its arrays are not retained.</param>
    /// <returns>An independently owned exact checkpoint.</returns>
    public static CompressedParticleSnapshot Pack(ParticleSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        int count = state.Count == 0 ? 0 : 1 + (state.Count - 1) / ChunkParticles;
        Chunk[] chunks = new Chunk[count];
        byte[] scratch = new byte[Math.Min(state.Count, ChunkParticles) * RecordBytes];
        for (int chunkIndex = 0; chunkIndex < count; chunkIndex++)
        {
            int start = chunkIndex * ChunkParticles;
            int particles = Math.Min(ChunkParticles, state.Count - start);
            int length = particles * RecordBytes;
            for (int i = 0; i < particles; i++)
            {
                int source = start + i;
                Span<byte> record = scratch.AsSpan(i * RecordBytes, RecordBytes);
                BinaryPrimitives.WriteInt64LittleEndian(record, state.Ids[source]);
                BinaryPrimitives.WriteDoubleLittleEndian(record[8..], state.Positions[source].X);
                BinaryPrimitives.WriteDoubleLittleEndian(record[16..], state.Positions[source].Y);
                BinaryPrimitives.WriteDoubleLittleEndian(record[24..], state.Velocities[source].X);
                BinaryPrimitives.WriteDoubleLittleEndian(record[32..], state.Velocities[source].Y);
                BinaryPrimitives.WriteInt32LittleEndian(record[40..], state.Species[source]);
            }
            using MemoryStream output = new();
            using (BrotliStream compressor = new(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                compressor.Write(scratch, 0, length);
            }
            chunks[chunkIndex] = output.Length < length
                ? new(output.ToArray(), true, particles) : new(scratch.AsSpan(0, length).ToArray(), false, particles);
        }
        return new(state, chunks);
    }

    /// <summary>Restores all actual values and their authoritative storage order.</summary>
    /// <returns>A separately owned immutable state; repeated restoration never mutates this checkpoint.</returns>
    public ParticleSnapshot Restore()
    {
        long[] ids = new long[Count];
        Vector2d[] positions = new Vector2d[Count], velocities = new Vector2d[Count];
        int[] species = new int[Count];
        byte[] scratch = new byte[Math.Min(Count, ChunkParticles) * RecordBytes];
        int destination = 0;
        foreach (Chunk chunk in chunks)
        {
            int length = chunk.Particles * RecordBytes;
            ReadOnlySpan<byte> data;
            if (chunk.Compressed)
            {
                using MemoryStream input = new(chunk.Bytes, writable: false);
                using BrotliStream decompressor = new(input, CompressionMode.Decompress);
                decompressor.ReadExactly(scratch.AsSpan(0, length));
                if (decompressor.ReadByte() != -1) { throw new InvalidDataException("Checkpoint chunk has unexpected trailing data."); }
                data = scratch.AsSpan(0, length);
            }
            else { data = chunk.Bytes; }
            for (int i = 0; i < chunk.Particles; i++)
            {
                ReadOnlySpan<byte> record = data.Slice(i * RecordBytes, RecordBytes);
                ids[destination] = BinaryPrimitives.ReadInt64LittleEndian(record);
                positions[destination] = new(BinaryPrimitives.ReadDoubleLittleEndian(record[8..]), BinaryPrimitives.ReadDoubleLittleEndian(record[16..]));
                velocities[destination] = new(BinaryPrimitives.ReadDoubleLittleEndian(record[24..]), BinaryPrimitives.ReadDoubleLittleEndian(record[32..]));
                species[destination] = BinaryPrimitives.ReadInt32LittleEndian(record[40..]);
                destination++;
            }
        }
        return new(ids, positions, velocities, species, Time, Step);
    }

    private sealed record Chunk(byte[] Bytes, bool Compressed, int Particles);
}
