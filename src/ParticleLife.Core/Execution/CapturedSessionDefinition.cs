using System.Collections.ObjectModel;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Core.Execution;

/// <summary>Records a deliberate hardware/precision transfer at a completed action boundary.</summary>
/// <param name="BoundaryStep">Completed numerical-step count.</param>
/// <param name="Time">Exact accumulated host clock at the transfer.</param>
/// <param name="AppliedEventCount">Action entries already applied, including any recorded conversion payload.</param>
/// <param name="From">Actual previous execution provenance.</param>
/// <param name="To">Actual destination execution provenance.</param>
/// <param name="ValuesConverted">Whether any particle component changed during an explicitly permitted precision conversion.</param>
public sealed record ExecutionTransition(long BoundaryStep, double Time, int AppliedEventCount,
    ExecutionProvenance From, ExecutionProvenance To, bool ValuesConverted);

/// <summary>Combines action history with authoritative periodic/terminal captured values and execution provenance.</summary>
/// <remarks>The embedded action session keeps its own codec/version. Captures restore recorded boundaries;
/// actions describe re-simulation, whose trajectories may differ. A terminal capture is mandatory, even
/// for a zero-step recording or a session with edits after its last numerical step.</remarks>
public sealed class CapturedSessionDefinition
{
    /// <summary>Validates a capture-backed session without replaying it or loading a device.</summary>
    /// <param name="actions">Exact initial state and ordered logical action history.</param>
    /// <param name="captures">Nonempty captures ordered by step and same-boundary applied-event count.</param>
    /// <param name="transitions">Ordered actual execution transfers, each backed by a destination capture.</param>
    public CapturedSessionDefinition(SessionDefinition actions, IEnumerable<SimulationCapture> captures,
        IEnumerable<ExecutionTransition> transitions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(captures);
        ArgumentNullException.ThrowIfNull(transitions);
        SimulationCapture[] ownedCaptures = captures.ToArray();
        ExecutionTransition[] ownedTransitions = transitions.ToArray();
        if (ownedCaptures.Length == 0)
        {
            throw new ArgumentException("A captured session requires an authoritative terminal capture.", nameof(captures));
        }
        SimulationConfiguration initialConfiguration = new(actions.InitialExperiment.Parameters,
            actions.InitialExperiment.Matrix, actions.InitialExperiment.TimeStep,
            actions.InitialExperiment.Backend, actions.InitialExperiment.WorkerCount);
        long previousStep = 0;
        double previousTime = 0;
        int previousEvents = 0;
        Dictionary<long, int> membership = Membership(actions.InitialExperiment.InitialState);
        foreach (SimulationCapture capture in ownedCaptures)
        {
            ArgumentNullException.ThrowIfNull(capture);
            long step = capture.State.Step;
            double time = capture.State.Time;
            int eventCount = capture.AppliedEventCount;
            if (step < previousStep || step > actions.TerminalStep || eventCount < previousEvents
                || eventCount > actions.Events.Count || (step == previousStep ? time != previousTime : time <= previousTime))
            {
                throw new ArgumentException("Capture clocks and action positions must advance consistently within the session.", nameof(captures));
            }
            if ((eventCount > 0 && actions.Events[eventCount - 1].BoundaryStep > step)
                || (eventCount < actions.Events.Count && actions.Events[eventCount].BoundaryStep < step))
            {
                throw new ArgumentException("A capture's applied-event count does not describe its completed boundary.", nameof(captures));
            }
            SimulationConfiguration expectedConfiguration = eventCount == 0
                ? initialConfiguration : actions.Events[eventCount - 1].Configuration;
            if (!capture.Configuration.EquivalentTo(expectedConfiguration))
            {
                throw new ArgumentException("A capture's settings disagree with its applied action boundary.", nameof(captures));
            }
            for (int eventIndex = previousEvents; eventIndex < eventCount; eventIndex++)
            {
                if (actions.Events[eventIndex].ReplacementState is { } replacement)
                {
                    membership = Membership(replacement);
                }
                actions.Events[eventIndex].StateDelta?.ApplyMembership(membership);
            }
            if (capture.State.Count != membership.Count)
            {
                throw new ArgumentException("A capture's membership disagrees with its recorded structural edits.", nameof(captures));
            }
            for (int index = 0; index < capture.State.Count; index++)
            {
                if (!membership.TryGetValue(capture.State.Ids[index], out int species)
                    || species != capture.State.Species[index])
                {
                    throw new ArgumentException("A capture changes identity or species without a recorded structural edit.", nameof(captures));
                }
            }
            previousStep = step;
            previousTime = time;
            previousEvents = eventCount;
        }
        SimulationCapture terminal = ownedCaptures[^1];
        if (terminal.State.Step != actions.TerminalStep || terminal.AppliedEventCount != actions.Events.Count)
        {
            throw new ArgumentException("The final capture must include every terminal action.", nameof(captures));
        }
        previousStep = 0;
        previousEvents = 0;
        foreach (ExecutionTransition transition in ownedTransitions)
        {
            ArgumentNullException.ThrowIfNull(transition);
            ArgumentNullException.ThrowIfNull(transition.From);
            ArgumentNullException.ThrowIfNull(transition.To);
            if (!double.IsFinite(transition.Time) || transition.Time < 0
                || transition.BoundaryStep < previousStep || transition.AppliedEventCount < previousEvents
                || !ownedCaptures.Any(capture => capture.State.Step == transition.BoundaryStep
                    && capture.State.Time == transition.Time && capture.AppliedEventCount == transition.AppliedEventCount
                    && capture.Execution == transition.To))
            {
                throw new ArgumentException("Every ordered execution transfer requires its actual destination capture.", nameof(transitions));
            }
            previousStep = transition.BoundaryStep;
            previousEvents = transition.AppliedEventCount;
        }
        Actions = actions;
        Captures = new ReadOnlyCollection<SimulationCapture>(ownedCaptures);
        Transitions = new ReadOnlyCollection<ExecutionTransition>(ownedTransitions);
    }

    /// <summary>Gets action history for explicitly requested re-simulation.</summary>
    public SessionDefinition Actions { get; }
    /// <summary>Gets actual saved boundaries in authoritative recording order.</summary>
    public IReadOnlyList<SimulationCapture> Captures { get; }
    /// <summary>Gets actual execution transfers and precision conversions.</summary>
    public IReadOnlyList<ExecutionTransition> Transitions { get; }
    /// <summary>Gets the mandatory exact terminal state, including terminal edits.</summary>
    public SimulationCapture TerminalCapture => Captures[^1];

    private static Dictionary<long, int> Membership(ParticleSnapshot state)
    {
        Dictionary<long, int> result = new(state.Count);
        for (int index = 0; index < state.Count; index++)
        {
            result.Add(state.Ids[index], state.Species[index]);
        }
        return result;
    }
}
