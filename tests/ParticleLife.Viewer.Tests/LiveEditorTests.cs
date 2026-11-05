using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using ParticleLife.Viewer;

namespace ParticleLife.Viewer.Tests;

public sealed class LiveEditorTests
{
    [Fact]
    public void PreserveDensityResizesWorldAndPopulationInOneReplayableUndo()
    {
        using PlaybackController playback = CreatePlayback();
        ExperimentEditor editor = new(playback);
        playback.SingleStep();
        ParticleSnapshot before = Copy(playback.Observe());
        ModelParameters parameters = editor.Configuration.Parameters;
        editor.ApplyPopulation(2, [6, 6], preserveDensity: true);
        Assert.Equal(parameters.WorldWidth * 2, editor.Configuration.Parameters.WorldWidth);
        Assert.Equal(parameters.WorldHeight * 2, editor.Configuration.Parameters.WorldHeight);
        Assert.Equal(parameters.Cutoff, editor.Configuration.Parameters.Cutoff);
        Assert.Equal(before.Positions[0], playback.Observe().Positions[0]);
        Assert.Equal(before.Velocities[1], playback.Observe().Velocities[1]);
        Assert.Equal(before.Time, playback.Observe().Time);
        Assert.Equal(1, editor.SessionEventCount);
        using Simulation replay = SessionReplay.Replay(editor.CaptureSession("preserve-density"));
        Same(Copy(playback.Observe()), replay.Observe());
        Assert.True(editor.Configuration.EquivalentTo(replay.Configuration));
        editor.Undo();
        Same(before, playback.Observe());
        Assert.Equal(parameters, editor.Configuration.Parameters);
        Assert.False(editor.CanUndo);
        editor.Redo();
        Assert.Equal(12, playback.Observe().Count);
        Assert.Equal(parameters.WorldWidth * 2, editor.Configuration.Parameters.WorldWidth);
    }

    [Fact]
    public void PreserveDensityRejectsZeroAndClampsShrinkingWallLayerAtomically()
    {
        using PlaybackController playback = CreatePlayback();
        ExperimentEditor editor = new(playback);
        SimulationConfiguration current = editor.Configuration;
        editor.ApplyConfiguration(new SimulationConfiguration(current.Parameters with { WallWidth = 0.5 },
            current.Matrix, current.TimeStep));
        long revision = editor.Revision;
        Assert.Throws<ArgumentException>(() => editor.ApplyPopulation(2, [0, 0], preserveDensity: true));
        Assert.Equal(revision, editor.Revision);
        Assert.Equal(3, playback.Observe().Count);
        editor.ApplyPopulation(2, [1, 0], preserveDensity: true);
        Assert.Equal(Math.Min(editor.Configuration.Parameters.WorldWidth, editor.Configuration.Parameters.WorldHeight) / 2,
            editor.Configuration.Parameters.WallWidth);
        editor.ApplyPopulation(2, [0, 0]);
        revision = editor.Revision;
        Assert.Throws<ArgumentException>(() => editor.ApplyPopulation(2, [1, 1], preserveDensity: true));
        Assert.Equal(revision, editor.Revision);
        Assert.Equal(0, playback.Observe().Count);
    }

    [Theory]
    [InlineData(ForceBackend.Direct, 1)]
    [InlineData(ForceBackend.UniformGrid, 1)]
    [InlineData(ForceBackend.UniformGrid, 4)]
    public void LiveRulesPreserveCompletedStateAndNextStepMatchesFreshReference(ForceBackend backend, int workers)
    {
        using PlaybackController playback = CreatePlayback();
        ExperimentEditor editor = new(playback);
        playback.AdvanceFrame(0.03);
        ParticleSnapshot before = Copy(playback.Observe());
        SimulationConfiguration old = editor.Configuration;
        SimulationConfiguration candidate = new(old.Parameters with
        {
            Cutoff = 0.126,
            DampingHalfLife = null,
            WallStrength = 2.4,
            WorldWidth = 0.8,
            WorldHeight = 1.5
        }, new InteractionMatrix(2, [0.4, 0.9, -0.6, 0.1]), 0.004, backend, workers);
        editor.ApplyConfiguration(candidate);
        Same(before, playback.Observe());
        Assert.Equal(0, playback.PendingPlaybackSeconds);
        using Simulation reference = new(before, candidate.Parameters, candidate.Matrix, candidate.TimeStep, backend, workers);
        reference.Step();
        playback.SingleStep();
        Same(Copy(reference.Observe()), playback.Observe());
        Assert.Equal(before.Time + candidate.TimeStep, playback.Observe().Time);
        Assert.Equal(before.Step + 1, playback.Observe().Step);
        Assert.Same(old.Parameters, playback.Experiment.Parameters);
    }

    [Fact]
    public void MatrixTemplateAndWorldResizeKeepAbsoluteOffWorldStateWhilePaused()
    {
        using PlaybackController playback = CreatePlayback();
        ExperimentEditor editor = new(playback);
        playback.SingleStep();
        ParticleSnapshot before = Copy(playback.Observe());
        editor.ApplyMatrixTemplate(PresetKind.Snakes, 15);
        Same(before, playback.Observe());
        Assert.Equal(0.2, editor.Configuration.Matrix[0, 1]);
        editor.ApplyConfiguration(new SimulationConfiguration(editor.Configuration.Parameters with { WorldWidth = 0.2, WorldHeight = 0.2 },
            editor.Configuration.Matrix, editor.Configuration.TimeStep));
        Same(before, playback.Observe());
        Assert.True(playback.Observe().Positions[0].X > editor.Configuration.Parameters.WorldWidth);
        Assert.True(playback.IsPaused);
    }

    [Fact]
    public void NoOpPreservesMeasurementPendingTimeAndBothHistories()
    {
        using PlaybackController playback = CreatePlayback(diagnostics: 1);
        ExperimentEditor editor = new(playback);
        playback.AdvanceFrame(0.015);
        DiagnosticSample? measurement = playback.LatestDiagnostic;
        double pending = playback.PendingPlaybackSeconds;
        editor.ApplyConfiguration(new SimulationConfiguration(editor.Configuration.Parameters,
            new InteractionMatrix(2, editor.Configuration.Matrix.Values), editor.Configuration.TimeStep));
        editor.SetMatrixCoefficient(0, 1, editor.Configuration.Matrix[0, 1]);
        Assert.Same(measurement, playback.LatestDiagnostic);
        Assert.Equal(pending, playback.PendingPlaybackSeconds);
        Assert.False(editor.CanUndo);
        Assert.Equal(0, editor.SessionEventCount);
        Assert.Equal(0, editor.Revision);
    }

    [Fact]
    public void InvalidEditPreservesStatePendingHistoryAndGesture()
    {
        using PlaybackController playback = CreatePlayback();
        ExperimentEditor editor = new(playback);
        editor.BeginGesture("Coefficient drag");
        editor.SetMatrixCoefficient(0, 1, 0.6);
        playback.AdvanceFrame(0.015);
        ParticleSnapshot before = Copy(playback.Observe());
        double pending = playback.PendingPlaybackSeconds;
        long revision = editor.Revision;
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.SetMatrixCoefficient(0, 1, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.ApplyPopulation(3, [1, -1, 2]));
        Assert.Throws<OverflowException>(() => editor.ApplyPopulation(50000, new int[50000]));
        Same(before, playback.Observe());
        Assert.Equal(pending, playback.PendingPlaybackSeconds);
        Assert.Equal(revision, editor.Revision);
        Assert.Equal(1, editor.SessionEventCount);
        editor.SetMatrixCoefficient(0, 1, 0.8);
        editor.EndGesture();
        editor.Undo();
        Assert.Equal(-0.5, editor.Configuration.Matrix[0, 1]);
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public void GestureUndoGroupsUserActionButPreservesEveryAppliedBoundaryForReplay()
    {
        using PlaybackController playback = CreatePlayback();
        ExperimentEditor editor = new(playback);
        editor.BeginGesture("Drag cutoff");
        foreach (double cutoff in new[] { 0.125, 0.126, 0.2 })
        {
            SimulationConfiguration current = editor.Configuration;
            editor.ApplyConfiguration(new SimulationConfiguration(current.Parameters with { Cutoff = cutoff }, current.Matrix, current.TimeStep));
            playback.SingleStep();
        }

        editor.EndGesture();
        Assert.Equal(3, editor.SessionEventCount);
        ParticleSnapshot beforeUndo = Copy(playback.Observe());
        editor.Undo();
        Same(beforeUndo, playback.Observe());
        Assert.Equal(0.1, editor.Configuration.Parameters.Cutoff);
        Assert.False(editor.CanUndo);
        Assert.True(editor.CanRedo);
        editor.Redo();
        Assert.Equal(0.2, editor.Configuration.Parameters.Cutoff);
        Assert.Equal(5, editor.SessionEventCount);
        using Simulation replay = SessionReplay.Replay(editor.CaptureSession("drag"));
        Same(Copy(playback.Observe()), replay.Observe());
        Assert.True(editor.Configuration.EquivalentTo(replay.Configuration));
    }

    [Fact]
    public void NewEditAfterUndoDiscardsRedoWithoutDeletingScientificHistory()
    {
        using PlaybackController playback = CreatePlayback();
        ExperimentEditor editor = new(playback);
        editor.SetMatrixCoefficient(0, 1, 0.7);
        playback.SingleStep();
        editor.Undo();
        editor.SetMatrixCoefficient(1, 0, -0.2);
        Assert.False(editor.CanRedo);
        Assert.Equal(3, editor.SessionEventCount);
        using Simulation replay = SessionReplay.Replay(editor.CaptureSession("branch"));
        Same(Copy(playback.Observe()), replay.Observe());
        Assert.Equal(-0.2, replay.Configuration.Matrix[1, 0]);
    }

    [Fact]
    public void NetZeroGestureStillInvalidatesRedoAfterRealIntermediateEdits()
    {
        using PlaybackController playback = CreatePlayback();
        ExperimentEditor editor = new(playback);
        editor.SetMatrixCoefficient(0, 1, 0.7);
        editor.Undo();
        Assert.True(editor.CanRedo);
        editor.BeginGesture("Return to original");
        editor.SetMatrixCoefficient(1, 0, -0.9);
        playback.SingleStep();
        editor.SetMatrixCoefficient(1, 0, 0.7);
        editor.EndGesture();
        Assert.False(editor.CanRedo);
        Assert.False(editor.CanUndo);
        Assert.Equal(4, editor.SessionEventCount);
        using Simulation replay = SessionReplay.Replay(editor.CaptureSession("net-zero gesture"));
        Same(Copy(playback.Observe()), replay.Observe());
    }

    [Fact]
    public void GrowthShrinkAndRemappingPreserveSurvivorsAndTopLeftMatrix()
    {
        using PlaybackController playback = CreatePlayback();
        ExperimentEditor editor = new(playback);
        playback.SingleStep();
        ParticleSnapshot original = Copy(playback.Observe());
        editor.ApplyPopulation(3, [2, 2, 2]);
        Assert.Equal(6, playback.Observe().Count);
        Assert.Equal(new long[] { 10, 20, 30, 31, 32, 33 }, playback.Observe().Ids.ToArray());
        Assert.Equal(original.Positions[0], playback.Observe().Positions[0]);
        Assert.Equal(original.Velocities[1], playback.Observe().Velocities[1]);
        Assert.Equal(-0.5, editor.Configuration.Matrix[0, 1]);
        Assert.Equal(0, editor.Configuration.Matrix[2, 1]);
        Assert.Equal(new int[] { 2, 2, 2 }, editor.GetCurrentPopulations());
        editor.ApplyPopulation(1, [3]);
        Assert.Equal(new long[] { 10, 20, 30 }, playback.Observe().Ids.ToArray());
        Assert.Equal(new int[] { 0, 0, 0 }, playback.Observe().Species.ToArray());
        Assert.Equal(1, editor.Configuration.Matrix.SpeciesCount);
        Assert.Equal(original.Time, playback.Observe().Time);
        Assert.Equal(original.Step, playback.Observe().Step);
    }

    [Fact]
    public void StructuralUndoPreservesSurvivingMotionAndRevivesStoredRemovedState()
    {
        using PlaybackController playback = CreatePlayback();
        ExperimentEditor editor = new(playback);
        playback.SingleStep();
        ParticleSnapshot removedBoundary = Copy(playback.Observe());
        editor.ApplyPopulation(2, [1, 0]);
        playback.SingleStep();
        ParticleSnapshot beforeUndo = Copy(playback.Observe());
        editor.Undo();
        Assert.Equal(3, playback.Observe().Count);
        Assert.Equal(beforeUndo.Time, playback.Observe().Time);
        Assert.Equal(beforeUndo.Step, playback.Observe().Step);
        Assert.Equal(beforeUndo.Positions[0], playback.Observe().Positions[0]);
        Assert.Equal(removedBoundary.Positions[1], playback.Observe().Positions[1]);
        Assert.Equal(removedBoundary.Velocities[2], playback.Observe().Velocities[2]);
        editor.Redo();
        Assert.Equal(new long[] { 10 }, playback.Observe().Ids.ToArray());
        Assert.Equal(beforeUndo.Positions[0], playback.Observe().Positions[0]);
        using Simulation replay = SessionReplay.Replay(editor.CaptureSession("structural-undo"));
        Same(Copy(playback.Observe()), replay.Observe());
    }

    [Fact]
    public void IdentityAllocatorNeverReusesRemovedOrUndoneAdditions()
    {
        using PlaybackController playback = CreatePlayback();
        ExperimentEditor editor = new(playback);
        editor.ApplyPopulation(2, [2, 2]);
        Assert.Equal(31, playback.Observe().Ids[^1]);
        editor.Undo();
        editor.ApplyPopulation(2, [3, 1]);
        Assert.Equal(32, playback.Observe().Ids[^1]);
        editor.ApplyPopulation(2, [0, 0]);
        editor.ApplyPopulation(2, [1, 0]);
        Assert.Equal(33, playback.Observe().Ids[0]);
    }

    [Fact]
    public void EmptyStateSupportsSpeciesAndPopulationEditsAndUndo()
    {
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 0, 2), diagnosticEverySteps: 0);
        ExperimentEditor editor = new(playback);
        playback.SingleStep();
        editor.ApplyPopulation(4, [0, 0, 0, 0]);
        Assert.Equal(4, editor.Configuration.Matrix.SpeciesCount);
        Assert.Equal(0, playback.Observe().Count);
        editor.ApplyPopulation(4, [0, 2, 0, 1]);
        Assert.Equal(new long[] { 0, 1, 2 }, playback.Observe().Ids.ToArray());
        editor.Undo();
        editor.Undo();
        Assert.Equal(2, editor.Configuration.Matrix.SpeciesCount);
        Assert.Equal(0, playback.Observe().Count);
        Assert.Equal(1, playback.Observe().Step);
    }

    [Fact]
    public void SeedOnlyChangesNoStateAndGenerationRecordsActualPayloadAndProvenance()
    {
        using PlaybackController playback = CreatePlayback();
        ExperimentEditor editor = new(playback);
        ParticleSnapshot original = Copy(playback.Observe());
        editor.SetGeneration(new GenerationSettings
        {
            Seed = 923,
            Placement = PlacementKind.Grid,
            Velocity = VelocityKind.UniformDirection,
            Speed = 2,
            DirectionRadians = Math.PI / 2
        });
        Same(original, playback.Observe());
        Assert.Equal(0, editor.SessionEventCount);
        editor.ResampleParticles();
        ParticleSnapshot generated = Copy(playback.Observe());
        Assert.Equal(original.Ids.ToArray(), generated.Ids.ToArray());
        Assert.InRange(Math.Abs(generated.Velocities[0].X), 0, 2e-15);
        Assert.Equal(2, generated.Velocities[0].Y);
        SessionDefinition session = editor.CaptureSession("generation");
        Assert.Equal(923, session.Events[0].Generator!.Seed);
        Assert.NotNull(session.Events[0].ReplacementState);
        playback.SingleStep();
        double time = playback.Observe().Time;
        editor.Undo();
        Assert.Equal(original.Positions.ToArray(), playback.Observe().Positions.ToArray());
        Assert.Equal(time, playback.Observe().Time);
        editor.Redo();
        Assert.Equal(generated.Positions.ToArray(), playback.Observe().Positions.ToArray());
        Assert.Equal(generated.Velocities.ToArray(), playback.Observe().Velocities.ToArray());
    }

    [Fact]
    public void ResetRestoresOriginalConfigurationAndStartsFreshTimeline()
    {
        using PlaybackController playback = CreatePlayback();
        ExperimentEditor editor = new(playback);
        ExperimentDefinition baseline = playback.Experiment;
        editor.SetMatrixCoefficient(0, 1, 0.6);
        editor.ApplyPopulation(3, [2, 3, 1]);
        playback.SingleStep();
        editor.Reset();
        Same(baseline.InitialState, playback.Observe());
        Assert.Same(baseline, playback.Experiment);
        Assert.Equal(2, editor.Configuration.Matrix.SpeciesCount);
        Assert.Equal(-0.5, editor.Configuration.Matrix[0, 1]);
        Assert.False(editor.CanUndo);
        Assert.False(editor.CanRedo);
        Assert.Empty(editor.CaptureSession("reset").Events);
        Assert.Equal(0, editor.CaptureSession("reset").TerminalStep);
    }

    [Fact]
    public void PlaybackSpeedAndTimestepClearBacklogWithoutChangingClock()
    {
        using PlaybackController playback = CreatePlayback();
        ExperimentEditor editor = new(playback);
        playback.AdvanceFrame(0.005);
        editor.SetPlayback(new PlaybackSettings { Speed = 2, MaxStepsPerFrame = 3, DiagnosticEverySteps = 0 });
        Assert.Equal(0, playback.PendingPlaybackSeconds);
        Assert.Equal(0, playback.Observe().Step);
        Assert.Equal(1, playback.AdvanceFrame(0.005));
        Assert.Equal(0.01, playback.Observe().Time);
        playback.AdvanceFrame(0.003);
        editor.ApplyConfiguration(new SimulationConfiguration(editor.Configuration.Parameters, editor.Configuration.Matrix, 0.004));
        Assert.Equal(0, playback.PendingPlaybackSeconds);
        Assert.Equal(0.01, playback.Observe().Time);
        Assert.Equal(1, playback.AdvanceFrame(0.002));
        Assert.Equal(0.014, playback.Observe().Time);
        Assert.Equal(1, editor.SessionEventCount);
    }

    [Fact]
    public void AppearanceAndDiagnosticConnectivityNeverChangeTrajectories()
    {
        using PlaybackController modified = CreatePlayback();
        using PlaybackController reference = CreatePlayback();
        ExperimentEditor editor = new(modified);
        editor.SetAppearance(editor.Appearance.WithSpeciesColor(1, 0x123456) with { ParticleRadius = 12, UiScale = 1.5 });
        editor.SetPlayback(new PlaybackSettings { DiagnosticRadius = 0.5, DiagnosticEverySteps = 1 });
        for (int i = 0; i < 24; i++)
        {
            modified.SingleStep();
            reference.SingleStep();
        }

        Same(Copy(reference.Observe()), modified.Observe());
        Assert.Equal(0, editor.SessionEventCount);
        Assert.NotNull(modified.LatestDiagnostic);
    }

    [Fact]
    public void WarmSteppingAfterLiveAndStructuralChangesAllocatesZero()
    {
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Chains, 128, 3, backend: ForceBackend.UniformGrid,
            workerCount: 4), diagnosticEverySteps: 0);
        ExperimentEditor editor = new(playback);
        editor.ApplyPopulation(4, [20, 30, 40, 10]);
        editor.ApplyConfiguration(new SimulationConfiguration(editor.Configuration.Parameters with { Cutoff = 0.126 },
            editor.Configuration.Matrix, 0.003, ForceBackend.UniformGrid, 4));
        for (int i = 0; i < 256; i++)
        {
            playback.SingleStep();
        }

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 64; i++)
        {
            playback.SingleStep();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
    }

    private static PlaybackController CreatePlayback(int diagnostics = 0)
    {
        ParticleSnapshot initial = new([10, 20, 30], [new(0.4, 0.5), new(0.44, 0.5), new(0.8, 0.6)],
            [new(0.2, 0.1), new(-0.1, 0), new(0.05, -0.1)], [0, 1, 0]);
        return new PlaybackController(new ExperimentDefinition("editor-tests", new ModelParameters(), new InteractionMatrix(2, [0.2, -0.5, 0.7, 0.1]),
            initial, 0.01, new GeneratorMetadata("manual", "1", null, "test")), diagnosticEverySteps: diagnostics);
    }

    private static ParticleSnapshot Copy(ParticleStateView state) => PlaybackControllerTests.Copy(state);

    private static void Same(ParticleSnapshot expected, ParticleStateView actual) => PlaybackControllerTests.SameState(expected, actual);
}
