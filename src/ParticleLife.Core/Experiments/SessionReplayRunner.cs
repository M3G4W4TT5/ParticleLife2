using ParticleLife.Core.Numerics;

namespace ParticleLife.Core.Experiments;

/// <summary>Owns incremental, simulation-step-based replay independently of graphics or wall-clock timing.</summary>
/// <remarks>Construction applies boundary-zero edits. Every successful advance caches execution metadata,
/// then applies all reached edits in order, including terminal edits. Failed numerical steps retain the
/// last completed state. Use this runner exclusively: hosts must not step/reconfigure its simulation
/// themselves during replay. To continue independently, use <see cref="SessionReplay.Replay"/>.</remarks>
public sealed class SessionReplayRunner : IDisposable
{
    private bool disposed;
    private bool ownsSimulation = true;

    /// <summary>Creates an independent initial simulation and applies every boundary-zero event.</summary>
    /// <param name="session">Immutable source timeline.</param>
    /// <param name="overrides">Explicit execution overrides retained for provenance, or null.</param>
    /// <exception cref="ArgumentException">An effective configuration or boundary-zero payload is invalid.</exception>
    public SessionReplayRunner(SessionDefinition session, ReplayOverrides? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session = session;
        Overrides = overrides;
        ExperimentDefinition initial = session.InitialExperiment;
        SimulationConfiguration initialConfiguration = new(initial.Parameters, initial.Matrix, initial.TimeStep,
            initial.Backend, initial.WorkerCount);
        if (overrides is not null)
        {
            initialConfiguration = overrides.Apply(initialConfiguration);
            foreach (SessionEvent change in session.Events)
            {
                _ = overrides.Apply(change.Configuration);
            }
        }

        Simulation = new(initial.InitialState, initialConfiguration.Parameters, initialConfiguration.Matrix,
            initialConfiguration.TimeStep, initialConfiguration.Backend, initialConfiguration.WorkerCount);
        try
        {
            ApplyBoundaryEvents();
        }
        catch
        {
            Simulation.Dispose();
            throw;
        }
    }

    /// <summary>Gets the unchanged source session, including recorded execution choices.</summary>
    public SessionDefinition Session { get; }

    /// <summary>Gets the explicit overrides to include in host output provenance.</summary>
    public ReplayOverrides? Overrides { get; }

    /// <summary>Gets completed replay state for observation; the runner owns mutation and disposal.</summary>
    public Simulation Simulation { get; }

    /// <summary>Gets the number of successfully applied events, including same-boundary and terminal edits.</summary>
    public int AppliedEventCount { get; private set; }

    /// <summary>Gets whether the terminal boundary and all its events have been applied.</summary>
    public bool IsComplete => Simulation.Observe().Step == Session.TerminalStep && AppliedEventCount == Session.Events.Count;

    /// <summary>Gets execution of the most recently completed step, retained across subsequent boundary edits.</summary>
    public ForceExecution? LastCompletedExecution { get; private set; }

    /// <summary>Gets effective settings governing the most recently completed step, before reached edits.</summary>
    public SimulationConfiguration? LastCompletedConfiguration { get; private set; }

    /// <summary>Advances exactly once, then applies all changes at the new completed boundary.</summary>
    /// <remarks>No hidden numerical step occurs for an event or a terminal edit. Warmed advances without
    /// events retain the core allocation contract. Structural payload clocks are compared exactly with
    /// accumulated time, including variable timestep increments; a malformed payload is never published.</remarks>
    /// <exception cref="InvalidOperationException">The session is already complete.</exception>
    /// <exception cref="ArgumentException">A structural payload has an inconsistent replay clock.</exception>
    /// <exception cref="ArithmeticException">The next numerical step cannot complete.</exception>
    public void AdvanceOneStep()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (IsComplete)
        {
            throw new InvalidOperationException("The session has already reached its terminal boundary.");
        }

        // A previous invalid event must never be silently skipped by taking another numerical step.
        ApplyBoundaryEvents();
        SimulationConfiguration completedConfiguration = Simulation.Configuration;
        Simulation.Step();
        LastCompletedConfiguration = completedConfiguration;
        LastCompletedExecution = Simulation.LastExecution;
        ApplyBoundaryEvents();
    }

    /// <summary>Runs remaining steps and edits to the saved terminal boundary.</summary>
    public void RunToEnd()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        while (!IsComplete)
        {
            AdvanceOneStep();
        }
    }

    private void ApplyBoundaryEvents()
    {
        long boundary = Simulation.Observe().Step;
        while (AppliedEventCount < Session.Events.Count && Session.Events[AppliedEventCount].BoundaryStep == boundary)
        {
            SessionReplay.ApplyEvent(Simulation, Session.Events[AppliedEventCount], Overrides);
            AppliedEventCount++;
        }
    }

    /// <summary>Transfers the completed simulation to a host for continued independent experimentation.</summary>
    /// <returns>The terminal simulation; the caller now owns mutation and disposal.</returns>
    /// <remarks>Disposing this runner after transfer does not dispose the returned simulation. Transfer
    /// is allowed exactly once and only after every terminal event has been applied.</remarks>
    /// <exception cref="InvalidOperationException">Replay is incomplete or ownership was already transferred.</exception>
    /// <exception cref="ObjectDisposedException">The runner has been disposed.</exception>
    public Simulation DetachSimulation()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!IsComplete || !ownsSimulation)
        {
            throw new InvalidOperationException("Only an owned completed replay can transfer simulation ownership, once.");
        }

        ownsSimulation = false;
        return Simulation;
    }

    /// <summary>Releases owned persistent workers; completed state remains readable.</summary>
    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            if (ownsSimulation)
            {
                Simulation.Dispose();
            }
        }
    }
}
