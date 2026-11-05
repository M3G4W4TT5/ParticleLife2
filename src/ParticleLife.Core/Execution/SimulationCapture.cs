using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Core.Execution;

/// <summary>Owns actual particle values and settings at one explicitly recorded completed boundary.</summary>
/// <remarks>The state is authoritative. AppliedEventCount identifies the position within a same-step
/// event sequence; step alone cannot distinguish captures before and after terminal edits. The host
/// clock remains binary64 even when particle arithmetic is binary32.</remarks>
public sealed class SimulationCapture
{
    /// <summary>Validates an exact capture without running numerical steps or loading a device.</summary>
    /// <param name="configuration">Actual complete settings at the boundary.</param>
    /// <param name="state">Independently owned actual values, time and completed-step count.</param>
    /// <param name="execution">Actual hardware, precision and version provenance.</param>
    /// <param name="appliedEventCount">Number of action-history entries already applied, including same-boundary edits.</param>
    /// <param name="reason">Nonempty capture purpose such as transfer, periodic, terminal or user capture.</param>
    public SimulationCapture(SimulationConfiguration configuration, ParticleSnapshot state,
        ExecutionProvenance execution, int appliedEventCount, string reason)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentOutOfRangeException.ThrowIfNegative(appliedEventCount);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        foreach (int species in state.Species)
        {
            if (species >= configuration.Matrix.SpeciesCount)
            {
                throw new ArgumentException("A captured species is absent from its rule matrix.", nameof(state));
            }
        }
        if (execution.Precision == StatePrecision.Float32 && !CaptureTransfer.IsExactFloat32(state))
        {
            throw new ArgumentException("A float32 capture must contain exact widened binary32 particle values.", nameof(state));
        }
        Configuration = configuration;
        State = state;
        Execution = execution;
        AppliedEventCount = appliedEventCount;
        Reason = reason;
    }

    /// <summary>Gets actual settings at the captured boundary.</summary>
    public SimulationConfiguration Configuration { get; }
    /// <summary>Gets authoritative stored state and clock.</summary>
    public ParticleSnapshot State { get; }
    /// <summary>Gets actual execution provenance.</summary>
    public ExecutionProvenance Execution { get; }
    /// <summary>Gets the number of action events preceding this capture.</summary>
    public int AppliedEventCount { get; }
    /// <summary>Gets the explicit capture purpose.</summary>
    public string Reason { get; }
}
