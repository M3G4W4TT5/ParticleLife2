using System.Diagnostics;
using System.Runtime.InteropServices;
using ParticleLife.Viewer;

namespace ParticleLife.MonoGame.Gpu;

/// <summary>Owns reusable observation-only buffers and one source-boundary fence on a borrowed compute API.</summary>
/// <remarks>Neither numerical grid scratch nor ordinary speed-summary storage is reused. The source
/// particle buffer is borrowed only for ordered dispatch: queued reads precede any future numerical
/// reuse. Cancellation deletes the fence but does not cancel native commands; later writes are ordered
/// and deleted storage remains alive until native references finish. No positive CPU wait is issued.</remarks>
internal sealed unsafe class GpuSpatialSampler : IDisposable
{
    private readonly GpuComputeApi api;
    private readonly GpuProgram clear, accumulate, reduce;
    private readonly UniformDoubleVectorCall uniform4d;
    private readonly int rectangleUniform, dimensionsUniform;
    private uint histogram;
    private uint[] summaries = [];
    private int histogramCapacity, particleCapacity;
    private int[] staging = [];
    private uint finalSummary;
    private nint fence;
    private SpatialObservationRequest? request;
    private SimulationBoundary boundary;
    private long started;
    private bool disposed;

    internal bool IsPending => fence != 0;
    internal long RetainedBytes { get; private set; }
    internal long ReadbackBytes { get; private set; }

    internal GpuSpatialSampler(GpuComputeApi api)
    {
        this.api = api;
        uniform4d = api.Gl.Load<UniformDoubleVectorCall>("glUniform4d");
        clear = new(api, GpuSpatialKernels.Clear);
        GpuProgram? candidateAccumulate = null, candidateReduce = null;
        try
        {
            candidateAccumulate = new(api, GpuSpatialKernels.Accumulate);
            candidateReduce = new(api, GpuSpatialKernels.Reduce);
            rectangleUniform = candidateAccumulate.Uniform("rectangle");
            dimensionsUniform = candidateAccumulate.Uniform("dimensions");
            if (rectangleUniform < 0 || dimensionsUniform < 0) { throw new InvalidOperationException("Observation geometry uniforms are unavailable."); }
            accumulate = candidateAccumulate;
            reduce = candidateReduce;
        }
        catch
        {
            candidateReduce?.Dispose();
            candidateAccumulate?.Dispose();
            clear.Dispose();
            throw;
        }
    }

    internal void Begin(uint completedBuffer, SimulationBoundary source, SpatialObservationRequest geometry)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (IsPending) { throw new InvalidOperationException("An observation already owns its source boundary."); }
        started = Stopwatch.GetTimestamp();
        using GpuComputeScope scope = new(api);
        EnsureCapacity(source.Count, geometry.Columns * geometry.Rows + 1);
        int length = geometry.Columns * geometry.Rows + 1;
        Bind(1, histogram);
        clear.Use();
        api.Gl.Uniform1i(clear.Count, length);
        Dispatch(length);
        accumulate.Use();
        Bind(0, completedBuffer);
        Bind(1, histogram);
        Bind(2, summaries[0]);
        api.Gl.Uniform1i(accumulate.Count, source.Count);
        uniform4d(rectangleUniform, geometry.Origin.X, geometry.Origin.Y, geometry.Width, geometry.Height);
        api.Uniform4i(dimensionsUniform, geometry.Columns, geometry.Rows, 0, 0);
        Dispatch(source.Count);
        int groups = Math.Max(1, GpuWorkspace.DivideUp(source.Count, 256));
        int level = 0;
        while (groups > 1)
        {
            reduce.Use();
            Bind(0, summaries[level]);
            Bind(1, summaries[level + 1]);
            api.Gl.Uniform1i(reduce.Count, groups);
            Dispatch(groups);
            groups = GpuWorkspace.DivideUp(groups, 256);
            level++;
        }
        api.MemoryBarrier(GpuComputeApi.BufferUpdateBarrier);
        api.Gl.Check("submitting optional spatial observation");
        finalSummary = summaries[level];
        boundary = source;
        request = geometry;
        fence = api.Fence();
    }

    internal SpatialObservation? Poll()
    {
        if (!IsPending || !api.IsComplete(fence)) { return null; }
        Cancel();
        using GpuComputeScope scope = new(api);
        SpatialObservationRequest geometry = request!;
        int bins = geometry.Columns * geometry.Rows;
        fixed (int* pointer = staging) { Read(histogram, (bins + 1) * sizeof(int), pointer); }
        ObservationSummary summary;
        Read(finalSummary, sizeof(ObservationSummary), &summary);
        ReadbackBytes += (bins + 1L) * sizeof(int) + sizeof(ObservationSummary);
        api.Gl.Check("reading optional spatial observation");
        if (summary.Flags != 0 || summary.Count != boundary.Count)
        {
            throw new InvalidOperationException("The spatial observation returned an invalid speed or membership summary.");
        }
        SpatialObservation observation = new(geometry, staging.AsSpan(0, bins), staging[bins],
            Math.Min(summary.MeanSpeed, summary.MaximumSpeed), summary.MaximumSpeed, boundary.Step, boundary.Time,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        if (observation.Count != boundary.Count) { throw new InvalidOperationException("The spatial histogram did not account for every particle."); }
        return observation;
    }

    private void EnsureCapacity(int particles, int histogramLength)
    {
        if (histogramLength > histogramCapacity)
        {
            int[] candidateStaging = new int[histogramLength];
            uint candidate = api.CreateBuffer(histogramLength * sizeof(int));
            if (histogram != 0) { api.Gl.DeleteBuffers(1, ref histogram); }
            RetainedBytes += (histogramLength - histogramCapacity) * sizeof(int);
            histogram = candidate;
            histogramCapacity = histogramLength;
            staging = candidateStaging;
        }
        if (summaries.Length != 0 && particles <= particleCapacity) { return; }
        // An Int32 population needs at most four successive 256-way summary levels.
        List<uint> candidates = new(4);
        uint[] candidateSummaries;
        long bytes = 0;
        try
        {
            for (int length = Math.Max(1, GpuWorkspace.DivideUp(particles, 256)); ; length = GpuWorkspace.DivideUp(length, 256))
            {
                candidates.Add(api.CreateBuffer(checked((nint)((long)length * sizeof(ObservationSummary)))));
                bytes += (long)length * sizeof(ObservationSummary);
                if (length == 1) { break; }
            }
            candidateSummaries = candidates.ToArray();
        }
        catch
        {
            foreach (uint handle in candidates) { uint buffer = handle; api.Gl.DeleteBuffers(1, ref buffer); }
            throw;
        }
        foreach (uint handle in summaries) { uint buffer = handle; api.Gl.DeleteBuffers(1, ref buffer); }
        summaries = candidateSummaries;
        particleCapacity = particles;
        RetainedBytes = (long)histogramCapacity * sizeof(int) + bytes;
    }

    private void Bind(uint index, uint buffer) => api.BindBufferBase(GpuComputeApi.StorageBuffer, index, buffer);
    private void Dispatch(int count)
    {
        api.DispatchCompute((uint)Math.Max(1, GpuWorkspace.DivideUp(count, 256)), 1, 1);
        api.MemoryBarrier(GpuComputeApi.StorageBarrier);
    }
    private void Read(uint buffer, nint bytes, void* destination)
    {
        api.Gl.BindBuffer(GpuComputeApi.StorageBuffer, buffer);
        api.GetBufferSubData(GpuComputeApi.StorageBuffer, 0, bytes, destination);
    }
    internal void Cancel()
    {
        if (fence != 0 && api.Gl.CanUseOwner) { api.DeleteSync(fence); }
        fence = 0;
    }
    public void Dispose()
    {
        if (disposed) { return; }
        Cancel();
        if (api.Gl.CanUseOwner)
        {
            if (histogram != 0) { api.Gl.DeleteBuffers(1, ref histogram); }
            foreach (uint handle in summaries) { uint buffer = handle; api.Gl.DeleteBuffers(1, ref buffer); }
        }
        clear.Dispose(); accumulate.Dispose(); reduce.Dispose();
        disposed = true;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct ObservationSummary
    {
        internal double MeanSpeed, MaximumSpeed;
        internal uint Count, Flags;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void UniformDoubleVectorCall(int location, double x, double y, double z, double w);
}
