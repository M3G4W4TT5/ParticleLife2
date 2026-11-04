using ParticleLife.Core.Numerics;

namespace ParticleLife.Core.Experiments;

/// <summary>Shares exact boundary application between headless replay and interactive hosts.</summary>
public static class SessionReplay
{
    /// <summary>Replays through the terminal boundary and returns the independently owned simulation.</summary>
    /// <param name="session">Validated exact initial conditions and ordered applied events.</param>
    /// <param name="overrides">Explicit execution overrides to report separately, or null to honour recorded choices.</param>
    /// <returns>The terminal simulation, ready for observation or continued experimentation; the caller must dispose it.</returns>
    /// <exception cref="ArgumentException">An event's structural clock differs from the actual replay boundary.</exception>
    /// <exception cref="ArithmeticException">A numerical step fails; no partial simulation is returned.</exception>
    public static Simulation Replay(SessionDefinition session, ReplayOverrides? overrides = null)
    {
        using SessionReplayRunner runner = new(session, overrides);
        runner.RunToEnd();
        return runner.DetachSimulation();
    }

    /// <summary>Applies one saved change at its exact completed-step boundary using normal atomic reconfiguration.</summary>
    /// <param name="simulation">Exclusively owned simulation between completed steps.</param>
    /// <param name="change">Actual recorded change; no random generation occurs.</param>
    /// <param name="overrides">Optional explicit backend/worker overrides.</param>
    /// <exception cref="ArgumentException">The event does not match the current boundary or its state is incompatible.</exception>
    public static void ApplyEvent(Simulation simulation, SessionEvent change, ReplayOverrides? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(change);
        if (simulation.Observe().Step != change.BoundaryStep)
        {
            throw new ArgumentException("Apply a session edit only at its recorded completed-step boundary.", nameof(change));
        }

        simulation.Reconfigure(overrides?.Apply(change.Configuration) ?? change.Configuration, change.ReplacementState);
    }
}
