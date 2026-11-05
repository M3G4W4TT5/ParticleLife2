namespace ParticleLife.Core.Numerics;

/// <summary>Selects exact neighbour discovery for the same double-precision force model and integrator.</summary>
public enum ForceBackend
{
    /// <summary>Enumerates every ordered non-self pair in storage order.</summary>
    Direct,

    /// <summary>Enumerates local cells, with direct fallback for coordinate range or non-finite force accumulation.</summary>
    UniformGrid
}

/// <summary>Explains why an evaluation requested with a grid used direct enumeration instead.</summary>
public enum GridFallbackReason
{
    /// <summary>No fallback was needed.</summary>
    None,

    /// <summary>At least one position divided by the cell width lies outside the documented indexing range.</summary>
    CoordinateRange,

    /// <summary>A grid-order force sum was non-finite, requiring a retry in the reference storage order.</summary>
    AccumulationRange
}

/// <summary>Describes reusable index storage after construction or a completed evaluation.</summary>
/// <param name="ParticleCapacity">Number of particle entries retained in each packed-index array.</param>
/// <param name="CellCapacity">Dictionary capacity reserved for distinct occupied cells.</param>
/// <param name="OccupiedCells">Number of occupied cells, or zero after a coordinate fallback.</param>
/// <param name="EstimatedRetainedBytes">Estimated array payload bytes, excluding object headers and allocator padding.</param>
/// <param name="CellWidth">Power-of-two cell width in reduced length units, or zero for a direct evaluator.</param>
/// <remarks>
/// The packed index uses 32 bytes per retained particle (cell coordinates, cell IDs, range starts/ends,
/// and source mapping), plus 36 bytes per dictionary slot in the current 64-bit .NET layout.
/// Evaluator telemetry additionally includes 20 bytes per packed position/species and, for parallel
/// evaluation, 16 bytes per retained output force. Thread stacks, events and object headers are excluded.
/// It is an implementation-dependent estimate, not a process-memory or allocated-byte measurement.
/// Capacity is retained across smaller populations and changing occupancy; it grows only with population.
/// </remarks>
public readonly record struct GridIndexStatistics(int ParticleCapacity, int CellCapacity, int OccupiedCells,
    long EstimatedRetainedBytes, double CellWidth);

/// <summary>Records the requested and actual neighbour search used by a successful force evaluation.</summary>
/// <param name="RequestedBackend">The configured neighbour-search implementation.</param>
/// <param name="ActualBackend">The implementation that evaluated all pairs in this call.</param>
/// <param name="FallbackReason">Reason for direct fallback, or None.</param>
/// <param name="Index">Grid occupancy and retained capacity for this input, or zero values for direct search.</param>
/// <param name="ConfiguredWorkerCount">Explicit receiver-worker count, including the calling thread.</param>
/// <param name="ActualWorkerCount">Workers used for the successful evaluation; a direct fallback uses one.</param>
/// <remarks>This describes search only; neither fallback nor backend selection changes model equations or integration.</remarks>
public readonly record struct ForceExecution(ForceBackend RequestedBackend, ForceBackend ActualBackend,
    GridFallbackReason FallbackReason, GridIndexStatistics Index, int ConfiguredWorkerCount = 1, int ActualWorkerCount = 1);

/// <summary>Validates the bounded, explicit CPU configuration independently of machine timing.</summary>
public static class ForceWorkerConfiguration
{
    /// <summary>Gets the maximum supported workers, including the calling thread.</summary>
    public const int MaximumWorkers = 64;

    /// <summary>Requires 1..64 workers for grid evaluation and exactly one for the direct reference.</summary>
    /// <param name="backend">Exact neighbour search.</param>
    /// <param name="workerCount">Requested workers; never chosen automatically from timings.</param>
    /// <exception cref="ArgumentOutOfRangeException">The backend or worker count is unsupported.</exception>
    public static void Validate(ForceBackend backend, int workerCount)
    {
        if (!Enum.IsDefined(backend) || workerCount < 1 || workerCount > MaximumWorkers
            || (backend == ForceBackend.Direct && workerCount != 1))
        {
            throw new ArgumentOutOfRangeException(nameof(workerCount), "Use 1..64 grid workers or one direct worker.");
        }
    }
}
