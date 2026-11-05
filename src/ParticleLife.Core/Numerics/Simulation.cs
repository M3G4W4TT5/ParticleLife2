using ParticleLife.Core.Models;
using ParticleLife.Core.State;

namespace ParticleLife.Core.Numerics;

/// <summary>Owns reusable state and work buffers for a Particle Life simulation with explicit timestep increments.</summary>
/// <remarks>
/// <para>Simulation is independent of wall-clock and rendering time. Each step first evaluates all
/// forces from complete input, then integrates into separate outputs, then swaps completed buffers.
/// No particle observes an earlier particle's partially updated state.</para>
/// <para>Steps, resets and successful reconfiguration invalidate prior observations. Do not read or mutate a simulation concurrently.
/// Warmed successful steps and resets at established capacity allocate no managed memory. Construction,
/// index initialization, capacity growth and explicit snapshots allocate. There is no randomness in the stepping algorithm.</para>
/// <para>One worker selects serial execution. Additional workers evaluate disjoint receivers from
/// the same packed input and join before integration or publication. Dispose parallel simulations
/// between steps to join their persistent threads; hosts still observe state sequentially.</para>
/// </remarks>
public sealed class Simulation : IDisposable
{
    private IForceEvaluator evaluator;
    private double dampingFactor;
    private long[] ids = [];
    private int[] species = [];
    private Vector2d[] positions = [];
    private Vector2d[] velocities = [];
    private Vector2d[] outputPositions = [];
    private Vector2d[] outputVelocities = [];
    private Vector2d[] forces = [];
    private int count;
    private double time;
    private long step;
    private bool disposed;

    /// <summary>Copies initial state and selects exact force search with explicit workers and reusable buffers.</summary>
    /// <param name="initial">Owned initial state or checkpoint; never mutated by the simulation.</param>
    /// <param name="parameters">Immutable model parameters in reduced units.</param>
    /// <param name="matrix">Immutable directed species coefficients.</param>
    /// <param name="timeStep">Positive finite fixed timestep in reduced time units.</param>
    /// <param name="backend">Neighbour search; the API default retains the direct reference implementation.</param>
    /// <param name="workerCount">Explicit grid workers including the caller, 1..64; direct requires one.</param>
    /// <exception cref="ArgumentNullException">An argument object is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A parameter or timestep is invalid.</exception>
    /// <exception cref="ArgumentException">An initial species is outside the matrix.</exception>
    public Simulation(ParticleSnapshot initial, ModelParameters parameters, InteractionMatrix matrix, double timeStep,
        ForceBackend backend = ForceBackend.Direct, int workerCount = 1)
    {
        ArgumentNullException.ThrowIfNull(initial);
        Configuration = new(parameters, matrix, timeStep, backend, workerCount);
        DirectForceEvaluator.ValidateState(initial.Observe(), matrix.SpeciesCount);
        dampingFactor = SemiImplicitEuler.DampingFactor(timeStep, parameters.DampingHalfLife);
        Reset(initial);
        // Establish state storage before starting owned background workers.
        evaluator = CreateEvaluator(Configuration);
    }

    /// <summary>Gets the immutable configuration currently governing subsequent steps.</summary>
    public SimulationConfiguration Configuration { get; private set; }

    /// <summary>Gets the timestep increment for subsequent steps, in reduced time units.</summary>
    public double TimeStep => Configuration.TimeStep;

    /// <summary>Gets immutable model parameters.</summary>
    public ModelParameters Parameters => Configuration.Parameters;

    /// <summary>Gets the immutable directed species matrix.</summary>
    public InteractionMatrix Matrix => Configuration.Matrix;

    /// <summary>Gets the requested neighbour search; model equations and integration are unchanged.</summary>
    public ForceBackend Backend => Configuration.Backend;

    /// <summary>Gets configured receiver workers, including the caller; fallback may execute with one.</summary>
    public int WorkerCount => Configuration.WorkerCount;

    /// <summary>Exposes resource identity to numerical contract tests, never to hosts.</summary>
    internal IForceEvaluator Evaluator => evaluator;

    /// <summary>Gets counts from the last successful step's input, or zero counts after construction/reset.</summary>
    public ForceEvaluationStatistics LastStatistics { get; private set; }

    /// <summary>Gets actual search execution from the last successful step, or null after construction/reset.</summary>
    /// <remarks>Grid coordinate or accumulation fallback applies to an entire evaluation. Failed steps never publish its metadata.</remarks>
    public ForceExecution? LastExecution { get; private set; }

    /// <summary>Gets the number of completed grid-requested steps that used direct fallback since construction/reset.</summary>
    /// <remarks>This cumulative count reports fallback earlier in a run even when the latest step used the grid.</remarks>
    public long DirectFallbackSteps { get; private set; }

    /// <summary>Gets completed direct-fallback steps caused by non-finite grid force accumulation since reset.</summary>
    /// <remarks>Subtract this from <see cref="DirectFallbackSteps"/> to obtain coordinate-range fallback steps.
    /// Counts publish only with a successful step; a direct retry can still fail without changing them.</remarks>
    public long AccumulationFallbackSteps { get; private set; }

    /// <summary>Obtains a read-only view of completed state without allocating.</summary>
    /// <returns>A view valid only until the next step, reset or reconfiguration, including stable identities and simulation time.</returns>
    public ParticleStateView Observe() => new(ids.AsSpan(0, count), positions.AsSpan(0, count),
        velocities.AsSpan(0, count), species.AsSpan(0, count), time, step);

    /// <summary>Copies the completed state into an independently owned immutable snapshot.</summary>
    /// <returns>A persistent snapshot unaffected by subsequent stepping or resets.</returns>
    /// <remarks>Allocates and validates outside the numerical hot loop; do not call on every step for timing benchmarks.</remarks>
    public ParticleSnapshot CaptureSnapshot() => new(ids.AsSpan(0, count), positions.AsSpan(0, count),
        velocities.AsSpan(0, count), species.AsSpan(0, count), time, step);

    /// <summary>Restores exact saved values, time and step count without regenerating random state.</summary>
    /// <param name="snapshot">Validated initial state or checkpoint with species supported by this model.</param>
    /// <remarks>Reuses buffers if the population fits established capacity; otherwise grows them. Invalid input leaves state unchanged.</remarks>
    /// <exception cref="ArgumentNullException">The snapshot is null.</exception>
    /// <exception cref="ArgumentException">A species is outside the interaction matrix.</exception>
    public void Reset(ParticleSnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(snapshot);
        DirectForceEvaluator.ValidateState(snapshot.Observe(), Matrix.SpeciesCount);
        StateBuffers? replacement = snapshot.Count > positions.Length ? new(snapshot.Count) : null;
        CopySnapshot(snapshot, replacement);
        LastStatistics = default;
        LastExecution = null;
        DirectFallbackSteps = 0;
        AccumulationFallbackSteps = 0;
    }

    /// <summary>Atomically applies complete settings and an optional structural state at the current boundary.</summary>
    /// <param name="configuration">Already validated physical, timestep and execution settings.</param>
    /// <param name="replacementState">Optional exact structural payload with the current time and step.
    /// Omit it for ordinary settings edits, including world resizing, to retain all particle values.</param>
    /// <remarks>
    /// <para>Call only between completed steps, never concurrently with evaluation or observation.
    /// All species and replacement-clock checks and capacity/resource preparation finish before publication.
    /// Invalid edits leave completed state, configuration, telemetry and worker ownership unchanged.</para>
    /// <para>Ordinary edits do not copy particle arrays. Model edits retain established index/packed
    /// capacities and persistent workers; only backend or worker-count changes replace an evaluator.
    /// Structural growth may allocate state storage. A replacement contains actual particle values;
    /// this method never generates or remaps particles implicitly. Finite off-world positions remain valid.</para>
    /// <para>Elapsed time and completed-step count never change. Last-step measurements are cleared
    /// after actual edits, while cumulative fallback counts survive. A value-identical configuration
    /// and absent or value-identical replacement is a no-op, preserving observations and measurements.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">The candidate contains unsupported species or a different clock.</exception>
    /// <exception cref="ObjectDisposedException">The simulation has been disposed.</exception>
    public void Reconfigure(SimulationConfiguration configuration, ParticleSnapshot? replacementState = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(configuration);
        if (replacementState is not null && (replacementState.Time != time || replacementState.Step != step))
        {
            throw new ArgumentException("A structural edit must retain the current completed time and step.", nameof(replacementState));
        }

        DirectForceEvaluator.ValidateState(replacementState is null ? Observe() : replacementState.Observe(),
            configuration.Matrix.SpeciesCount);
        bool sameConfiguration = Configuration.EquivalentTo(configuration);
        bool sameState = replacementState is null || StateEquals(replacementState);
        if (sameConfiguration && sameState)
        {
            return;
        }

        double nextDamping = SemiImplicitEuler.DampingFactor(configuration.TimeStep, configuration.Parameters.DampingHalfLife);
        StateBuffers? nextBuffers = replacementState is not null && replacementState.Count > positions.Length
            ? new(replacementState.Count) : null;
        bool replaceEvaluator = Backend != configuration.Backend || WorkerCount != configuration.WorkerCount;
        IForceEvaluator? nextEvaluator = replaceEvaluator ? CreateEvaluator(configuration) : null;
        if (!replaceEvaluator && (Parameters != configuration.Parameters || !ReferenceEquals(Matrix, configuration.Matrix)))
        {
            switch (evaluator)
            {
                case DirectForceEvaluator direct:
                    direct.Reconfigure(configuration.Parameters, configuration.Matrix);
                    break;
                case UniformGridForceEvaluator grid:
                    grid.Reconfigure(configuration.Parameters, configuration.Matrix);
                    break;
            }
        }

        IForceEvaluator previousEvaluator = evaluator;
        if (nextEvaluator is not null)
        {
            evaluator = nextEvaluator;
        }

        Configuration = configuration;
        dampingFactor = nextDamping;
        if (!sameState)
        {
            CopySnapshot(replacementState!, nextBuffers);
        }

        LastStatistics = default;
        LastExecution = null;
        if (replaceEvaluator)
        {
            (previousEvaluator as IDisposable)?.Dispose();
        }
    }

    /// <summary>Copies a validated payload after all possibly allocating preparation succeeds.</summary>
    private void CopySnapshot(ParticleSnapshot snapshot, StateBuffers? replacement)
    {
        if (replacement is not null)
        {
            ids = replacement.Ids;
            species = replacement.Species;
            positions = replacement.Positions;
            velocities = replacement.Velocities;
            outputPositions = replacement.OutputPositions;
            outputVelocities = replacement.OutputVelocities;
            forces = replacement.Forces;
        }

        snapshot.Ids.CopyTo(ids);
        snapshot.Species.CopyTo(species);
        snapshot.Positions.CopyTo(positions);
        snapshot.Velocities.CopyTo(velocities);
        count = snapshot.Count;
        time = snapshot.Time;
        step = snapshot.Step;
    }

    private bool StateEquals(ParticleSnapshot snapshot) => snapshot.Count == count
        && snapshot.Ids.SequenceEqual(ids.AsSpan(0, count))
        && snapshot.Species.SequenceEqual(species.AsSpan(0, count))
        && snapshot.Positions.SequenceEqual(positions.AsSpan(0, count))
        && snapshot.Velocities.SequenceEqual(velocities.AsSpan(0, count));

    private static IForceEvaluator CreateEvaluator(SimulationConfiguration configuration) => configuration.Backend switch
    {
        ForceBackend.Direct => new DirectForceEvaluator(configuration.Parameters, configuration.Matrix),
        ForceBackend.UniformGrid => new UniformGridForceEvaluator(configuration.Parameters, configuration.Matrix, configuration.WorkerCount),
        _ => throw new ArgumentOutOfRangeException(nameof(configuration))
    };

    /// <summary>Prepares all state arrays before publishing any capacity change.</summary>
    private sealed class StateBuffers(int capacity)
    {
        internal long[] Ids { get; } = new long[capacity];
        internal int[] Species { get; } = new int[capacity];
        internal Vector2d[] Positions { get; } = new Vector2d[capacity];
        internal Vector2d[] Velocities { get; } = new Vector2d[capacity];
        internal Vector2d[] OutputPositions { get; } = new Vector2d[capacity];
        internal Vector2d[] OutputVelocities { get; } = new Vector2d[capacity];
        internal Vector2d[] Forces { get; } = new Vector2d[capacity];
    }

    /// <summary>Advances one complete fixed timestep without managed allocation after index and buffer warm-up.</summary>
    /// <remarks>
    /// Publishes output only after force evaluation, integration and time checks all succeed.
    /// On a numerical failure the completed state, time, step count, last statistics and execution metadata remain unchanged.
    /// Working buffers may contain rejected partial results but will be overwritten on a later successful step.
    /// </remarks>
    /// <exception cref="ArithmeticException">Forces, state or time overflow, time cannot advance in double precision, or the step count is exhausted.</exception>
    public void Step()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        double nextTime = time + TimeStep;
        if (!double.IsFinite(nextTime) || nextTime <= time || step == long.MaxValue)
        {
            throw new ArithmeticException("Simulation time or step count cannot advance within its numerical range.");
        }

        ParticleStateView input = Observe();
        ForceEvaluationStatistics statistics = evaluator.Evaluate(input, forces.AsSpan(0, count));
        SemiImplicitEuler.Integrate(input, forces.AsSpan(0, count), TimeStep, dampingFactor,
            outputPositions.AsSpan(0, count), outputVelocities.AsSpan(0, count));
        (positions, outputPositions) = (outputPositions, positions);
        (velocities, outputVelocities) = (outputVelocities, velocities);
        time = nextTime;
        step++;
        LastStatistics = statistics;
        LastExecution = evaluator.LastExecution;
        if (Backend == ForceBackend.UniformGrid && evaluator.LastExecution.ActualBackend == ForceBackend.Direct)
        {
            DirectFallbackSteps++;
            if (evaluator.LastExecution.FallbackReason == GridFallbackReason.AccumulationRange)
            {
                AccumulationFallbackSteps++;
            }
        }
    }

    /// <summary>Joins and releases owned worker threads and synchronization resources.</summary>
    /// <remarks>Call between steps, never concurrently with evaluation or observation. Repeated disposal
    /// is harmless. The completed state remains readable, but stepping, resetting and reconfiguration are prohibited.</remarks>
    public void Dispose()
    {
        if (!disposed)
        {
            (evaluator as IDisposable)?.Dispose();
            disposed = true;
        }
    }
}
