using System.Diagnostics;
using ParticleLife.Core.Execution;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using ParticleLife.Viewer;

namespace ParticleLife.Viewer.Tests;

public sealed class DiscoveryCapturedSessionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "particle-life-captured-library-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedSessionSurvivesResetAndDisposalOfItsOwner(bool useGpu)
    {
        PreparedDiscoverySave? prepared = null;
        using (EditorSession session = new(CreateEditor(), runInBackground: true))
        {
            if (useGpu)
            {
                await Complete(session, session.SwitchToGpu((configuration, state) => new LibraryResident(configuration, state), true));
            }
            await Complete(session, session.Run("Change recorded rule", owner => owner.SetMatrixCoefficient(0, 0, 0.4), requiresCapture: false));
            await Step(session, 1);
            await Complete(session, session.Run("Freeze session", owner => prepared = owner.PrepareDiscoverySave(DiscoveryKind.Session, "before reset")));
            await Complete(session, session.Run("Reset active discovery", owner => owner.Reset(), requiresCapture: false));
            Assert.Equal(0, session.Publication.Step);
            Assert.Equal(0, session.SessionEventCount);
        }
        string path = Path.Combine(directory, "frozen.json");
        DiscoveryFileOutcome outcome = await Task.Run(() => prepared!.Write(path));
        SessionDefinition actions;
        if (useGpu)
        {
            CapturedSessionDefinition captured = ExecutionCaptureFile.Load(path);
            actions = captured.Actions;
            Assert.Equal(DiscoveryKind.CapturedSession, outcome.Kind);
            Assert.Equal(1, captured.TerminalCapture.State.Step);
            Assert.Equal(0.375, captured.TerminalCapture.State.Positions[0].X);
            Assert.NotEmpty(captured.Transitions);
            Assert.True(captured.Captures.Count >= 2);
        }
        else
        {
            actions = SessionFile.Load(path);
            Assert.Equal(DiscoveryKind.Session, outcome.Kind);
        }
        Assert.Equal("before reset", actions.InitialExperiment.Name);
        Assert.Equal(1, actions.TerminalStep);
        Assert.Single(actions.Events);
        Assert.Equal(0.4, actions.Events[0].Configuration.Matrix[0, 0]);
        Assert.Equal(0, actions.InitialExperiment.Matrix[0, 0]);
    }

    [Fact]
    public async Task PresetInitialAndSelectiveComponentsDoNotReadBackResidentState()
    {
        using EditorSession session = new(CreateEditor(), runInBackground: true);
        LibraryResident? resident = null;
        Task transfer = session.SwitchToGpu((configuration, state) => resident = new(configuration, state), true);
        await Complete(session, transfer);
        await Complete(session, session.Run("Disable periodic capture", owner => owner.SetCaptureCadence(0), requiresCapture: false));
        await Step(session, 1);
        int observations = resident!.ObservationCalls;
        PreparedDiscoverySave? preset = null, initial = null;
        Task preparePreset = session.Run("Prepare preset", owner => preset = owner.PrepareDiscoverySave(DiscoveryKind.Preset, "recipe"),
            requiresCapture: PreparedDiscoverySave.RequiresCurrentCapture(DiscoveryKind.Preset));
        Task prepareInitial = session.Run("Prepare initial", owner => initial = owner.PrepareDiscoverySave(DiscoveryKind.Initial, "initial"),
            requiresCapture: PreparedDiscoverySave.RequiresCurrentCapture(DiscoveryKind.Initial));
        await Complete(session, preparePreset); await Complete(session, prepareInitial);
        PresetDefinition components = new("components", new(session.Configuration.Parameters,
            new InteractionMatrix(1, [0.7], new PairInteractionRanges(1, [0.02], [0.2])), session.Configuration.TimeStep),
            [9], new GenerationSettings { Seed = 99, Placement = PlacementKind.Ring }, new AppearanceSettings([0x123456]), new());
        await Complete(session, session.Run("Apply selected settings", owner => owner.ApplySelectivePreset(components,
            PresetComponents.Coefficients | PresetComponents.Radii | PresetComponents.Palette | PresetComponents.Generation, [0]), requiresCapture: false));
        Assert.Equal(observations, resident.ObservationCalls);
        Assert.Equal(0, resident.CaptureRequests);
        Assert.Equal(0, resident.FullStateReadbacks);
        Assert.False(resident.HasCurrentCapture);
        await Task.Run(() => preset!.Write(Path.Combine(directory, "recipe.json")));
        await Task.Run(() => initial!.Write(Path.Combine(directory, "initial.json")));
        Assert.Equal(2, ExperimentFile.Load(Path.Combine(directory, "initial.json")).InitialState.Count);
        Assert.Equal(DiscoveryKind.Initial, DiscoveryLibrary.Inspect(Path.Combine(directory, "initial.json"))!.Metadata.Kind);
        Assert.Equal(0, resident.FullStateReadbacks);
    }

    [Fact]
    public async Task CapturedPreparedSaveShareAndRestoreKeepTerminalValuesWhileCpuResimulationIsExplicitlyDifferent()
    {
        Directory.CreateDirectory(directory);
        using EditorSession session = new(CreateEditor(), runInBackground: true);
        LibraryResident? resident = null;
        await Complete(session, session.SwitchToGpu((configuration, state) => resident = new(configuration, state), true));
        await Complete(session, session.Run("Disable optional capture", owner => owner.SetCaptureCadence(0), requiresCapture: false));
        await Step(session, 1);
        PreparedDiscoverySave? prepared = null;
        await Complete(session, session.Run("Prepare session", owner => prepared = owner.PrepareDiscoverySave(DiscoveryKind.Session, "GPU session"),
            requiresCapture: PreparedDiscoverySave.RequiresCurrentCapture(DiscoveryKind.Session)));
        Assert.Equal(1, resident!.FullStateReadbacks);
        await Step(session, 2); // Serialization must retain the already prepared step-one snapshot.
        string path = Path.Combine(directory, "gpu.session.json"), archive = Path.Combine(directory, "gpu.plshare"), imported = Path.Combine(directory, "imported.json");
        DiscoveryFileOutcome saved = await Task.Run(() => prepared!.Write(path));
        CapturedSessionDefinition captured = ExecutionCaptureFile.Load(path);
        Assert.Equal(1, captured.TerminalCapture.State.Step);
        Assert.Equal(DiscoveryKind.CapturedSession, saved.Kind);
        Assert.Equal(StatePrecision.Float32, captured.TerminalCapture.Execution.Precision);
        Assert.Contains("TERMINAL", saved.Entry!.Metadata.PreviewLabel);
        Assert.Equal((float)captured.TerminalCapture.State.Positions[0].X, saved.Entry.Metadata.Points[0].X);
        Assert.NotEqual((float)captured.Actions.InitialExperiment.InitialState.Positions[0].X, saved.Entry.Metadata.Points[0].X);
        await Task.Run(() => DiscoveryShare.Export(path, archive), TestContext.Current.CancellationToken);
        DiscoveryFileOutcome shared = await Task.Run(() => DiscoveryShare.ImportDetailed(archive, imported));
        Assert.Equal(DiscoveryKind.CapturedSession, shared.Kind);
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(imported));
        Assert.Equal(File.ReadAllBytes(path + ".viewer.json"), File.ReadAllBytes(imported + ".viewer.json"));
        PreparedDiscoveryLoad restore = await Task.Run(() => ExperimentEditor.PrepareDiscoveryLoad(imported, DiscoveryLoadAction.RestoreCapturedState));
        PreparedDiscoveryLoad resimulate = await Task.Run(() => ExperimentEditor.PrepareDiscoveryLoad(imported, DiscoveryLoadAction.ResimulateCapturedOnCpu));
        Assert.ThrowsAny<Exception>(() => ExperimentEditor.PrepareDiscoveryLoad(imported, DiscoveryLoadAction.ReplayActions));
        Assert.Contains("Future CPU motion may differ", restore.Notice!);
        Assert.Contains("does not restore", resimulate.Notice!);
        File.Delete(imported + DiscoveryLibrary.MetadataSuffix);
        Assert.Equal(DiscoveryKind.CapturedSession, DiscoveryLibrary.Inspect(imported)!.Metadata.Kind);
        DiscoveryLibrary.Write(imported, saved.Entry.Metadata with { Kind = DiscoveryKind.Session, PreviewLabel = "Session INITIAL state", FileBytes = 0, FileModifiedUtcTicks = 0 });
        DiscoveryEntry migrated = DiscoveryLibrary.Inspect(imported)!;
        Assert.Equal(DiscoveryKind.CapturedSession, migrated.Metadata.Kind);
        Assert.Empty(migrated.Metadata.Points); // Never mislabel an old initial thumbnail as terminal.
        foreach (string file in Directory.EnumerateFiles(directory)) { File.Delete(file); }
        await Complete(session, session.Run("Change future recipe", owner => owner.SetGeneration(new() { Seed = 999 }), requiresCapture: false));
        await Complete(session, session.Run("Restore stored capture", owner => restore.Apply(owner),
            requiresCapture: PreparedDiscoveryLoad.RequiresCurrentCapture(DiscoveryLoadAction.RestoreCapturedState)));
        Assert.Null(session.Resident);
        Assert.Equal(ExecutionHardware.Cpu, session.Publication.Execution.Hardware);
        Assert.Equal(captured.TerminalCapture.State.Time, session.Publication.Time);
        Assert.Equal(77, session.Generation.Seed);
        Assert.Equal(7, session.Appearance.ParticleRadius);
        Assert.Equal(new[] { 9 }, session.Populations.ToArray());
        ParticleSnapshot? restored = null;
        await Complete(session, session.Run("Observe CPU restore", owner => restored = owner.CaptureCurrentExperiment("restored").InitialState, requiresCapture: false));
        Assert.Equal(captured.TerminalCapture.State.Positions.ToArray(), restored!.Positions.ToArray());
        Assert.Equal(captured.TerminalCapture.State.Velocities.ToArray(), restored.Velocities.ToArray());
        Assert.Equal(captured.Actions.Events.Count, session.SessionEventCount);
        Assert.Equal(1, resident.FullStateReadbacks); // Restoring does not download the old step-two GPU state.

        using EditorSession cpu = new(CreateEditor(), runInBackground: true);
        LibraryResident? resimulationSource = null;
        await Complete(cpu, cpu.SwitchToGpu((configuration, state) => resimulationSource = new(configuration, state), true));
        await Complete(cpu, cpu.Run("Disable periodic capture", owner => owner.SetCaptureCadence(0), requiresCapture: false));
        await Step(cpu, 1);
        await Complete(cpu, cpu.Run("Re-simulate CPU actions", owner => resimulate.Apply(owner),
            requiresCapture: PreparedDiscoveryLoad.RequiresCurrentCapture(DiscoveryLoadAction.ResimulateCapturedOnCpu)));
        await PumpUntil(cpu, () => cpu.Resident is null && !cpu.IsReplaying && cpu.Publication.Step == captured.Actions.TerminalStep);
        ParticleSnapshot? result = null;
        await Complete(cpu, cpu.Run("Observe CPU result", owner => result = owner.CaptureCurrentExperiment("CPU result").InitialState, requiresCapture: false));
        using Simulation expected = SessionReplay.Replay(captured.Actions);
        Assert.Equal(expected.Observe().Positions.ToArray(), result!.Positions.ToArray());
        Assert.NotEqual(captured.TerminalCapture.State.Positions[0].X, result.Positions[0].X);
        Assert.Equal(77, cpu.Generation.Seed);
        Assert.Equal(1, resimulationSource!.FullStateReadbacks);
    }

    private static ExperimentEditor CreateEditor()
    {
        ParticleSnapshot initial = new([11, 27], [new(0.25, 0.5), new(0.75, 0.5)], [new(0.125, 0), new(-0.125, 0)], [0, 0]);
        ExperimentDefinition experiment = new("library fixture", new() { ForceScale = 0, WallStrength = 0, DampingHalfLife = null },
            new InteractionMatrix(1, [0]), initial, 0.02, new("manual", "1", 77, "test"));
        ExperimentEditor editor = new(new PlaybackController(experiment, diagnosticEverySteps: 0));
        editor.Playback.SetPaused(true); editor.SetGeneration(new() { Seed = 77 }); editor.SetPopulationRecipe([9]);
        editor.SetAppearance(editor.Appearance with { ParticleRadius = 7 });
        return editor;
    }
    private static async Task Complete(EditorSession session, Task operation)
    {
        await PumpUntil(session, () => operation.IsCompleted); await operation;
    }
    private static async Task Step(EditorSession session, long step)
    {
        session.Playback.SingleStep(); await PumpUntil(session, () => session.Publication.Step == step && !session.HasPendingCommands);
    }
    private static async Task PumpUntil(EditorSession session, Func<bool> complete)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (!complete())
        {
            session.Pump(); Assert.True(timeout.Elapsed < TimeSpan.FromSeconds(10), "Library resident fixture did not reach its boundary.");
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
        session.Pump();
    }
    public void Dispose() { if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); } }

    /// <summary>A capture-enforcing resident fixture with deliberately different arithmetic to distinguish restore from re-simulation.</summary>
    private sealed class LibraryResident : IResidentSimulation
    {
        private ParticleSnapshot state;
        private int[] counts = [];
        internal int ObservationCalls, CaptureRequests;
        internal LibraryResident(SimulationConfiguration configuration, ParticleSnapshot state) { Configuration = configuration; this.state = state; Replace(configuration, state); }
        public SimulationConfiguration Configuration { get; private set; }
        public SimulationBoundary Boundary => new(state.Count, state.Step, state.Time);
        public ExecutionProvenance Execution { get; } = new(ExecutionHardware.Gpu, StatePrecision.Float32, "library-fixture/1", "test", "fixture GPU", "fake API", null, false);
        public bool IsStepPending { get; private set; }
        public bool IsCapturePending { get; private set; }
        public bool HasCurrentCapture { get; private set; } = true;
        public ReadOnlyMemory<int> SpeciesCounts => counts;
        public ResidentSummary? LatestSummary => null;
        public long FullStateReadbacks { get; private set; }
        public long MetadataReadbackBytes => 0;
        public void SubmitStep() => IsStepPending = true;
        public bool PollStep()
        {
            if (!IsStepPending) { return false; }
            Vector2d[] positions = state.Positions.ToArray();
            for (int i = 0; i < positions.Length; i++) { positions[i] = new((float)(positions[i].X + 0.125), (float)positions[i].Y); }
            state = new(state.Ids, positions, state.Velocities, state.Species, state.Time + Configuration.TimeStep, state.Step + 1);
            IsStepPending = false; HasCurrentCapture = false; return true;
        }
        public void CancelPendingStep() => IsStepPending = false;
        public void BeginCapture() { CaptureRequests++; IsCapturePending = true; }
        public ParticleSnapshot? PollCapture()
        {
            if (!IsCapturePending) { return null; }
            IsCapturePending = false; HasCurrentCapture = true; FullStateReadbacks++; return state;
        }
        public ParticleStateView ObserveCaptured()
        {
            ObservationCalls++; if (!HasCurrentCapture) { throw new StateCaptureRequiredException(); }
            return state.Observe();
        }
        public void Reconfigure(SimulationConfiguration configuration, ParticleSnapshot? replacementState = null)
        {
            Configuration = configuration; if (replacementState is not null) { Replace(configuration, replacementState); }
        }
        public void Replace(SimulationConfiguration configuration, ParticleSnapshot replacement)
        {
            Configuration = configuration; state = replacement; counts = new int[configuration.Matrix.SpeciesCount];
            foreach (int species in state.Species) { counts[species]++; }
            HasCurrentCapture = true;
        }
        public void RequestSummary() { }
        public void PollSummary() { }
        public void Dispose() { }
    }
}
