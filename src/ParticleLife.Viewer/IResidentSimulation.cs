using ParticleLife.Core.Execution;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Viewer;

/// <summary>Identifies a completed numerical boundary without borrowing or downloading particle storage.</summary>
/// <param name="Count">Actual particle membership.</param>
/// <param name="Step">Successfully completed numerical steps.</param>
/// <param name="Time">Accumulated binary64 host clock in reduced time units.</param>
public readonly record struct SimulationBoundary(int Count, long Step, double Time);

/// <summary>Records a small device-reduced observation without claiming CPU connectivity diagnostics.</summary>
/// <param name="Count">Actual particle count at sampling.</param>
/// <param name="MeanSpeed">Mean speed in reduced length per reduced time.</param>
/// <param name="MaximumSpeed">Maximum speed in the same units.</param>
/// <param name="Step">Captured completed boundary.</param>
/// <param name="Time">Actual sample time.</param>
public sealed record ResidentSummary(int Count, double MeanSpeed, double MaximumSpeed, long Step, double Time);

/// <summary>Defines the concrete asynchronous boundary required by resident simulation and explicit capture.</summary>
/// <remarks>The graphics host creates and owns the implementation's thread/context. Calls never hide
/// a full state download. A CPU observation is legal only after an explicit capture of the current
/// boundary. Metadata, rule edits, palette changes and rendering use independent paths. This interface
/// is graphics-free and does not require CPU force buffers or a native device in headless hosts.</remarks>
public interface IResidentSimulation : IDisposable
{
    /// <summary>Gets actual settings for subsequent steps.</summary>
    SimulationConfiguration Configuration { get; }
    /// <summary>Gets last successfully published membership and clock.</summary>
    SimulationBoundary Boundary { get; }
    /// <summary>Gets actual device, numerical precision and implementation version.</summary>
    ExecutionProvenance Execution { get; }
    /// <summary>Gets whether a numerical candidate is still unpublished.</summary>
    bool IsStepPending { get; }
    /// <summary>Gets whether an explicitly requested state copy is still pending.</summary>
    bool IsCapturePending { get; }
    /// <summary>Gets whether the current completed boundary has an owned CPU observation.</summary>
    bool HasCurrentCapture { get; }
    /// <summary>Gets actual species membership, which ordinary integration cannot change.</summary>
    ReadOnlyMemory<int> SpeciesCounts { get; }
    /// <summary>Gets a small sampled observation and its age, or null before a completed sample.</summary>
    ResidentSummary? LatestSummary { get; }
    /// <summary>Gets completed full-state readbacks, distinguishing captures from normal execution.</summary>
    long FullStateReadbacks { get; }
    /// <summary>Gets completed metadata/reduction bytes read, separately from full captures.</summary>
    long MetadataReadbackBytes { get; }

    /// <summary>Begins one fixed step from consistent completed input without waiting for the device.</summary>
    void SubmitStep();
    /// <summary>Polls/submits a bounded amount of work; returns true only when a valid numerical step publishes.</summary>
    /// <returns>True for one newly completed step; false while pending or after a cancellation.</returns>
    bool PollStep();
    /// <summary>Requests abandoning an unpublished candidate after its current device submission completes.</summary>
    void CancelPendingStep();
    /// <summary>Begins an explicit owned copy of the current completed state without a blocking wait.</summary>
    void BeginCapture();
    /// <summary>Polls one explicit copy and returns owned actual values only after its fence completes.</summary>
    /// <returns>A completed capture, or null while the copy is pending.</returns>
    ParticleSnapshot? PollCapture();
    /// <summary>Returns only a previously requested current capture; never initiates a readback.</summary>
    /// <returns>A view whose owner is the independently retained current capture.</returns>
    ParticleStateView ObserveCaptured();
    /// <summary>Applies validated rules and optional structural state only at a completed boundary.</summary>
    /// <param name="configuration">Candidate settings; rejected candidates retain active resources and state.</param>
    /// <param name="replacementState">Optional explicit state with the same time/step as the completed boundary.</param>
    void Reconfigure(SimulationConfiguration configuration, ParticleSnapshot? replacementState = null);
    /// <summary>Replaces the complete state/configuration at an explicitly chosen reset/load boundary.</summary>
    /// <param name="configuration">Complete requested settings.</param>
    /// <param name="state">Actual owned values and clock, already converted with explicit consent if needed.</param>
    void Replace(SimulationConfiguration configuration, ParticleSnapshot state);
    /// <summary>Requests a small speed/count reduction at the current completed boundary.</summary>
    void RequestSummary();
    /// <summary>Polls an outstanding reduction without downloading particles.</summary>
    void PollSummary();
}

/// <summary>Signals that an operation must request explicit state capture before it can inspect particles.</summary>
public sealed class StateCaptureRequiredException : InvalidOperationException
{
    /// <summary>Creates an explicit capture-precondition failure without triggering device work.</summary>
    public StateCaptureRequiredException() : base("This operation requires an explicit capture of the current resident state.") { }
}
