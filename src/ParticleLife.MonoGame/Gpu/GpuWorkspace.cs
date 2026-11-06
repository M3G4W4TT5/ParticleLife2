using System.Numerics;

namespace ParticleLife.MonoGame.Gpu;

/// <summary>Owns capacity-dependent resident state, exact grid indices, hierarchical scratch and explicit-copy staging.</summary>
/// <remarks>Construction is transactional: the active workspace is replaced only after every candidate
/// allocation succeeds. Counts are never capped; a larger bucket merely needs more bounded force chunks.
/// Scan levels contain ceil(previous/512) entries. Reduction levels contain ceil(previous/256) records.
/// Empty populations still allocate one record so native zero-sized storage never has special ownership.</remarks>
internal sealed unsafe class GpuWorkspace : IDisposable
{
    private readonly GpuComputeApi api;
    private readonly List<uint> owned = [];
    internal uint Input, Output;
    internal readonly uint Counts, Offsets, Indices, Cells, Status, Staging;
    internal readonly int Count, BucketCount;
    internal readonly uint[] ScanSums, ScanOffsets, Summaries;
    internal readonly int[] ScanCounts, SummaryCounts;
    internal long RetainedBytes { get; private set; }

    internal GpuWorkspace(GpuComputeApi api, ReadOnlySpan<GpuParticle> particles)
    {
        this.api = api;
        Count = particles.Length;
        if ((long)Count > (long)api.MaximumDispatchGroups * 256)
        {
            throw new NotSupportedException("This device cannot dispatch the requested particle count in a single indexing stage.");
        }
        long requestedBuckets = Math.Max(256L, (long)Count * 2);
        if (requestedBuckets > 1 << 29)
        {
            throw new NotSupportedException("The requested population exceeds the GPU grid's supported indexing range.");
        }
        BucketCount = (int)BitOperations.RoundUpToPowerOf2((uint)requestedBuckets);
        if ((long)BucketCount > (long)api.MaximumDispatchGroups * 256)
        {
            throw new NotSupportedException("This device cannot dispatch the requested hash-table size.");
        }
        try
        {
            fixed (GpuParticle* pointer = particles)
            {
                Input = Allocate(Math.Max(1L, Count) * sizeof(GpuParticle), Count == 0 ? null : pointer);
            }
            Output = Allocate(Math.Max(1L, Count) * sizeof(GpuParticle));
            Counts = Allocate((long)BucketCount * sizeof(uint));
            Offsets = Allocate((long)BucketCount * sizeof(uint));
            Indices = Allocate(Math.Max(1L, Count) * sizeof(uint));
            Cells = Allocate(Math.Max(1L, Count) * 2 * sizeof(int));
            Status = Allocate(16);
            Staging = Allocate(Math.Max(1L, Count) * sizeof(GpuParticle));
            List<uint> sums = [], offsets = [];
            List<int> scanCounts = [];
            for (int length = BucketCount; ; length = DivideUp(length, 512))
            {
                scanCounts.Add(length);
                sums.Add(Allocate((long)DivideUp(length, 512) * sizeof(uint)));
                offsets.Add(length == BucketCount ? Offsets : Allocate((long)length * sizeof(uint)));
                if (length <= 512)
                {
                    break;
                }
            }
            ScanSums = sums.ToArray();
            ScanOffsets = offsets.ToArray();
            ScanCounts = scanCounts.ToArray();
            List<uint> summaries = [];
            List<int> summaryCounts = [];
            for (int length = Math.Max(1, DivideUp(Count, 256)); ; length = DivideUp(length, 256))
            {
                summaries.Add(Allocate((long)length * 16));
                summaryCounts.Add(length);
                if (length == 1)
                {
                    break;
                }
            }
            Summaries = summaries.ToArray();
            SummaryCounts = summaryCounts.ToArray();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private uint Allocate(long bytes, void* data = null)
    {
        uint buffer = api.CreateBuffer(checked((nint)bytes), data);
        owned.Add(buffer);
        RetainedBytes += bytes;
        return buffer;
    }

    internal static int DivideUp(int value, int divisor) => value / divisor + (value % divisor == 0 ? 0 : 1);

    public void Dispose()
    {
        if (api.Gl.CanUseOwner)
        {
            foreach (uint handle in owned)
            {
                uint buffer = handle;
                api.Gl.DeleteBuffers(1, ref buffer);
            }
        }
        owned.Clear();
    }
}
