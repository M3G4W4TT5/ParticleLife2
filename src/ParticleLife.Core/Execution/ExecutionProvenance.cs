using System.Runtime.InteropServices;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;

namespace ParticleLife.Core.Execution;

/// <summary>Identifies execution hardware independently of neighbour search and state precision.</summary>
public enum ExecutionHardware
{
    /// <summary>Uses the graphics-independent host processor implementation.</summary>
    Cpu,
    /// <summary>Uses a selected device implementation with resident state.</summary>
    Gpu
}

/// <summary>Identifies the precision of stored particle positions and velocities, not the host clock.</summary>
public enum StatePrecision
{
    /// <summary>Uses IEEE 754 binary64 particle components.</summary>
    Float64,
    /// <summary>Uses IEEE 754 binary32 components, widened exactly when represented by a snapshot.</summary>
    Float32
}

/// <summary>Records the actual implementation and device meaning of a completed state.</summary>
/// <remarks>Restoring captured values and repeating actions are different promises. A device/backend
/// identifier is provenance, not a claim of identical trajectories on another driver or machine.</remarks>
public sealed record ExecutionProvenance
{
    /// <summary>Validates a concrete execution description without loading any device library.</summary>
    /// <param name="hardware">Actual processor class.</param>
    /// <param name="precision">Actual particle arithmetic/storage precision.</param>
    /// <param name="implementation">Versioned implementation identifier.</param>
    /// <param name="runtime">Runtime/compiler provenance.</param>
    /// <param name="device">Device name, or null for an ordinary CPU configuration.</param>
    /// <param name="api">Device API/version, or null for CPU.</param>
    /// <param name="driver">Driver description, or null when unavailable.</param>
    /// <param name="repeatableActions">Whether this implementation promises repeatable action replay on its tested configuration.</param>
    public ExecutionProvenance(ExecutionHardware hardware, StatePrecision precision, string implementation,
        string runtime, string? device, string? api, string? driver, bool repeatableActions)
    {
        if (!Enum.IsDefined(hardware) || !Enum.IsDefined(precision))
        {
            throw new ArgumentOutOfRangeException(nameof(hardware), "Unknown execution hardware or precision.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(implementation);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtime);
        if (hardware == ExecutionHardware.Gpu)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(device);
            ArgumentException.ThrowIfNullOrWhiteSpace(api);
        }
        Hardware = hardware;
        Precision = precision;
        Implementation = implementation;
        Runtime = runtime;
        Device = device;
        Api = api;
        Driver = driver;
        RepeatableActions = repeatableActions;
    }

    /// <summary>Gets actual execution hardware.</summary>
    public ExecutionHardware Hardware { get; }
    /// <summary>Gets actual particle precision.</summary>
    public StatePrecision Precision { get; }
    /// <summary>Gets the implementation and version.</summary>
    public string Implementation { get; }
    /// <summary>Gets runtime/compiler provenance.</summary>
    public string Runtime { get; }
    /// <summary>Gets the device name when applicable.</summary>
    public string? Device { get; }
    /// <summary>Gets the device API and version when applicable.</summary>
    public string? Api { get; }
    /// <summary>Gets driver provenance when available.</summary>
    public string? Driver { get; }
    /// <summary>Gets the supported repeatability promise for action replay on the recorded configuration.</summary>
    public bool RepeatableActions { get; }

    /// <summary>Describes the retained double-precision CPU implementation.</summary>
    /// <param name="configuration">Actual requested CPU search settings.</param>
    /// <returns>A graphics-free CPU execution description.</returns>
    public static ExecutionProvenance Cpu(SimulationConfiguration configuration) => new(ExecutionHardware.Cpu,
        StatePrecision.Float64, ExperimentFile.GetForceAlgorithmVersion(configuration.Backend),
        RuntimeInformation.FrameworkDescription, null, null, null, true);
}
