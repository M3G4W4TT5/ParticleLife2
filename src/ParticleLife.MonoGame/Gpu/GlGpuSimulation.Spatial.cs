using ParticleLife.Viewer;

namespace ParticleLife.MonoGame.Gpu;

internal sealed unsafe partial class GlGpuSimulation
{
    private GpuSpatialSampler? spatialSampler;

    public bool IsSpatialObservationPending => spatialSampler?.IsPending == true;
    public SpatialObservation? LatestSpatialObservation { get; private set; }
    public string? SpatialObservationError { get; private set; }
    public long SpatialObservationReadbackBytes => spatialSampler?.ReadbackBytes ?? 0;

    /// <summary>Accepts optional observation work independently of a pending numerical candidate.</summary>
    /// <remarks>The completed input buffer remains unchanged during its read dispatch. Command ordering
    /// protects it even if the simulation publishes another boundary before this request is polled.
    /// Normal binary64 rectangle lengths and zero/normal origins are supported; subnormal inputs are rejected explicitly
    /// because GLSL arithmetic may flush them. Such observation failure never pauses simulation.</remarks>
    public bool TryRequestSpatialObservation(SpatialObservationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (IsSpatialObservationPending) { return false; }
        try
        {
            EnsureOwner();
            request.Validate();
            if (!double.IsNormal(request.Width) || !double.IsNormal(request.Height)
                || double.IsSubnormal(request.Origin.X) || double.IsSubnormal(request.Origin.Y))
            {
                throw new NotSupportedException("GPU observation rectangle lengths must be normal positive binary64 values and origins must be zero or normal; choose a larger display region.");
            }
            spatialSampler ??= new(api);
            spatialSampler.Begin(workspace.Input, Boundary, request);
            SpatialObservationError = null;
            return true;
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or NotSupportedException or OutOfMemoryException)
        {
            spatialSampler?.Cancel();
            SpatialObservationError = failure.Message;
            return false;
        }
    }

    public void PollSpatialObservation()
    {
        if (!IsSpatialObservationPending) { return; }
        long before = spatialSampler!.ReadbackBytes;
        try
        {
            EnsureOwner();
            if (spatialSampler.Poll() is { } sample)
            {
                LatestSpatialObservation = sample;
                SpatialObservationError = null;
            }
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or NotSupportedException or OutOfMemoryException)
        {
            spatialSampler.Cancel();
            SpatialObservationError = failure.Message;
        }
        finally { MetadataReadbackBytes += spatialSampler.ReadbackBytes - before; }
    }

    public void CancelSpatialObservation() => spatialSampler?.Cancel();

    private void InvalidateSpatialObservation()
    {
        CancelSpatialObservation();
        LatestSpatialObservation = null;
        SpatialObservationError = null;
    }
}
