using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.State;
using ParticleLife.Viewer;

namespace ParticleLife.Viewer.Tests;

public sealed class PlaybackControllerTests
{
    [Fact]
    public void FractionalFramesAdvanceOnlyCompleteFixedSteps()
    {
        ExperimentDefinition experiment = PresetGenerator.Create(PresetKind.Random, 8, 3, 42, timeStep: 0.125);
        PlaybackController controller = new(experiment);
        Assert.Equal(0, controller.AdvanceFrame(0.03125));
        Assert.Equal(0, controller.AdvanceFrame(0.0625));
        Assert.Equal(1, controller.AdvanceFrame(0.03125));
        Assert.Equal(1, controller.Observe().Step);
        Assert.Equal(0.125, controller.Observe().Time);
        Assert.Equal(0, controller.PendingPlaybackSeconds);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(240)]
    public void RenderFrequencyDoesNotChangeCompletedSteps(int framesPerSecond)
    {
        PlaybackController controller = new(SmallExperiment(), diagnosticEverySteps: 0);
        for (int frame = 0; frame < framesPerSecond; frame++)
        {
            controller.AdvanceFrame(1d / framesPerSecond);
        }

        Assert.Equal(144, controller.Observe().Step);
        Close(1, controller.Observe().Time);
        Close(0, controller.DroppedPlaybackSeconds);
    }

    [Fact]
    public void SlowFrameBoundsWorkAndReportsDiscardedWholeSteps()
    {
        PlaybackController controller = new(PresetGenerator.Create(PresetKind.Chains, 8, 3, timeStep: 0.125), maxStepsPerFrame: 3);
        Assert.Equal(3, controller.AdvanceFrame(1.0625));
        Assert.Equal(3, controller.Observe().Step);
        Assert.Equal(0.375, controller.Observe().Time);
        Assert.Equal(0.625, controller.DroppedPlaybackSeconds);
        Assert.Equal(0.0625, controller.PendingPlaybackSeconds);
        Assert.Equal(1, controller.AdvanceFrame(0.0625));
        Assert.Equal(4, controller.Observe().Step);
    }

    [Fact]
    public void HugeFiniteFrameStillCompletesOnlyBoundedSteps()
    {
        PlaybackController controller = new(SmallExperiment(), maxStepsPerFrame: 2, diagnosticEverySteps: 0);
        Assert.Equal(2, controller.AdvanceFrame(double.MaxValue));
        Assert.True(controller.PendingPlaybackSeconds >= 0 && controller.PendingPlaybackSeconds < controller.Experiment.TimeStep);
        Assert.True(double.IsFinite(controller.DroppedPlaybackSeconds));
        Assert.Equal(2, controller.Observe().Step);
    }

    [Fact]
    public void PauseAndResumeDiscardPendingTimeAndIgnorePausedFrames()
    {
        PlaybackController controller = new(SmallExperiment());
        double dt = controller.Experiment.TimeStep;
        controller.AdvanceFrame(dt * 0.75);
        controller.SetPaused(true);
        Assert.Equal(0, controller.PendingPlaybackSeconds);
        Assert.Equal(0, controller.AdvanceFrame(3600));
        controller.SetPaused(false);
        Assert.Equal(0, controller.AdvanceFrame(dt * 0.25));
        Assert.Equal(0, controller.Observe().Step);
        Assert.Equal(0, controller.DroppedPlaybackSeconds);
    }

    [Fact]
    public void SettingSamePlaybackStatusDoesNotLoseFractionalTime()
    {
        PlaybackController controller = new(SmallExperiment());
        controller.AdvanceFrame(controller.Experiment.TimeStep * 0.75);
        controller.SetPaused(false);
        Assert.Equal(1, controller.AdvanceFrame(controller.Experiment.TimeStep * 0.25));
    }

    [Fact]
    public void SingleStepPausesAndAdvancesExactlyOnceWithoutBacklog()
    {
        PlaybackController controller = new(SmallExperiment());
        controller.AdvanceFrame(controller.Experiment.TimeStep * 0.9);
        controller.SingleStep();
        Assert.True(controller.IsPaused);
        Assert.Equal(1, controller.Observe().Step);
        Assert.Equal(controller.Experiment.TimeStep, controller.Observe().Time);
        Assert.Equal(0, controller.PendingPlaybackSeconds);
        Assert.Equal(0, controller.AdvanceFrame(100));
        controller.SingleStep();
        Assert.Equal(2, controller.Observe().Step);
    }

    [Fact]
    public void ResetRestoresExactSavedValuesAndClearsPlaybackAndMeasurements()
    {
        ExperimentDefinition experiment = SmallExperiment();
        string saved = ExperimentFile.Serialize(experiment);
        PlaybackController controller = new(experiment, maxStepsPerFrame: 2, diagnosticEverySteps: 1);
        controller.AdvanceFrame(1);
        Assert.NotNull(controller.LatestDiagnostic);
        Assert.True(controller.DroppedPlaybackSeconds > 0);
        controller.SetPaused(true);
        controller.Reset();
        Assert.True(controller.IsPaused);
        Assert.Equal(0, controller.PendingPlaybackSeconds);
        Assert.Equal(0, controller.DroppedPlaybackSeconds);
        Assert.Null(controller.LatestDiagnostic);
        Assert.Same(experiment, controller.Experiment);
        Assert.Equal(saved, ExperimentFile.Serialize(controller.Experiment));
        SameState(experiment.InitialState, controller.Observe());
    }

    [Fact]
    public void ReplacementBecomesResetTargetAndClearsOldMeasurement()
    {
        PlaybackController controller = new(SmallExperiment(), diagnosticEverySteps: 1);
        controller.SingleStep();
        ExperimentDefinition replacement = PresetGenerator.Create(PresetKind.Snakes, 6, 2, 17, timeStep: 0.125);
        controller.ReplaceExperiment(replacement);
        Assert.True(controller.IsPaused);
        Assert.Null(controller.LatestDiagnostic);
        Assert.Equal(0, controller.PendingPlaybackSeconds);
        Assert.Same(replacement, controller.Experiment);
        controller.SingleStep();
        controller.Reset();
        SameState(replacement.InitialState, controller.Observe());
    }

    [Fact]
    public void RejectedReplacementPreservesCurrentValidExperimentAndPendingTime()
    {
        PlaybackController controller = new(SmallExperiment());
        controller.AdvanceFrame(controller.Experiment.TimeStep * 0.5);
        ExperimentDefinition current = controller.Experiment;
        double pending = controller.PendingPlaybackSeconds;
        Assert.Throws<ArgumentNullException>(() => controller.ReplaceExperiment(null!));
        Assert.Same(current, controller.Experiment);
        Assert.Equal(pending, controller.PendingPlaybackSeconds);
        SameState(current.InitialState, controller.Observe());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidElapsedTimeIsRejectedWithoutChangingState(double elapsed)
    {
        PlaybackController controller = new(SmallExperiment());
        controller.AdvanceFrame(controller.Experiment.TimeStep * 0.5);
        double pending = controller.PendingPlaybackSeconds;
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.AdvanceFrame(elapsed));
        Assert.Equal(pending, controller.PendingPlaybackSeconds);
        SameState(controller.Experiment.InitialState, controller.Observe());
    }

    [Fact]
    public void MeasurementsUseCompletedStepCadenceAndIdentifyTheirOwnTime()
    {
        PlaybackController controller = new(SmallExperiment(), diagnosticEverySteps: 3);
        Assert.Null(controller.LatestDiagnostic);
        controller.SingleStep();
        controller.SingleStep();
        Assert.Null(controller.LatestDiagnostic);
        controller.SingleStep();
        DiagnosticSample sample = Assert.IsType<DiagnosticSample>(controller.LatestDiagnostic);
        Assert.Equal(3, sample.Step);
        Assert.Equal(controller.Observe().Time, sample.Time);
        controller.SingleStep();
        Assert.Same(sample, controller.LatestDiagnostic);
        controller.AdvanceFrame(100);
        Assert.Same(sample, controller.LatestDiagnostic);
    }

    [Fact]
    public void DiagnosticsChangesDoNotChangeTrajectoryOrPendingTime()
    {
        ExperimentDefinition experiment = SmallExperiment();
        PlaybackController sampled = new(experiment, diagnosticEverySteps: 1);
        PlaybackController unsampled = new(experiment, diagnosticEverySteps: 0);
        for (int i = 0; i < 30; i++)
        {
            sampled.AdvanceFrame(experiment.TimeStep * 2.25);
            unsampled.AdvanceFrame(experiment.TimeStep * 2.25);
            if (i == 10)
            {
                double pending = sampled.PendingPlaybackSeconds;
                sampled.DiagnosticEverySteps = 5;
                Assert.Null(sampled.LatestDiagnostic);
                Assert.Equal(pending, sampled.PendingPlaybackSeconds);
            }
        }

        SameState(Copy(unsampled.Observe()), sampled.Observe());
        sampled.DiagnosticEverySteps = 0;
        Assert.Null(sampled.LatestDiagnostic);
        sampled.SingleStep();
        Assert.Null(sampled.LatestDiagnostic);
        Assert.Throws<ArgumentOutOfRangeException>(() => sampled.DiagnosticEverySteps = -1);
    }

    [Fact]
    public void NumericalFailurePausesAndPreservesLastCompletedState()
    {
        ModelParameters parameters = new() { ForceScale = 0, WallStrength = 0, DampingHalfLife = null };
        ParticleSnapshot initial = new([91], [new Vector2d(double.MaxValue, 0)], [new Vector2d(double.MaxValue, 0)], [0]);
        ExperimentDefinition experiment = new("overflow", parameters, new InteractionMatrix(1, [0]), initial,
            1, new GeneratorMetadata("manual", "1", null, "test"));
        PlaybackController controller = new(experiment);
        Assert.Throws<ArithmeticException>(() => controller.AdvanceFrame(2));
        Assert.True(controller.IsPaused);
        Assert.Equal(0, controller.PendingPlaybackSeconds);
        SameState(initial, controller.Observe());
    }

    [Fact]
    public void MeasurementOverflowDoesNotStopValidStepping()
    {
        ModelParameters parameters = new() { ForceScale = 0, WallStrength = 0, DampingHalfLife = null };
        ParticleSnapshot initial = new([91], [new Vector2d(0, 0)], [new Vector2d(1.3e308, 1.3e308)], [0]);
        ExperimentDefinition experiment = new("diagnostic-overflow", parameters, new InteractionMatrix(1, [0]), initial,
            1e-308, new GeneratorMetadata("manual", "1", null, "test"));
        PlaybackController controller = new(experiment, diagnosticEverySteps: 1);
        Assert.Equal(1, controller.AdvanceFrame(experiment.TimeStep));
        Assert.False(controller.IsPaused);
        Assert.Null(controller.LatestDiagnostic);
        Assert.Contains("Speed overflow", controller.DiagnosticError);
        Assert.Equal(1, controller.Observe().Step);
        controller.Reset();
        Assert.Null(controller.DiagnosticError);
    }

    [Fact]
    public void ReusableMeasurementsMatchReferenceAfterResetAndDifferentPopulationReplacement()
    {
        PlaybackController controller = new(PresetGenerator.Create(PresetKind.Chains, 64, 3, 8), diagnosticEverySteps: 1);
        for (int i = 0; i < 3; i++)
        {
            controller.SingleStep();
            Assert.Equal(Diagnostics.Sample(controller.Observe(), controller.Experiment.Parameters.Cutoff), controller.LatestDiagnostic);
        }

        controller.Reset();
        Assert.Null(controller.LatestDiagnostic);
        controller.SingleStep();
        Assert.Equal(Diagnostics.Sample(controller.Observe(), controller.Experiment.Parameters.Cutoff), controller.LatestDiagnostic);
        foreach (int count in new[] { 7, 128, 0, 24 })
        {
            controller.ReplaceExperiment(PresetGenerator.Create(PresetKind.Random, count, 3, 9,
                new ModelParameters { Cutoff = 0.125 }));
            Assert.Null(controller.LatestDiagnostic);
            controller.SingleStep();
            Assert.Equal(Diagnostics.Sample(controller.Observe(), 0.125), controller.LatestDiagnostic);
            Assert.Equal(1, controller.LatestDiagnostic!.Step);
        }
    }

    [Fact]
    public void WarmScheduledMeasurementsAllocateOnlyOneResultPerCompletedStep()
    {
        PlaybackController small = new(SmallExperiment(), diagnosticEverySteps: 1);
        PlaybackController larger = new(PresetGenerator.Create(PresetKind.Chains, 128, 3, 11), diagnosticEverySteps: 1);
        for (int i = 0; i < 64; i++)
        {
            small.SingleStep();
            larger.SingleStep();
        }

        long startSmall = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 32; i++)
        {
            small.SingleStep();
        }

        long smallBytes = GC.GetAllocatedBytesForCurrentThread() - startSmall;
        long startLarger = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 32; i++)
        {
            larger.SingleStep();
        }

        long largerBytes = GC.GetAllocatedBytesForCurrentThread() - startLarger;
        Assert.InRange(smallBytes / 32, 1, 128);
        Assert.Equal(smallBytes, largerBytes);
        Assert.Equal(larger.Observe().Step, larger.LatestDiagnostic!.Step);
    }

    [Fact]
    public void FiniteOffWorldParticlesRemainMembersRegardlessOfMapping()
    {
        ModelParameters parameters = new() { ForceScale = 0, WallStrength = 0, DampingHalfLife = null };
        ParticleSnapshot initial = new([8, 4], [new Vector2d(-3, 2), new Vector2d(5, -1)], [new Vector2d(0.1, 0), default], [0, 0]);
        ExperimentDefinition experiment = new("off-world", parameters, new InteractionMatrix(1, [0]), initial,
            0.125, new GeneratorMetadata("manual", "1", null, "test"));
        PlaybackController controller = new(experiment, diagnosticEverySteps: 0);
        WorldTransform transform = new(1, 1, 0, 0, 800, 400);
        Assert.True(transform.ToScreen(initial.Positions[0]).X < 0);
        controller.SingleStep();
        Assert.Equal(2, controller.Observe().Count);
        Assert.Equal(new long[] { 8, 4 }, controller.Observe().Ids.ToArray());
        Assert.Equal(new Vector2d(-2.9875, 2), controller.Observe().Positions[0]);
        controller.Reset();
        SameState(initial, controller.Observe());
    }

    [Fact]
    public void WarmPlaybackWithoutDiagnosticsAllocatesNoManagedBytes()
    {
        PlaybackController controller = new(SmallExperiment(), diagnosticEverySteps: 0);
        for (int i = 0; i < 1000; i++)
        {
            controller.AdvanceFrame(controller.Experiment.TimeStep);
        }

        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 128; i++)
        {
            controller.AdvanceFrame(controller.Experiment.TimeStep);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ConstructorRejectsInvalidCadenceAndCatchUpCap()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlaybackController(SmallExperiment(), maxStepsPerFrame: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlaybackController(SmallExperiment(), diagnosticEverySteps: -1));
    }

    [Fact]
    public void DisposalKeepsCompletedStateReadableButPreventsFurtherWork()
    {
        PlaybackController controller = new(SmallExperiment());
        controller.SingleStep();
        ParticleSnapshot completed = Copy(controller.Observe());
        controller.Dispose();
        controller.Dispose();
        SameState(completed, controller.Observe());
        Assert.Throws<ObjectDisposedException>(controller.SingleStep);
        Assert.Throws<ObjectDisposedException>(controller.Reset);
        Assert.Throws<ObjectDisposedException>(() => controller.AdvanceFrame(0));
        Assert.Throws<ObjectDisposedException>(() => controller.ReplaceExperiment(SmallExperiment()));
    }

    private static ExperimentDefinition SmallExperiment() => PresetGenerator.Create(PresetKind.Random, 8, 3, 42);

    internal static ParticleSnapshot Copy(ParticleStateView view) =>
        new(view.Ids, view.Positions, view.Velocities, view.Species, view.Time, view.Step);

    internal static void SameState(ParticleSnapshot expected, ParticleStateView actual)
    {
        Assert.Equal(expected.Time, actual.Time);
        Assert.Equal(expected.Step, actual.Step);
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            int match = actual.Ids.IndexOf(expected.Ids[i]);
            Assert.True(match >= 0);
            Assert.Equal(expected.Species[i], actual.Species[match]);
            Assert.Equal(expected.Positions[i], actual.Positions[match]);
            Assert.Equal(expected.Velocities[i], actual.Velocities[match]);
        }
    }

    private static void Close(double expected, double actual)
    {
        // Scheduler sums at most 240 one-second fractions; 1e-13 seconds bounds ordinary roundoff.
        Assert.InRange(Math.Abs(expected - actual), 0, 1e-13);
    }
}
