using System.Diagnostics;
using ParticleLife.Core.Execution;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using ParticleLife.Viewer;

namespace ParticleLife.Viewer.Tests;

public sealed class ResidentExecutionTests
{
    [Fact]
    public async Task FailedHandoffKeepsActualEditorHistoryBaselineAndLaterCommandOrder()
    {
        ExperimentEditor original = CreateEditor();
        ExperimentDefinition baseline = original.Playback.Experiment;
        using EditorSession session = new(original, runInBackground: true);
        using ManualResetEventSlim entered = new(), release = new();
        Task blocked = session.Run("Hold CPU boundary", _ => { entered.Set(); release.Wait(); });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Task before = session.Run("Before transfer", editor => SetForce(editor, 2), requiresCapture: false);
        double factoryForce = 0;
        Task transfer = session.SwitchToGpu((configuration, _) =>
        {
            factoryForce = configuration.Parameters.ForceScale;
            throw new NotSupportedException("Injected allocation failure");
        }, allowPrecisionConversion: true);
        Task after = session.Run("After transfer", editor => SetForce(editor, 3), requiresCapture: false);
        release.Set();
        await PumpUntil(session, () => blocked.IsCompleted && before.IsCompleted && transfer.IsCompleted && after.IsCompleted);
        Assert.True(transfer.IsFaulted);
        Assert.Null(session.Resident);
        Assert.Equal(2, factoryForce);
        Assert.Equal(3, session.Publication.Configuration.Parameters.ForceScale);
        Assert.Same(baseline, original.Playback.Experiment);
        Assert.True(session.CanUndo);
        Task undo = session.Run("Undo after rejected transfer", editor => editor.Undo());
        await PumpUntil(session, () => undo.IsCompleted);
        Assert.Equal(2, session.Publication.Configuration.Parameters.ForceScale);
    }

    [Fact]
    public async Task ScalarPresentationAndNormalStepsNeverRequestStateCapture()
    {
        using EditorSession session = new(CreateEditor(), runInBackground: true);
        FakeResident? resident = null;
        Task transfer = session.SwitchToGpu((configuration, state) => resident = new(configuration, state), true);
        await PumpUntil(session, () => transfer.IsCompleted);
        await transfer;
        Task cadence = session.Run("Disable optional captures", editor => editor.SetCaptureCadence(0), requiresCapture: false);
        await PumpUntil(session, () => cadence.IsCompleted);
        for (int step = 1; step <= 5; step++)
        {
            session.Playback.SingleStep();
            await PumpUntil(session, () => session.Publication.Step == step && !session.HasPendingCommands);
        }
        int observations = resident!.ObservationCalls;
        session.ApplyConfiguration(new(session.Configuration.Parameters with { ForceScale = 2 }, session.Configuration.Matrix, session.Configuration.TimeStep));
        session.SetAppearance(session.Appearance with { Camera = new() { Zoom = 2 } });
        session.SetPlayback(session.Playback.Settings with { Speed = 2 });
        session.EnablePairRanges(true);
        session.SetSelectedPairRanges([0], 0.025, 0.125);
        session.RandomizeInteractionSelection([0], InteractionRandomization.Rules, 73, 0.1, 0.2);
        await PumpUntil(session, () => !session.HasPendingCommands);
        Assert.Equal(0.125, session.Publication.Configuration.Matrix.Ranges!.CutoffRadii[0]);
        Assert.Equal(observations, resident.ObservationCalls);
        Assert.Equal(0, resident.FullStateReadbacks);
        Assert.Equal(0, resident.CaptureRequests);
        Assert.Equal(5, session.Publication.Step);
        Assert.Equal(2, session.Appearance.Camera.Zoom);
        Assert.Null(session.AcquireRenderFrame());
        Task structural = session.Run("Explicit population edit", editor => editor.ApplyPopulation(1, [1]));
        await PumpUntil(session, () => structural.IsCompleted);
        await structural;
        Assert.Equal(1, resident.FullStateReadbacks);
        Assert.Equal(1, session.Publication.Count);
        Assert.Equal(5, session.Publication.Step);
        session.Playback.SingleStep();
        await PumpUntil(session, () => session.Publication.Step == 6 && !session.HasPendingCommands);
        Task toCpu = session.SwitchToCpu();
        await PumpUntil(session, () => toCpu.IsCompleted);
        await toCpu;
        Assert.Null(session.Resident);
        Assert.Equal(ExecutionHardware.Cpu, session.Publication.Execution.Hardware);
        Assert.Equal(6, session.Publication.Step);
        Assert.Equal(2, resident.FullStateReadbacks);
    }

    [Fact]
    public async Task TerminalSaveContainsActualFloatStateAndAllTerminalEdits()
    {
        using EditorSession session = new(CreateEditor(), runInBackground: true);
        Task transfer = session.SwitchToGpu((configuration, state) => new FakeResident(configuration, state), true);
        await PumpUntil(session, () => transfer.IsCompleted);
        await transfer;
        session.Playback.SingleStep();
        await PumpUntil(session, () => session.Publication.Step == 1 && !session.HasPendingCommands);
        session.ApplyConfiguration(new(session.Configuration.Parameters with { ForceScale = 3 }, session.Configuration.Matrix, session.Configuration.TimeStep));
        CapturedSessionDefinition? saved = null;
        Task save = session.Run("Capture terminal", editor => saved = editor.CaptureRecordedSession("actual GPU state"));
        await PumpUntil(session, () => save.IsCompleted);
        await save;
        CapturedSessionDefinition restored = ExecutionCaptureFile.Deserialize(ExecutionCaptureFile.Serialize(saved!));
        Assert.Equal(session.Publication.Step, restored.TerminalCapture.State.Step);
        Assert.Equal(session.Publication.Time, restored.TerminalCapture.State.Time);
        Assert.Equal(3, restored.TerminalCapture.Configuration.Parameters.ForceScale);
        Assert.Equal(restored.Actions.Events.Count, restored.TerminalCapture.AppliedEventCount);
        Assert.True(CaptureTransfer.IsExactFloat32(restored.TerminalCapture.State));
        Assert.Equal(new long[] { long.MinValue, long.MaxValue }, restored.TerminalCapture.State.Ids.ToArray());
        Assert.False(restored.TerminalCapture.Execution.RepeatableActions);
        ExperimentDefinition? pattern = null;
        Task patternCapture = session.Run("Save GPU pattern", owner => pattern = owner.CaptureCurrentExperiment("GPU pattern"));
        await PumpUntil(session, () => patternCapture.IsCompleted);
        await patternCapture;
        ExperimentDefinition loadedPattern = ExperimentFile.Deserialize(ExperimentFile.Serialize(pattern!));
        Assert.Equal(StatePrecision.Float32, loadedPattern.Provenance!.SourceExecution!.Precision);
        Assert.Equal(ExecutionHardware.Gpu, loadedPattern.Provenance.SourceExecution.Hardware);
        Assert.Equal(restored.TerminalCapture.State.Positions.ToArray(), loadedPattern.InitialState.Positions.ToArray());
    }

    [Fact]
    public async Task PrecisionTransferRejectsAnExceededRecordingBudgetBeforeAdoption()
    {
        ExperimentEditor original = CreateEditor();
        original.SetRecordingBudgets(1024, 1024);
        SetForce(original, 1); SetForce(original, 2); SetForce(original, 3);
        long recorded = original.Recording.ActionBytes;
        using EditorSession session = new(original, runInBackground: true);
        Task transfer = session.SwitchToGpu((configuration, state) => new FakeResident(configuration, state), true);
        await PumpUntil(session, () => transfer.IsCompleted);
        Assert.True(transfer.IsFaulted);
        Assert.Null(session.Resident);
        Assert.Equal(ExecutionHardware.Cpu, session.Publication.Execution.Hardware);
        Assert.Equal(recorded, session.Recording.ActionBytes);
        Assert.Equal(3, session.SessionEventCount);
        double retainedX = 0;
        Task observed = session.Run("Observe retained CPU state", owner => retainedX = owner.Playback.Observe().Positions[0].X);
        await PumpUntil(session, () => observed.IsCompleted);
        await observed;
        Assert.Equal(0.1, retainedX);
    }

    [Fact]
    public async Task DeviceFailureRecoversOnlyOwnedCheckpointAndMakesClockRollbackExplicit()
    {
        using EditorSession session = new(CreateEditor(), runInBackground: true);
        FakeResident? resident = null;
        Task transfer = session.SwitchToGpu((configuration, state) => resident = new(configuration, state), true);
        await PumpUntil(session, () => transfer.IsCompleted);
        await transfer;
        long checkpointActionBytes = session.Recording.ActionBytes;
        long baseline = session.Publication.BaselineVersion;
        session.Playback.SingleStep();
        await PumpUntil(session, () => session.Publication.Step == 1 && !session.HasPendingCommands);
        session.ApplyConfiguration(new(session.Configuration.Parameters with { ForceScale = 2 }, session.Configuration.Matrix, session.Configuration.TimeStep));
        await PumpUntil(session, () => !session.HasPendingCommands);
        Assert.True(session.Recording.ActionBytes > checkpointActionBytes);
        resident!.Lost = true;
        session.Playback.SingleStep();
        await PumpUntil(session, () => session.Publication.DeviceFaulted);
        Assert.Equal(1, session.Publication.Step);
        Assert.True(session.Playback.IsPaused);
        Task recovery = session.SwitchToCpu(recoverLatestCapture: true);
        await PumpUntil(session, () => recovery.IsCompleted);
        await recovery;
        Assert.Null(session.Resident);
        Assert.Equal(0, session.Publication.Step);
        Assert.Contains("uncaptured continuation was discarded", session.Publication.CaptureRecording!.Notice);
        Assert.Equal(0, resident.FullStateReadbacks);
        Assert.Equal(checkpointActionBytes, session.Recording.ActionBytes);
        Assert.Equal(0, session.Recording.UndoBytes);
        Assert.True(session.Publication.BaselineVersion > baseline);
    }

    [Fact]
    public async Task ExactGpuRestoreRejectsLossyDowngradeBeforeFactoryRuns()
    {
        using EditorSession session = new(CreateEditor(), runInBackground: true);
        bool called = false;
        Task transfer = session.SwitchToGpu((configuration, state) => { called = true; return new FakeResident(configuration, state); });
        await PumpUntil(session, () => transfer.IsCompleted);
        Assert.True(transfer.IsFaulted);
        Assert.False(called);
        Assert.Equal(ExecutionHardware.Cpu, session.Publication.Execution.Hardware);
        Assert.Equal(0, session.Publication.Step);
    }

    [Theory]
    [InlineData("initial")]
    [InlineData("replacement")]
    [InlineData("delta")]
    public void CapturedRestoreReservesIdentitiesRemovedEarlierInTheRecordedHistory(string source)
    {
        SimulationConfiguration configuration = new(new ModelParameters { ForceScale = 0 }, new(1, [0]), 1.0 / 64);
        ParticleSnapshot low = new([1], [new(0.25, 0.25)], [default], [0]);
        ParticleSnapshot high = new([1, 900], [new(0.25, 0.25), new(0.75, 0.75)], [default, default], [0, 0]);
        ExperimentDefinition baseline = new("identity history", configuration.Parameters, configuration.Matrix,
            source == "initial" ? high : low, configuration.TimeStep, new("actual", "1", null, "test"));
        List<SessionEvent> edits = [];
        if (source == "replacement") { edits.Add(new(0, configuration, high)); }
        if (source == "delta") { edits.Add(new(0, configuration, stateDelta: ParticleStateDelta.TryCreate(low, high))); }
        edits.Add(source == "delta" ? new(0, configuration, stateDelta: ParticleStateDelta.TryCreate(high, low)) : new(0, configuration, low));
        CapturedSessionDefinition saved = new(new(baseline, edits, 0),
            [new(configuration, low, ExecutionProvenance.Cpu(configuration), edits.Count, "terminal")], []);
        using PlaybackController playback = new(baseline, diagnosticEverySteps: 0);
        ExperimentEditor editor = new(playback);
        editor.RestoreCapturedSession(saved);
        editor.ApplyPopulation(1, [2]);
        Assert.Equal(new long[] { 1, 901 }, playback.Observe().Ids.ToArray());
    }

    private static ExperimentEditor CreateEditor()
    {
        ParticleSnapshot state = new([long.MinValue, long.MaxValue], [new(0.1, 0.25), new(0.75, 0.5)], [new(0.125, 0), default], [0, 0]);
        ExperimentDefinition experiment = new("resident fixture", new ModelParameters { ForceScale = 0 }, new(1, [0]), state,
            1.0 / 64, new("actual", "1", null, "test"));
        PlaybackController playback = new(experiment, diagnosticEverySteps: 0);
        playback.SetPaused(true);
        return new(playback);
    }

    private static void SetForce(ExperimentEditor editor, double value) => editor.ApplyConfiguration(new(
        editor.Configuration.Parameters with { ForceScale = value }, editor.Configuration.Matrix, editor.Configuration.TimeStep));

    private static async Task PumpUntil(EditorSession session, Func<bool> completed)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (!completed())
        {
            session.Pump();
            Assert.True(timeout.Elapsed < TimeSpan.FromSeconds(8), "Resident test did not reach its completed boundary.");
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
        session.Pump();
    }

    private sealed class FakeResident : IResidentSimulation
    {
        private ParticleSnapshot state;
        private int[] speciesCounts = [];
        internal int CaptureRequests, ObservationCalls;
        internal bool Lost;
        internal FakeResident(SimulationConfiguration configuration, ParticleSnapshot state) { Configuration = configuration; this.state = state; Replace(configuration, state); }
        public SimulationConfiguration Configuration { get; private set; }
        public SimulationBoundary Boundary => new(state.Count, state.Step, state.Time);
        public ExecutionProvenance Execution { get; } = new(ExecutionHardware.Gpu, StatePrecision.Float32, "fake/1", "test", "fake-device", "fake-api", null, false);
        public bool IsStepPending { get; private set; }
        public bool IsCapturePending { get; private set; }
        public bool HasCurrentCapture { get; private set; } = true;
        public ReadOnlyMemory<int> SpeciesCounts => speciesCounts;
        public ResidentSummary? LatestSummary => null;
        public long FullStateReadbacks { get; private set; }
        public long MetadataReadbackBytes => 0;
        public void SubmitStep() => IsStepPending = true;
        public bool PollStep()
        {
            if (!IsStepPending) { return false; }
            if (Lost) { throw new InvalidOperationException("Injected device loss"); }
            Vector2d[] positions = state.Positions.ToArray();
            for (int index = 0; index < positions.Length; index++)
            {
                positions[index] = new((float)(positions[index].X + Configuration.TimeStep * state.Velocities[index].X),
                    (float)(positions[index].Y + Configuration.TimeStep * state.Velocities[index].Y));
            }
            state = new(state.Ids, positions, state.Velocities, state.Species, state.Time + Configuration.TimeStep, state.Step + 1);
            HasCurrentCapture = false;
            IsStepPending = false;
            return true;
        }
        public void CancelPendingStep() => IsStepPending = false;
        public void BeginCapture() { CaptureRequests++; IsCapturePending = true; }
        public ParticleSnapshot? PollCapture()
        {
            if (!IsCapturePending) { return null; }
            if (Lost) { throw new InvalidOperationException("Injected capture loss"); }
            IsCapturePending = false;
            HasCurrentCapture = true;
            FullStateReadbacks++;
            return state;
        }
        public ParticleStateView ObserveCaptured()
        {
            ObservationCalls++;
            if (!HasCurrentCapture) { throw new StateCaptureRequiredException(); }
            return state.Observe();
        }
        public void Reconfigure(SimulationConfiguration configuration, ParticleSnapshot? replacementState = null)
        {
            Configuration = configuration;
            if (replacementState is not null) { Replace(configuration, replacementState); }
        }
        public void Replace(SimulationConfiguration configuration, ParticleSnapshot replacement)
        {
            Configuration = configuration;
            state = replacement;
            speciesCounts = new int[configuration.Matrix.SpeciesCount];
            foreach (int species in state.Species) { speciesCounts[species]++; }
            HasCurrentCapture = true;
        }
        public void RequestSummary() { }
        public void PollSummary() { }
        public void Dispose() { }
    }
}
