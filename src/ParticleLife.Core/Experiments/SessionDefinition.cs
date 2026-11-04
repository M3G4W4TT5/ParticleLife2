using System.Collections.ObjectModel;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Core.Experiments;

/// <summary>Owns an exact initial experiment, ordered applied edits and the terminal completed boundary.</summary>
/// <remarks>Events before the first step and after the last step are meaningful. Replay applies every
/// event at a boundary in its saved list order before advancing again. Terminal edits therefore survive
/// even for a zero-step session. Playback and appearance are deliberately outside this scientific format.</remarks>
public sealed class SessionDefinition
{
    /// <summary>Copies and validates the ordered event sequence without running the simulation.</summary>
    /// <param name="initialExperiment">Exact authoritative initial values at time and step zero.</param>
    /// <param name="events">Applied edits in nondecreasing boundary order; equal boundaries retain list order.</param>
    /// <param name="terminalStep">Nonnegative final completed-step count, at or beyond every event.</param>
    /// <exception cref="ArgumentException">An event, ordering, population/matrix relation or boundary is invalid.</exception>
    public SessionDefinition(ExperimentDefinition initialExperiment, IEnumerable<SessionEvent> events, long terminalStep)
    {
        ArgumentNullException.ThrowIfNull(initialExperiment);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentOutOfRangeException.ThrowIfNegative(terminalStep);
        SessionEvent[] ownedEvents = events.ToArray();
        long previousStep = 0;
        ParticleSnapshot speciesState = initialExperiment.InitialState;
        long lastStructuralStep = 0;
        double lastStructuralTime = 0;
        foreach (SessionEvent change in ownedEvents)
        {
            ArgumentNullException.ThrowIfNull(change);
            if (change.BoundaryStep < previousStep || change.BoundaryStep > terminalStep)
            {
                throw new ArgumentException("Session edits must be ordered and no later than the terminal boundary.", nameof(events));
            }

            if (change.ReplacementState is ParticleSnapshot structural)
            {
                if ((structural.Step == lastStructuralStep && structural.Time != lastStructuralTime)
                    || (structural.Step > lastStructuralStep && structural.Time <= lastStructuralTime))
                {
                    throw new ArgumentException("Structural payload clocks must advance consistently with their boundaries.", nameof(events));
                }

                speciesState = structural;
                lastStructuralStep = structural.Step;
                lastStructuralTime = structural.Time;
            }

            // Integration never changes species, so the last explicit population is authoritative here.
            DirectForceEvaluator.ValidateState(speciesState.Observe(), change.Configuration.Matrix.SpeciesCount);
            previousStep = change.BoundaryStep;
        }

        InitialExperiment = initialExperiment;
        Events = new ReadOnlyCollection<SessionEvent>(ownedEvents);
        TerminalStep = terminalStep;
    }

    /// <summary>Gets the exact experiment establishing the replay baseline.</summary>
    public ExperimentDefinition InitialExperiment { get; }

    /// <summary>Gets immutable events in authoritative application order.</summary>
    public IReadOnlyList<SessionEvent> Events { get; }

    /// <summary>Gets the terminal completed boundary, including every edit applied there.</summary>
    public long TerminalStep { get; }
}
