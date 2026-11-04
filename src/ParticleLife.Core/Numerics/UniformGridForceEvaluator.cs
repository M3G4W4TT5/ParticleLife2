using ParticleLife.Core.Models;
using ParticleLife.Core.State;

namespace ParticleLife.Core.Numerics;

/// <summary>Evaluates exact directed forces with a serial, reusable uniform-grid neighbour search.</summary>
/// <remarks>
/// <para>Each call validates and builds the index from the same complete input used for pair and wall
/// forces. A cell has a full signed integer coordinate; each source is visited at most once per receiver.
/// Width is the next power of two at least as large as the cutoff; nine cells cover every neighbour.
/// Negative and finite off-world positions, overlaps and unbounded cell occupancy are retained.</para>
/// <para>If any scaled coordinate is non-finite or has magnitude at least 2^50 cell widths, the entire
/// evaluation uses the direct reference search. A non-finite grid-order force sum also retries the
/// entire evaluation in direct storage order: extreme positive/negative forces can cancel in that
/// order while overflowing in cell order. Direct failure still propagates without publishing metadata.
/// Physical state is unchanged and the reason is reported in <see cref="LastExecution"/>. Pair mathematics,
/// overlap policy and directed coefficients are identical to direct evaluation, including tiny/subnormal
/// separations; finite accumulation can differ by roundoff. This fallback does not retry later integration
/// failures or replace finite grid sums with reference sums near an integration overflow boundary.</para>
/// <para>At stable population, expected cost is O(N + candidate pairs), with quadratic dense cases.
/// After population shrink, clearing retained dictionary capacity adds O(retained capacity) work.
/// Storage is O(established population capacity), independent of world extent. Construction and capacity growth allocate;
/// warmed calls allocate no managed memory, including rebuilds with changed occupancy. An instance
/// must not be used concurrently. Input is never mutated, and rejected partial output is not publishable.</para>
/// </remarks>
public sealed class UniformGridForceEvaluator : IForceEvaluator
{
    private readonly ModelParameters parameters;
    private readonly InteractionMatrix matrix;
    private readonly PairForceKernel kernel;
    private readonly UniformGridIndex index;
    private readonly DirectForceEvaluator direct;
    private bool prepared;
    private bool useGrid;
    private int preparedCount;

    /// <summary>Validates and retains immutable model configuration; storage grows with the first input.</summary>
    /// <param name="parameters">Model quantities in reduced units, including a cutoff with positive finite square.</param>
    /// <param name="matrix">Immutable directed coefficients indexed by receiver then source species.</param>
    /// <exception cref="ArgumentNullException">A configuration object is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A model parameter is invalid.</exception>
    public UniformGridForceEvaluator(ModelParameters parameters, InteractionMatrix matrix)
    {
        direct = new DirectForceEvaluator(parameters, matrix);
        this.parameters = parameters;
        this.matrix = matrix;
        kernel = new PairForceKernel(parameters, matrix);
        index = new UniformGridIndex(parameters.Cutoff);
        LastExecution = new(ForceBackend.UniformGrid, ForceBackend.UniformGrid, GridFallbackReason.None, index.Statistics);
    }

    /// <summary>Gets search/fallback metadata from the last successful force evaluation.</summary>
    /// <remarks>Before evaluation it contains configuration only. Failed evaluations leave it unchanged.</remarks>
    public ForceExecution LastExecution { get; private set; }

    /// <summary>Gets current index capacity and occupancy, including after an internal stage-only rebuild.</summary>
    /// <remarks>This is work-buffer telemetry; a failed simulation step may have changed it without publishing state.</remarks>
    public GridIndexStatistics IndexStatistics => index.Statistics;

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
        prepared = false;
        DirectForceEvaluator.ValidateState(input, matrix.SpeciesCount);
        useGrid = index.Build(input.Positions);
        preparedCount = input.Count;
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
        if (!prepared || input.Count != preparedCount)
        {
            throw new InvalidOperationException("Build an index from the same complete input before prepared evaluation.");
        }

        DirectForceEvaluator.ValidateOutput(input, forces);
        if (!useGrid)
        {
            return EvaluateDirectFallback(input, forces, GridFallbackReason.CoordinateRange);
        }

        ReadOnlySpan<Vector2d> positions = input.Positions;
        ReadOnlySpan<int> species = input.Species;
        long candidatePairs = 0;
        long acceptedPairs = 0;
        long overlapPairs = 0;
        for (int receiver = 0; receiver < input.Count; receiver++)
        {
            Vector2d totalForce = default;
            int row = species[receiver] * matrix.SpeciesCount;
            UniformGridIndex.CandidateEnumerator candidates = index.Candidates(receiver);
            while (candidates.MoveNext())
            {
                int source = candidates.Current;
                if (source == receiver)
                {
                    continue;
                }

                candidatePairs++;
                PairKind kind = kernel.Evaluate(positions[source] - positions[receiver], row + species[source], out Vector2d pairForce);
                if (kind == PairKind.Accepted)
                {
                    acceptedPairs++;
                    totalForce += pairForce;
                }
                else if (kind == PairKind.Overlap && source > receiver)
                {
                    overlapPairs++;
                }
            }

            totalForce += SoftWall.EvaluateValidated(positions[receiver], parameters);
            if (!totalForce.IsFinite)
            {
                return EvaluateDirectFallback(input, forces, GridFallbackReason.AccumulationRange);
            }

            forces[receiver] = totalForce;
        }

        LastExecution = new(ForceBackend.UniformGrid, ForceBackend.UniformGrid, GridFallbackReason.None, index.Statistics);
        return new(candidatePairs, acceptedPairs, overlapPairs);
    }

    /// <summary>Overwrites every force using direct enumeration, publishing metadata only if the retry succeeds.</summary>
    /// <remarks>Discard all partial grid counts and forces. No exception is caught, and successful fallback allocates nothing.</remarks>
    private ForceEvaluationStatistics EvaluateDirectFallback(ParticleStateView input, Span<Vector2d> forces, GridFallbackReason reason)
    {
        ForceEvaluationStatistics statistics = direct.Evaluate(input, forces);
        LastExecution = new(ForceBackend.UniformGrid, ForceBackend.Direct, reason, index.Statistics);
        return statistics;
    }
}
