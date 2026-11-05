using ParticleLife.Core.Execution;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Viewer;

public sealed partial class PlaybackController
{
    private IResidentSimulation? resident;

    /// <summary>Gets the resident device owner, or null while CPU execution is active.</summary>
    /// <remarks>The caller may query metadata on the owning thread; native rendering must obey the
    /// implementation's completed-buffer lifetime. No API here creates a graphics dependency in Core.</remarks>
    public IResidentSimulation? Resident => resident;

    /// <summary>Gets completed count and clock without borrowing or downloading particle storage.</summary>
    public SimulationBoundary Boundary
    {
        get
        {
            if (resident is not null) { return resident.Boundary; }
            ParticleStateView state = simulation.Observe();
            return new(state.Count, state.Step, state.Time);
        }
    }

    /// <summary>Gets actual hardware, precision and implementation provenance independently of CPU search settings.</summary>
    public ExecutionProvenance Execution => resident?.Execution ?? ExecutionProvenance.Cpu(Configuration);

    /// <summary>Adopts a fully constructed resident candidate at the same completed boundary.</summary>
    /// <param name="candidate">New owned implementation with current captured upload and equivalent configuration.</param>
    /// <remarks>Caller constructs and validates before this method. A rejected candidate remains caller-owned;
    /// the existing CPU editor, baseline and undo history are untouched. A successful transfer disposes CPU workers.</remarks>
    internal void AdoptResident(IResidentSimulation candidate)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(candidate);
        if (resident is not null || candidate.IsStepPending || candidate.IsCapturePending || !candidate.HasCurrentCapture
            || candidate.Boundary != Boundary || !candidate.Configuration.EquivalentTo(Configuration))
        {
            throw new ArgumentException("A resident transfer must start at the current captured boundary and settings.", nameof(candidate));
        }
        resident = candidate;
        simulation.Dispose();
        PublicationVersion++;
        MembershipVersion++;
        ClearPlayback();
    }

    /// <summary>Constructs a CPU owner from an explicit actual capture before releasing device state.</summary>
    internal void ReturnToCpu(ParticleSnapshot state, SimulationConfiguration? capturedConfiguration = null)
    {
        EnsureActive();
        SimulationConfiguration configuration = capturedConfiguration ?? Configuration;
        Simulation candidate = new(state, configuration.Parameters, configuration.Matrix, configuration.TimeStep,
            configuration.Backend, configuration.WorkerCount);
        IResidentSimulation? previous = resident;
        simulation = candidate;
        resident = null;
        PublicationVersion++;
        MembershipVersion++;
        ClearPlayback();
        previous?.Dispose();
    }

    /// <summary>Converts explicit structural edit results to the active storage precision before recording them.</summary>
    /// <remarks>Choosing GPU float32 execution permits float32 arithmetic for subsequent structural edits.
    /// Actual converted values, rather than the double construction draft, must enter action/undo records.</remarks>
    internal ParticleSnapshot? PrepareReplacement(ParticleSnapshot? state) => resident is null || state is null
        ? state : CaptureTransfer.Convert(state, resident.Execution.Precision, allowPrecisionConversion: true);

    /// <summary>Bounds scalar playback demand while one slow GPU step spans several presentation turns.</summary>
    /// <remarks>This discards excess requested wall-time catch-up, never numerical work or neighbours.
    /// The retained demand contains at most the configured step cap plus its fractional remainder.</remarks>
    internal void LimitResidentPlaybackDemand()
    {
        double retained = Configuration.TimeStep * MaxStepsPerFrame + PendingPlaybackSeconds % Configuration.TimeStep;
        if (PendingPlaybackSeconds > retained)
        {
            DroppedPlaybackSeconds += PendingPlaybackSeconds - retained;
            PendingPlaybackSeconds = retained;
        }
    }

    private bool AdvanceResidentStep()
    {
        IResidentSimulation owner = resident!;
        owner.PollSummary();
        if (owner.IsStepPending)
        {
            if (!owner.PollStep()) { return false; }
            PublicationVersion++;
            if (diagnosticEverySteps > 0 && Boundary.Step % diagnosticEverySteps == 0)
            {
                owner.RequestSummary();
            }
            return true;
        }
        if (IsPaused || owner.IsCapturePending || PendingPlaybackSeconds < Configuration.TimeStep) { return false; }
        owner.SubmitStep();
        PendingPlaybackSeconds -= Configuration.TimeStep;
        return false;
    }
}
