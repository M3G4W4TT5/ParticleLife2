using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Viewer;

/// <summary>Owns sequential fixed-step playback and explicitly scheduled measurements for a viewer.</summary>
/// <remarks>
/// <para>One elapsed wall-clock second requests one reduced simulation-time unit. Rendering never
/// changes the experiment timestep. Each frame completes at most <see cref="MaxStepsPerFrame"/>
/// steps, discards any remaining whole-step playback time and retains only the fractional remainder.
/// Slow playback therefore falls behind wall time rather than changing the numerical method.</para>
/// <para>Call input operations between frames, then advance, then observe for immediate rendering.
/// This object is not thread-safe. Observations must not survive advancement, reset or replacement.
/// Warm advancement without a scheduled diagnostic allocates no managed memory. Measurements reuse
/// private spatial/component storage and allocate only their immutable result after capacity is established;
/// worst-case connectivity remains quadratic. Cadence counts completed simulation steps, not rendered frames.</para>
/// <para>The controller owns its simulation, including any configured force workers. Dispose it between
/// frames to join those workers. Experiment replacement disposes the previous simulation only after
/// a replacement has constructed successfully. Read-only state remains observable after disposal.</para>
/// </remarks>
public sealed class PlaybackController : IDisposable
{
    private Simulation simulation;
    private readonly DiagnosticSampler diagnosticSampler = new();
    private int diagnosticEverySteps;
    private bool disposed;

    /// <summary>Creates playback at the exact saved initial conditions, initially running.</summary>
    /// <param name="experiment">Validated experiment with initial time and step zero.</param>
    /// <param name="maxStepsPerFrame">Positive maximum completed steps per call to <see cref="AdvanceFrame"/>.</param>
    /// <param name="diagnosticEverySteps">Nonnegative measurement cadence in completed steps; zero disables sampling.</param>
    /// <exception cref="ArgumentException">An argument is null or outside its valid range.</exception>
    public PlaybackController(ExperimentDefinition experiment, int maxStepsPerFrame = 8, int diagnosticEverySteps = 144)
    {
        ArgumentNullException.ThrowIfNull(experiment);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxStepsPerFrame);
        ArgumentOutOfRangeException.ThrowIfNegative(diagnosticEverySteps);
        simulation = experiment.CreateSimulation();
        Experiment = experiment;
        MaxStepsPerFrame = maxStepsPerFrame;
        this.diagnosticEverySteps = diagnosticEverySteps;
    }

    /// <summary>Gets the immutable experiment whose exact initial conditions reset and save use.</summary>
    public ExperimentDefinition Experiment { get; private set; }

    /// <summary>Gets actual search and fallback metadata from the last successful step, or null after reset/replacement.</summary>
    public ForceExecution? LastExecution => simulation.LastExecution;

    /// <summary>Gets successful grid-requested steps using direct fallback since reset or replacement.</summary>
    public long DirectFallbackSteps => simulation.DirectFallbackSteps;

    /// <summary>Gets successful direct fallbacks caused by non-finite grid-order accumulation since reset or replacement.</summary>
    /// <remarks>Subtract this count from <see cref="DirectFallbackSteps"/> to obtain coordinate-range fallbacks.</remarks>
    public long AccumulationFallbackSteps => simulation.AccumulationFallbackSteps;

    /// <summary>Gets geometric counts from the last successful step's input, or zero after reset/replacement.</summary>
    public ForceEvaluationStatistics LastStatistics => simulation.LastStatistics;

    /// <summary>Gets whether automatic playback is paused.</summary>
    public bool IsPaused { get; private set; }

    /// <summary>Gets the positive cap on numerical work in one frame.</summary>
    public int MaxStepsPerFrame { get; }

    /// <summary>Gets playback seconds retained towards the next fixed step.</summary>
    public double PendingPlaybackSeconds { get; private set; }

    /// <summary>Gets whole-step playback seconds discarded by catch-up limits since reset or replacement.</summary>
    /// <remarks>Paused elapsed time is ignored and is not counted as dropped playback.</remarks>
    public double DroppedPlaybackSeconds { get; private set; }

    /// <summary>Gets the latest sampled state, including its own simulation time and step, or null if unavailable.</summary>
    public DiagnosticSample? LatestDiagnostic { get; private set; }

    /// <summary>Gets spatial-query/fallback and storage metadata for <see cref="LatestDiagnostic"/>, or null when cleared.</summary>
    public DiagnosticExecution? LatestDiagnosticExecution { get; private set; }

    /// <summary>Gets the last diagnostic failure message, or null after successful sampling or clearing.</summary>
    /// <remarks>A measurement failure does not change the simulated trajectory or playback state.</remarks>
    public string? DiagnosticError { get; private set; }

    /// <summary>Gets or sets the measurement cadence in completed steps; zero disables sampling.</summary>
    /// <remarks>Changing this value clears the previous measurement. Sampling occurs at positive step
    /// multiples of the new cadence. It does not change state, pending playback or the numerical timestep.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The requested cadence is negative.</exception>
    public int DiagnosticEverySteps
    {
        get => diagnosticEverySteps;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            diagnosticEverySteps = value;
            ClearDiagnostic();
        }
    }

    /// <summary>Obtains completed state for immediate reading without copying a snapshot.</summary>
    /// <returns>A view valid only until the next step, reset or replacement.</returns>
    public ParticleStateView Observe() => simulation.Observe();

    /// <summary>Changes playback status, clearing fractional time when the status changes.</summary>
    /// <param name="paused">True to pause; false to resume without a backlog.</param>
    /// <remarks>Repeatedly setting the current status is a no-op and does not discard accumulated time.</remarks>
    public void SetPaused(bool paused)
    {
        if (IsPaused != paused)
        {
            IsPaused = paused;
            PendingPlaybackSeconds = 0;
        }
    }

    /// <summary>Pauses playback, clears pending time and completes exactly one numerical step.</summary>
    /// <remarks>A scheduled measurement may follow the completed step. On numerical failure, playback
    /// remains paused and the core preserves its last completed state.</remarks>
    /// <exception cref="ArithmeticException">The numerical step cannot complete within its valid range.</exception>
    public void SingleStep()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        IsPaused = true;
        PendingPlaybackSeconds = 0;
        CompleteStep();
    }

    /// <summary>Restores the current experiment's exact initial state, matrix, time and step count.</summary>
    /// <remarks>Preserves pause status, clears playback backlog and dropped-time accounting, and removes
    /// stale measurements. No generator is run; the saved matrix and model remain unchanged.</remarks>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        simulation.Reset(Experiment.InitialState);
        ClearPlayback();
    }

    /// <summary>Atomically replaces the experiment and simulation at a completed-step boundary.</summary>
    /// <param name="experiment">Validated replacement, whose initial state becomes the new reset target.</param>
    /// <remarks>Constructs the replacement before publishing it. A rejected replacement leaves the
    /// previous experiment and playback accounting intact. Preserves pause status, clears backlog,
    /// dropped-time accounting and stale diagnostics. Call only when no observation is being read.</remarks>
    /// <exception cref="ArgumentException">The replacement is null or cannot construct a valid simulation.</exception>
    public void ReplaceExperiment(ExperimentDefinition experiment)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(experiment);
        Simulation replacement = experiment.CreateSimulation();
        Simulation previous = simulation;
        simulation = replacement;
        Experiment = experiment;
        ClearPlayback();
        previous.Dispose();
    }

    /// <summary>Consumes elapsed playback time using only the experiment's fixed numerical timestep.</summary>
    /// <param name="elapsedSeconds">Finite, nonnegative wall-clock seconds since the previous frame.</param>
    /// <returns>The number of completed steps, between zero and <see cref="MaxStepsPerFrame"/>.</returns>
    /// <remarks>
    /// Paused frames ignore elapsed time. A numerical failure pauses playback and clears pending time;
    /// previously completed steps remain valid. The cap bounds the number of steps, not the cost of an
    /// individual quadratic step or diagnostic. Extremely large populations can still stall one frame.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Elapsed time is invalid or accumulated time overflows.</exception>
    /// <exception cref="ArithmeticException">A numerical step fails; the last completed state is preserved.</exception>
    public int AdvanceFrame(double elapsedSeconds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds), "Elapsed playback time must be finite and nonnegative.");
        }

        if (IsPaused)
        {
            return 0;
        }

        double pending = PendingPlaybackSeconds + elapsedSeconds;
        if (!double.IsFinite(pending))
        {
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds), "Accumulated playback time exceeds the supported range.");
        }

        PendingPlaybackSeconds = pending;
        int completed = 0;
        try
        {
            while (completed < MaxStepsPerFrame && PendingPlaybackSeconds >= Experiment.TimeStep)
            {
                CompleteStep();
                PendingPlaybackSeconds -= Experiment.TimeStep;
                completed++;
            }
        }
        catch (ArithmeticException)
        {
            IsPaused = true;
            PendingPlaybackSeconds = 0;
            throw;
        }

        if (PendingPlaybackSeconds >= Experiment.TimeStep)
        {
            double remainder = PendingPlaybackSeconds % Experiment.TimeStep;
            DroppedPlaybackSeconds += PendingPlaybackSeconds - remainder;
            PendingPlaybackSeconds = remainder;
        }

        return completed;
    }

    /// <summary>Releases the owned simulation and joins its persistent force workers, if configured.</summary>
    /// <remarks>Call only between frames. Disposal is idempotent; subsequent advancement, reset and
    /// experiment replacement throw <see cref="ObjectDisposedException"/>. Completed state remains readable.</remarks>
    public void Dispose()
    {
        if (!disposed)
        {
            simulation.Dispose();
            disposed = true;
        }
    }

    /// <summary>Completes one step, then samples only at the configured simulation-step cadence.</summary>
    /// <remarks>The observation is scoped entirely after stepping and is never retained. Diagnostic
    /// failures cannot undo a successful step and are reported separately from numerical failure.</remarks>
    private void CompleteStep()
    {
        simulation.Step();
        ParticleStateView state = simulation.Observe();
        if (diagnosticEverySteps > 0 && state.Step % diagnosticEverySteps == 0)
        {
            try
            {
                LatestDiagnostic = diagnosticSampler.Sample(state, Experiment.Parameters.Cutoff);
                LatestDiagnosticExecution = diagnosticSampler.LastExecution;
                DiagnosticError = null;
            }
            catch (InvalidOperationException error)
            {
                LatestDiagnostic = null;
                LatestDiagnosticExecution = null;
                DiagnosticError = error.Message;
            }
        }
    }

    private void ClearPlayback()
    {
        PendingPlaybackSeconds = 0;
        DroppedPlaybackSeconds = 0;
        ClearDiagnostic();
    }

    private void ClearDiagnostic()
    {
        LatestDiagnostic = null;
        LatestDiagnosticExecution = null;
        DiagnosticError = null;
    }
}
