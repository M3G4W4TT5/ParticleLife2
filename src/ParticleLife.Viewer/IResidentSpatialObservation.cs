namespace ParticleLife.Viewer;

/// <summary>Optionally samples a bounded device histogram and speed statistics without capturing particle state.</summary>
/// <remarks>Calls belong to the resident graphics owner. A request owns its immutable rectangle and
/// completed source clock until a zero-timeout fence poll finishes. At 128 by 128 cells the readback is
/// 65,564 bytes: 65,536 histogram bytes, four outside-count bytes and a 24-byte reduced summary. Existing
/// simulation implementations need not implement this optional presentation capability.</remarks>
public interface IResidentSpatialObservation
{
    /// <summary>Gets whether one accepted observation is still pending.</summary>
    bool IsSpatialObservationPending { get; }
    /// <summary>Gets the latest independently owned sample and its actual source clock, or null.</summary>
    SpatialObservation? LatestSpatialObservation { get; }
    /// <summary>Gets a rejected observation's reason without implying that numerical simulation failed.</summary>
    string? SpatialObservationError { get; }
    /// <summary>Gets cumulative histogram/summary payload bytes read, separate from full-state captures.</summary>
    long SpatialObservationReadbackBytes { get; }
    /// <summary>Requests a sample of the current completed buffer without waiting or replacing a pending request.</summary>
    /// <param name="request">Owned immutable display geometry; no physical rule is changed.</param>
    /// <returns>True when accepted; false when busy or unsupported, with failures described by SpatialObservationError.</returns>
    bool TryRequestSpatialObservation(SpatialObservationRequest request);
    /// <summary>Polls without blocking and publishes only a complete owned observation.</summary>
    void PollSpatialObservation();
    /// <summary>Abandons a pending observation without releasing storage still referenced by queued GPU commands early.</summary>
    /// <remarks>OpenGL command ordering and deferred buffer deletion preserve native lifetime. Cancellation
    /// does not erase an already published sample; replacing simulation state invalidates it separately.</remarks>
    void CancelSpatialObservation();
}
