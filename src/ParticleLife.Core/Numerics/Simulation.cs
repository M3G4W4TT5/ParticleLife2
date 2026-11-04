using ParticleLife.Core.Models;
using ParticleLife.Core.State;

namespace ParticleLife.Core.Numerics;

/// <summary>Owns reusable state and work buffers for a serial, fixed-timestep Particle Life simulation.</summary>
/// <remarks>
/// <para>Simulation is independent of wall-clock and rendering time. Each step first evaluates all
/// forces from complete input, then integrates into separate outputs, then swaps completed buffers.
/// No particle observes an earlier particle's partially updated state.</para>
/// <para>Steps and resets invalidate prior observations. Do not read or mutate a simulation concurrently.
/// Warmed successful steps and resets at established capacity allocate no managed memory. Construction,
/// index initialization, capacity growth and explicit snapshots allocate. There is no randomness in the stepping algorithm.</para>
/// </remarks>
public sealed class Simulation
{
    private readonly IForceEvaluator evaluator;
    private readonly double dampingFactor;
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

    /// <summary>Copies initial state and selects a serial force search with reusable state buffers.</summary>
    /// <param name="initial">Owned initial state or checkpoint; never mutated by the simulation.</param>
    /// <param name="parameters">Immutable model parameters in reduced units.</param>
    /// <param name="matrix">Immutable directed species coefficients.</param>
    /// <param name="timeStep">Positive finite fixed timestep in reduced time units.</param>
    /// <param name="backend">Neighbour search; the API default retains the direct reference implementation.</param>
    /// <exception cref="ArgumentNullException">An argument object is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A parameter or timestep is invalid.</exception>
    /// <exception cref="ArgumentException">An initial species is outside the matrix.</exception>
    public Simulation(ParticleSnapshot initial, ModelParameters parameters, InteractionMatrix matrix, double timeStep,
        ForceBackend backend = ForceBackend.Direct)
    {
        ArgumentNullException.ThrowIfNull(initial);
        evaluator = backend switch
        {
            ForceBackend.Direct => new DirectForceEvaluator(parameters, matrix),
            ForceBackend.UniformGrid => new UniformGridForceEvaluator(parameters, matrix),
            _ => throw new ArgumentOutOfRangeException(nameof(backend), "Unsupported neighbour-search backend.")
        };
        Backend = backend;
        dampingFactor = SemiImplicitEuler.DampingFactor(timeStep, parameters.DampingHalfLife);
        Parameters = parameters;
        Matrix = matrix;
        TimeStep = timeStep;
        Reset(initial);
    }

    /// <summary>Gets the fixed timestep in reduced time units.</summary>
    public double TimeStep { get; }

    /// <summary>Gets immutable model parameters.</summary>
    public ModelParameters Parameters { get; }

    /// <summary>Gets the immutable directed species matrix.</summary>
    public InteractionMatrix Matrix { get; }

    /// <summary>Gets the requested neighbour search; model equations and integration are unchanged.</summary>
    public ForceBackend Backend { get; }

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
    /// <returns>A view valid only until the next step or reset, including stable identities and simulation time.</returns>
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
        ArgumentNullException.ThrowIfNull(snapshot);
        DirectForceEvaluator.ValidateState(snapshot.Observe(), Matrix.SpeciesCount);
        if (snapshot.Count > positions.Length)
        {
            ids = new long[snapshot.Count];
            species = new int[snapshot.Count];
            positions = new Vector2d[snapshot.Count];
            velocities = new Vector2d[snapshot.Count];
            outputPositions = new Vector2d[snapshot.Count];
            outputVelocities = new Vector2d[snapshot.Count];
            forces = new Vector2d[snapshot.Count];
        }

        snapshot.Ids.CopyTo(ids);
        snapshot.Species.CopyTo(species);
        snapshot.Positions.CopyTo(positions);
        snapshot.Velocities.CopyTo(velocities);
        count = snapshot.Count;
        time = snapshot.Time;
        step = snapshot.Step;
        LastStatistics = default;
        LastExecution = null;
        DirectFallbackSteps = 0;
        AccumulationFallbackSteps = 0;
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
}
