using ParticleLife.Core.Numerics;

namespace ParticleLife.Core.Experiments;

/// <summary>Explicit execution-only overrides applied consistently to initial and edited session settings.</summary>
/// <remarks>Overrides never mutate the source session. Hosts must report these values alongside source
/// settings and actual execution. Switching a recorded backend without a worker override selects one
/// worker, matching experiment override semantics; retaining a backend retains its saved worker count.</remarks>
public sealed class ReplayOverrides
{
    /// <summary>Validates explicit execution selection, leaving unspecified settings recorded.</summary>
    /// <param name="backend">Optional neighbour-search override.</param>
    /// <param name="workerCount">Optional worker count override, including the caller.</param>
    /// <exception cref="ArgumentOutOfRangeException">The backend or explicit worker selection is invalid.</exception>
    public ReplayOverrides(ForceBackend? backend = null, int? workerCount = null)
    {
        if (backend is ForceBackend requested && !Enum.IsDefined(requested))
        {
            throw new ArgumentOutOfRangeException(nameof(backend));
        }

        if (workerCount is int workers)
        {
            ForceWorkerConfiguration.Validate(backend ?? ForceBackend.UniformGrid, workers);
        }

        Backend = backend;
        WorkerCount = workerCount;
    }

    /// <summary>Gets the explicitly requested backend override, or null.</summary>
    public ForceBackend? Backend { get; }

    /// <summary>Gets the explicitly requested worker override, or null.</summary>
    public int? WorkerCount { get; }

    /// <summary>Resolves one recorded configuration while preserving all physical and timestep settings.</summary>
    /// <param name="recorded">The authoritative configuration stored in the session.</param>
    /// <returns>The original object when effective values match, otherwise a validated replacement.</returns>
    /// <exception cref="ArgumentException">The effective backend/worker pair is invalid.</exception>
    public SimulationConfiguration Apply(SimulationConfiguration recorded)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        ForceBackend effectiveBackend = Backend ?? recorded.Backend;
        int effectiveWorkers = WorkerCount ?? (effectiveBackend == recorded.Backend ? recorded.WorkerCount : 1);
        return effectiveBackend == recorded.Backend && effectiveWorkers == recorded.WorkerCount ? recorded
            : new(recorded.Parameters, recorded.Matrix, recorded.TimeStep, effectiveBackend, effectiveWorkers);
    }
}
