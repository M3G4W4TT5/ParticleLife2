using ParticleLife.Core.Models;
using ParticleLife.Core.State;

namespace ParticleLife.Core.Numerics;

/// <summary>Evaluates exact directed forces using packed cell ranges and an explicit CPU worker count.</summary>
/// <remarks>
/// <para>Each call validates and builds the index from the same complete input used for pair and wall
/// forces. A cell has a full signed integer coordinate; each source is visited at most once per receiver.
/// Nonperiodic width is the next power of two at least as large as the maximum directed cutoff;
/// nine cells cover every neighbour. Periodic worlds use an even per-axis tiling with a conservative
/// deduplicated extra ring, and minimum-image displacement. Pair tables retain every directed range.
/// Negative and finite off-world positions, overlaps and unbounded cell occupancy are retained.</para>
/// <para>If any scaled coordinate is non-finite or has magnitude at least 2^50 cell widths, the entire
/// evaluation uses the direct reference search. A non-finite grid-order force sum also retries the
/// entire evaluation in direct storage order: extreme positive/negative forces can cancel in that
/// order while overflowing in cell order. Direct failure still propagates without publishing metadata.
/// Physical state is unchanged and the reason is reported in <see cref="LastExecution"/>. Pair mathematics,
/// overlap policy and directed coefficients are identical to direct evaluation, including tiny/subnormal
/// separations; finite accumulation can differ by roundoff. This fallback does not retry later integration
/// failures or replace finite grid sums with reference sums near an integration overflow boundary.</para>
/// <para>Positions and species are copied into reusable contiguous cell ranges. Within each cell,
/// source storage order is stable; nonperiodic nine-cell query order is unchanged from uniform-grid/1. Workers own
/// disjoint receiver outputs and never split a receiver's sum. Thus worker scheduling does not reorder
/// that sum or assume equal-and-opposite forces. Stable identities and the host state remain in their
/// original order. One worker uses a direct serial loop; larger counts use persistent background
/// threads plus the caller, claiming batches of 32 receivers to balance uneven cell occupancy.</para>
/// <para>The contiguous working copy addresses the scattered-load concern discussed in Páll and Hess
/// (2013), section 2.1, https://doi.org/10.1016/j.cpc.2013.06.003. This implementation remains scalar
/// and does not adopt that paper's clustered SIMD kernel or its workload-specific speedup claims.</para>
/// <para>At stable population, expected cost is O(N + candidate pairs), with quadratic dense cases.
/// After population shrink, clearing retained dictionary capacity adds O(retained capacity) work.
/// Storage is O(established population capacity), independent of world extent. Construction and capacity growth allocate;
/// warmed calls allocate no managed memory on any worker, including rebuilds with changed occupancy.
/// All workers join before output or metadata publishes, including on exceptions and before a direct
/// retry. An instance must not be used concurrently. Input is never mutated, and rejected partial output
/// is not publishable. Dispose releases background threads; disposal must not overlap evaluation.</para>
/// </remarks>
public sealed class UniformGridForceEvaluator : IForceEvaluator, IDisposable
{
    /// <summary>Identifies packed layout and receiver scheduling independently of the saved grid/1 search semantics.</summary>
    public const string AlgorithmVersion = "uniform-grid-packed/2";

    private const int ReceiverChunkSize = 32;
    private ModelParameters parameters;
    private InteractionMatrix matrix;
    private PairForceKernel kernel;
    private readonly UniformGridIndex index;
    private DirectForceEvaluator direct;
    private readonly ForceWorkerPool? workers;
    private readonly RangeResult[] workerResults;
    private Vector2d[] packedPositions = [];
    private int[] packedSpecies = [];
    private Vector2d[] parallelForces = [];
    private bool prepared;
    private bool useGrid;
    private int preparedCount;
    private int nextChunk;
    private int chunkCount;
    private bool disposed;

    /// <summary>Validates and retains immutable model configuration; storage grows with the first input.</summary>
    /// <param name="parameters">Model quantities in reduced units, including a cutoff with positive finite square.</param>
    /// <param name="matrix">Immutable directed coefficients indexed by receiver then source species.</param>
    /// <param name="workerCount">Explicit total CPU workers in [1,64], including the calling thread; one is serial.</param>
    /// <exception cref="ArgumentNullException">A configuration object is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A model parameter or worker count is invalid.</exception>
    public UniformGridForceEvaluator(ModelParameters parameters, InteractionMatrix matrix, int workerCount = 1)
    {
        ForceWorkerConfiguration.Validate(ForceBackend.UniformGrid, workerCount);
        direct = new DirectForceEvaluator(parameters, matrix);
        this.parameters = parameters;
        this.matrix = matrix;
        kernel = new PairForceKernel(parameters, matrix);
        index = new UniformGridIndex(matrix.MaximumCutoff(parameters));
        index.ConfigureBoundary(parameters, matrix.MaximumCutoff(parameters));
        WorkerCount = workerCount;
        workerResults = new RangeResult[workerCount];
        workers = workerCount > 1 ? new ForceWorkerPool(workerCount, EvaluateWorker) : null;
        LastExecution = new(ForceBackend.UniformGrid, ForceBackend.UniformGrid, GridFallbackReason.None,
            IndexStatistics, WorkerCount, WorkerCount);
    }

    /// <summary>Gets the fixed worker count, including the calling thread.</summary>
    public int WorkerCount { get; }

    /// <summary>Changes validated model values while retaining the index, packed buffers and worker pool.</summary>
    /// <remarks>Only the owning simulation calls this at a completed barrier. Prepare replacement
    /// kernels before publishing references, then invalidate derived index/prepared-state data.</remarks>
    internal void Reconfigure(ModelParameters nextParameters, InteractionMatrix nextMatrix)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        PairForceKernel nextKernel = new(nextParameters, nextMatrix);
        DirectForceEvaluator nextDirect = new(nextParameters, nextMatrix);
        index.ReconfigureCutoff(nextMatrix.MaximumCutoff(nextParameters));
        index.ConfigureBoundary(nextParameters, nextMatrix.MaximumCutoff(nextParameters));
        parameters = nextParameters;
        matrix = nextMatrix;
        kernel = nextKernel;
        direct = nextDirect;
        prepared = false;
        LastWorkerAllocatedBytes = 0;
        LastExecution = new(ForceBackend.UniformGrid, ForceBackend.UniformGrid, GridFallbackReason.None,
            IndexStatistics, WorkerCount, WorkerCount);
    }

    /// <summary>Gets background-worker managed bytes from the last successful evaluation; excludes the caller.</summary>
    /// <remarks>Measure the caller separately. Construction, index growth and thread setup are outside this counter.
    /// Serial evaluation and coordinate-range direct fallback report zero; successful warmed workers report zero.</remarks>
    public long LastWorkerAllocatedBytes { get; private set; }

    /// <summary>Gets search/fallback metadata from the last successful force evaluation.</summary>
    /// <remarks>Before evaluation it contains configuration only. Failed evaluations leave it unchanged.</remarks>
    public ForceExecution LastExecution { get; private set; }

    /// <summary>Gets current index capacity and occupancy, including after an internal stage-only rebuild.</summary>
    /// <remarks>This is work-buffer telemetry; a failed simulation step may have changed it without publishing state.
    /// The payload estimate includes 32 bytes per index particle, 36 bytes per dictionary slot,
    /// 20 bytes per packed position/species slot, and 16 bytes per parallel output slot. Worker thread
    /// stacks, events, small result objects, array headers and allocator padding are excluded.</remarks>
    public GridIndexStatistics IndexStatistics => index.Statistics with
    {
        EstimatedRetainedBytes = index.Statistics.EstimatedRetainedBytes + 20L * packedPositions.Length + 16L * parallelForces.Length
    };

    /// <inheritdoc/>
    public ForceEvaluationStatistics Evaluate(ParticleStateView input, Span<Vector2d> forces)
    {
        DirectForceEvaluator.ValidateOutput(input, forces);
        BuildIndex(input);
        return EvaluatePrepared(input, forces);
    }

    /// <summary>Validates and prepares one complete state for stage-separated measurement.</summary>
    /// <remarks>
    /// Internal tests/benchmarks only. The exact same input, including storage order, must remain
    /// unchanged through EvaluatePrepared. No input view is retained; another build replaces all entries.
    /// Public evaluation always performs these stages together, preventing stale-index use by hosts.
    /// </remarks>
    internal void BuildIndex(ParticleStateView input)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        prepared = false;
        DirectForceEvaluator.ValidateState(input, matrix.SpeciesCount);
        useGrid = index.Build(input.Positions);
        preparedCount = input.Count;
        if (useGrid)
        {
            if (input.Count > packedPositions.Length)
            {
                packedPositions = new Vector2d[input.Count];
                packedSpecies = new int[input.Count];
                if (workers is not null)
                {
                    parallelForces = new Vector2d[input.Count];
                }
            }

            ReadOnlySpan<int> sources = index.PackedSources;
            for (int packed = 0; packed < input.Count; packed++)
            {
                int source = sources[packed];
                packedPositions[packed] = input.Positions[source];
                packedSpecies[packed] = input.Species[source];
            }
        }

        prepared = true;
    }

    /// <summary>Evaluates the unchanged input last supplied to BuildIndex, excluding build time.</summary>
    /// <remarks>
    /// The caller must preserve the prepared state's positions and storage order. Output validation
    /// remains timed; input validation belongs to BuildIndex. A direct fallback revalidates its input.
    /// The index may be reused for repeated evaluation of that identical state, never a later timestep.
    /// </remarks>
    internal ForceEvaluationStatistics EvaluatePrepared(ParticleStateView input, Span<Vector2d> forces)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!prepared || input.Count != preparedCount)
        {
            throw new InvalidOperationException("Build an index from the same complete input before prepared evaluation.");
        }

        DirectForceEvaluator.ValidateOutput(input, forces);
        if (!useGrid)
        {
            return EvaluateDirectFallback(input, forces, GridFallbackReason.CoordinateRange, 0);
        }

        RangeResult result;
        long workerAllocated = 0;
        if (workers is null)
        {
            result = EvaluateRange(0, preparedCount, forces);
        }
        else
        {
            nextChunk = 0;
            chunkCount = preparedCount == 0 ? 0 : 1 + (preparedCount - 1) / ReceiverChunkSize;
            workers.Run();
            workerAllocated = workers.LastBackgroundAllocatedBytes;
            result = new(default, true);
            foreach (RangeResult worker in workerResults)
            {
                result = Add(result, worker);
            }
        }

        if (!result.Finite)
        {
            return EvaluateDirectFallback(input, forces, GridFallbackReason.AccumulationRange, workerAllocated);
        }

        if (workers is not null)
        {
            parallelForces.AsSpan(0, preparedCount).CopyTo(forces);
        }

        LastExecution = new(ForceBackend.UniformGrid, ForceBackend.UniformGrid, GridFallbackReason.None,
            IndexStatistics, WorkerCount, WorkerCount);
        LastWorkerAllocatedBytes = workerAllocated;
        return result.Statistics;
    }

    /// <summary>Evaluates whole receiver sums, preserving cell and ascending within-cell source order.</summary>
    /// <remarks>Packing changes data access, not arithmetic. Every ordered pair still evaluates its own
    /// directed coefficient. The output maps back to original storage; ranges never share receivers.</remarks>
    private RangeResult EvaluateRange(int start, int end, Span<Vector2d> forces)
    {
        ReadOnlySpan<int> sourceIndices = index.PackedSources;
        long candidatePairs = 0;
        long acceptedPairs = 0;
        long overlapPairs = 0;
        for (int packedReceiver = start; packedReceiver < end; packedReceiver++)
        {
            int receiver = sourceIndices[packedReceiver];
            Vector2d receiverPosition = packedPositions[packedReceiver];
            Vector2d totalForce = default;
            int row = packedSpecies[packedReceiver] * matrix.SpeciesCount;
            UniformGridIndex.RangeEnumerator ranges = index.CandidateRanges(receiver);
            while (ranges.MoveNext())
            {
                UniformGridIndex.CellRange range = ranges.Current;
                for (int packedSource = range.Start; packedSource < range.End; packedSource++)
                {
                    if (packedSource == packedReceiver)
                    {
                        continue;
                    }

                    candidatePairs++;
                    PairKind kind = kernel.Evaluate(WorldBoundary.Displacement(receiverPosition, packedPositions[packedSource], parameters),
                        row + packedSpecies[packedSource], out Vector2d pairForce);
                    if (kind == PairKind.Accepted)
                    {
                        acceptedPairs++;
                        totalForce += pairForce;
                    }
                    else if (kind == PairKind.Overlap && sourceIndices[packedSource] > receiver)
                    {
                        overlapPairs++;
                    }
                }
            }

            totalForce += SoftWall.EvaluateValidated(receiverPosition, parameters);
            if (!totalForce.IsFinite)
            {
                return new(default, false);
            }

            forces[receiver] = totalForce;
        }

        return new(new(candidatePairs, acceptedPairs, overlapPairs), true);
    }

    /// <summary>Claims small batches to balance mixed-density occupancy without cross-worker force reductions.</summary>
    /// <remarks>The persistent callback owns one result slot. The pool's completion barrier publishes it.
    /// Dynamic receiver tasks address the load-balancing issue discussed by Meyer (2013),
    /// https://doi.org/10.1103/PhysRevE.88.053309; unlike cell-pair tasks, directed receiver ownership needs
    /// no dependency schedule or action-reaction updates. No speedup from that molecular model is assumed.</remarks>
    private void EvaluateWorker(int worker)
    {
        RangeResult result = new(default, true);
        while (true)
        {
            int chunk = Interlocked.Increment(ref nextChunk) - 1;
            if (chunk >= chunkCount)
            {
                break;
            }

            int start = chunk * ReceiverChunkSize;
            int end = start + Math.Min(ReceiverChunkSize, preparedCount - start);
            RangeResult batch = EvaluateRange(start, end, parallelForces);
            result = Add(result, batch);
            if (!result.Finite)
            {
                break;
            }
        }

        workerResults[worker] = result;
    }

    /// <summary>Overwrites every force using direct enumeration, publishing metadata only if the retry succeeds.</summary>
    /// <remarks>Discard all partial grid counts and forces. No exception is caught, and successful fallback allocates nothing.</remarks>
    private ForceEvaluationStatistics EvaluateDirectFallback(ParticleStateView input, Span<Vector2d> forces,
        GridFallbackReason reason, long workerAllocated)
    {
        ForceEvaluationStatistics statistics = direct.Evaluate(input, forces);
        LastExecution = new(ForceBackend.UniformGrid, ForceBackend.Direct, reason, IndexStatistics, WorkerCount, 1);
        LastWorkerAllocatedBytes = workerAllocated;
        return statistics;
    }

    /// <summary>Releases persistent worker threads and events; serial instances own no operating-system resources.</summary>
    /// <remarks>Call only between evaluations. Disposal is idempotent; later evaluation throws ObjectDisposedException.</remarks>
    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            workers?.Dispose();
        }
    }

    /// <summary>Combines independent receiver counts, never force components or partial output.</summary>
    private static RangeResult Add(RangeResult first, RangeResult second) => new(new(
        first.Statistics.CandidatePairs + second.Statistics.CandidatePairs,
        first.Statistics.AcceptedPairs + second.Statistics.AcceptedPairs,
        first.Statistics.OverlapPairs + second.Statistics.OverlapPairs), first.Finite && second.Finite);

    private readonly record struct RangeResult(ForceEvaluationStatistics Statistics, bool Finite);
}
