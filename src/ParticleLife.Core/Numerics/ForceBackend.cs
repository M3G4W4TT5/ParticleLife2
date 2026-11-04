namespace ParticleLife.Core.Numerics;

/// <summary>Selects exact neighbour discovery for the same double-precision force model and integrator.</summary>
public enum ForceBackend
{
    /// <summary>Enumerates every ordered non-self pair in storage order.</summary>
    Direct,

    /// <summary>Enumerates local cells, with a direct fallback for coordinates outside reliable grid indexing.</summary>
    UniformGrid
}

/// <summary>Explains why an evaluation requested with a grid used direct enumeration instead.</summary>
public enum GridFallbackReason
{
    /// <summary>No fallback was needed.</summary>
    None,

    /// <summary>At least one position divided by the cell width lies outside the documented indexing range.</summary>
    CoordinateRange
}

/// <summary>Describes reusable index storage after construction or a completed evaluation.</summary>
/// <param name="ParticleCapacity">Number of particle links and cell coordinates retained.</param>
/// <param name="CellCapacity">Dictionary capacity reserved for distinct occupied cells.</param>
/// <param name="OccupiedCells">Number of occupied cells, or zero after a coordinate fallback.</param>
/// <param name="EstimatedRetainedBytes">Estimated array payload bytes, excluding object headers and allocator padding.</param>
/// <param name="CellWidth">Power-of-two cell width in reduced length units, or zero for a direct evaluator.</param>
/// <remarks>
/// The estimate is 20 bytes per retained particle (two Int64 coordinates and an Int32 link), plus
/// 36 bytes per dictionary slot (a 32-byte entry and 4-byte bucket in the current 64-bit .NET layout).
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
/// <remarks>This describes search only; neither fallback nor backend selection changes model equations or integration.</remarks>
public readonly record struct ForceExecution(ForceBackend RequestedBackend, ForceBackend ActualBackend,
    GridFallbackReason FallbackReason, GridIndexStatistics Index);
