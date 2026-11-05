using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using ParticleLife.Viewer;

namespace ParticleLife.Viewer.Tests;

public sealed class BackgroundExperimentTests
{
    [Fact]
    public async Task LeasesRetainCompletedStateAndExhaustedPoolDoesNotBlockSteps()
    {
        using BackgroundExperiment owner = CreateOwner();
        using RenderFrameLease first = owner.AcquireRenderFrame()!;
        Vector2d[] initial = first.Positions.ToArray();
        await owner.Submit("Step", editor => editor.Playback.SingleStep());
        using RenderFrameLease second = owner.AcquireRenderFrame()!;
        await owner.Submit("Step", editor => editor.Playback.SingleStep());
        using RenderFrameLease third = owner.AcquireRenderFrame()!;
        Assert.NotEqual(first.Sequence, second.Sequence);
        Assert.NotEqual(second.Sequence, third.Sequence);
        await owner.Submit("Step with every buffer leased", editor => editor.Playback.SingleStep());
        Assert.Equal(3, owner.Publication.Step);
        Assert.Equal(initial, first.Positions.ToArray());
        Assert.Equal(0, first.Step);
        Assert.Equal(1, second.Step);
        Assert.Equal(2, third.Step);
        first.Dispose();
        using RenderFrameLease stale = owner.AcquireRenderFrame()!;
        long sequence = stale.Sequence;
        await WaitUntil(() =>
        {
            using RenderFrameLease frame = owner.AcquireRenderFrame()!;
            return frame.Sequence > sequence && frame.Step == 3;
        });
    }

    [Fact]
    public async Task CoalescedPreviewsRespectBarriersAndRetainEveryAppliedReplayEdit()
    {
        using BackgroundExperiment owner = CreateOwner();
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        Task blocked = owner.Submit("Busy boundary", _ => { entered.Set(); release.Wait(); });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        SessionDefinition? session = null;
        ExperimentDefinition? final = null;
        Task skipped;
        Task complete;
        try
        {
            _ = owner.Submit("Begin", editor => editor.BeginGesture("Force drag"));
            skipped = owner.Submit("Old preview", editor => SetForce(editor, 2), "force");
            _ = owner.Submit("Latest preview", editor => SetForce(editor, 3), "force");
            _ = owner.Submit("Step barrier", editor => editor.Playback.SingleStep());
            _ = owner.Submit("Applied after step", editor => SetForce(editor, 4), "force");
            _ = owner.Submit("End", editor => editor.EndGesture());
            complete = owner.Submit("Capture", editor =>
            {
                session = editor.CaptureSession("drag");
                final = editor.CaptureCurrentExperiment("final");
            });
        }
        finally { release.Set(); }
        await blocked;
        await complete;
        Assert.True(skipped.IsCanceled);
        Assert.Equal(2, session!.Events.Count);
        Assert.Equal(0, session.Events[0].BoundaryStep);
        Assert.Equal(3, session.Events[0].Configuration.Parameters.ForceScale);
        Assert.Equal(1, session.Events[1].BoundaryStep);
        Assert.Equal(4, session.Events[1].Configuration.Parameters.ForceScale);
        using Simulation replay = SessionReplay.Replay(session);
        Assert.Equal(final!.InitialState.Positions.ToArray(), replay.Observe().Positions.ToArray());
        Assert.Equal(final.InitialState.Velocities.ToArray(), replay.Observe().Velocities.ToArray());
    }

    [Fact]
    public async Task BusyOwnerKeepsAppearanceResponsiveAndSaveReportsOnlyActualCompletion()
    {
        string directory = Path.Combine(Path.GetTempPath(), "particle-life-background-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            using EditorSession session = new(CreateEditor(), runInBackground: true);
            using ManualResetEventSlim entered = new();
            using ManualResetEventSlim release = new();
            Task blocked = session.Run("Busy boundary", _ => { entered.Set(); release.Wait(); });
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            bool reported = false;
            string path = Path.Combine(directory, "capture.json");
            Task saved;
            try
            {
                session.SetAppearance(session.Appearance with { ShowEditor = false, UiScale = 1.25 });
                SimulationConfiguration current = session.Configuration;
                session.ApplyConfiguration(new SimulationConfiguration(current.Parameters with { ForceScale = 2 }, current.Matrix,
                    current.TimeStep, current.Backend, current.WorkerCount));
                saved = session.Run("Save", editor => editor.SaveCurrent(path, "capture"), () => reported = true);
                Assert.False(session.Appearance.ShowEditor);
                Assert.Equal(1.25, session.Appearance.UiScale);
                Assert.True(session.HasPendingCommands);
                Assert.False(reported);
                Assert.False(File.Exists(path));
            }
            finally { release.Set(); }
            await blocked;
            await saved;
            Assert.False(reported);
            session.Pump();
            Assert.True(reported);
            ExperimentDefinition capture = ExperimentFile.Load(path);
            Assert.Equal(2, capture.Parameters.ForceScale);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task RejectedSaveNeverInvokesSuccessAndRetainsCompletedState()
    {
        using EditorSession session = new(CreateEditor(), runInBackground: true);
        session.Playback.SetPaused(false);
        await session.Run("Running boundary", _ => { });
        bool reported = false;
        string? failure = null;
        session.Failed += message => failure = message;
        Task save = session.Run("Rejected save", _ => throw new IOException("destination unavailable"), () => reported = true);
        await Assert.ThrowsAsync<IOException>(() => save);
        session.Pump();
        Assert.False(reported);
        Assert.Contains("destination unavailable", failure);
        Assert.Equal(0, session.Publication.Step);
        Assert.False(session.Publication.IsPaused);
    }

    [Fact]
    public async Task RelativeDraftsPreserveAnEarlierOwnerEditAndQueuedUndoGrouping()
    {
        using EditorSession session = new(CreateEditor(), runInBackground: true);
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        Task blocked = session.Run("Busy boundary", _ => { entered.Set(); release.Wait(); });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Task completed;
        try
        {
            _ = session.Run("Earlier owner edit", editor => SetForce(editor, 7));
            session.BeginGesture("Damping drag");
            SimulationConfiguration displayed = session.Configuration;
            session.ApplyConfiguration(new SimulationConfiguration(displayed.Parameters with { DampingHalfLife = 0.5 },
                displayed.Matrix, displayed.TimeStep, displayed.Backend, displayed.WorkerCount));
            session.EndGesture();
            completed = session.Run("Boundary", _ => { });
        }
        finally { release.Set(); }
        await blocked;
        await completed;
        Assert.Equal(7, session.Publication.Configuration.Parameters.ForceScale);
        Assert.Equal(0.5, session.Publication.Configuration.Parameters.DampingHalfLife);
        await session.Run("Undo", editor => editor.Undo());
        Assert.Equal(7, session.Publication.Configuration.Parameters.ForceScale);
        Assert.NotEqual(0.5, session.Publication.Configuration.Parameters.DampingHalfLife);
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("matrix")]
    [InlineData("generation")]
    [InlineData("appearance")]
    [InlineData("playback")]
    public async Task QueuedUndoSeparatesTouchedFieldsAcrossPreviewRuns(string group)
    {
        using EditorSession session = new(CreateEditor(), runInBackground: true);
        SimulationConfiguration initialConfiguration = session.Configuration;
        GenerationSettings initialGeneration = session.Generation;
        AppearanceSettings initialAppearance = session.Appearance;
        PlaybackSettings initialPlayback = session.Playback.Settings;
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        Task blocked = session.Run("Busy boundary", _ => { entered.Set(); release.Wait(); });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Task completed;
        try
        {
            switch (group)
            {
                case "configuration":
                    session.ApplyConfiguration(new SimulationConfiguration(initialConfiguration.Parameters with { ForceScale = 7 },
                        initialConfiguration.Matrix, initialConfiguration.TimeStep, initialConfiguration.Backend, initialConfiguration.WorkerCount));
                    session.Undo();
                    SimulationConfiguration displayed = session.Configuration;
                    Assert.Equal(7, displayed.Parameters.ForceScale);
                    session.ApplyConfiguration(new SimulationConfiguration(displayed.Parameters with { DampingHalfLife = 0.5 },
                        displayed.Matrix, displayed.TimeStep, displayed.Backend, displayed.WorkerCount));
                    break;
                case "matrix":
                    session.SetMatrixCoefficient(0, 0, 0.123);
                    session.Undo();
                    Assert.Equal(0.123, session.Configuration.Matrix[0, 0]);
                    session.SetMatrixCoefficient(0, 1, 0.456);
                    break;
                case "generation":
                    session.SetGeneration(initialGeneration with { Seed = initialGeneration.Seed + 1 });
                    session.Undo();
                    Assert.Equal(initialGeneration.Seed + 1, session.Generation.Seed);
                    session.SetGeneration(session.Generation with { Speed = 2 });
                    break;
                case "appearance":
                    session.SetAppearance(initialAppearance with { ParticleRadius = 5 });
                    session.Undo();
                    Assert.Equal(5, session.Appearance.ParticleRadius);
                    session.SetAppearance(session.Appearance with { UiScale = 1.25 });
                    break;
                case "playback":
                    session.SetPlayback(initialPlayback with { Speed = 2 });
                    session.Undo();
                    Assert.Equal(2, session.Playback.PlaybackSpeed);
                    session.SetPlayback(session.Playback.Settings with { MaxStepsPerFrame = 16 });
                    break;
            }
            completed = session.Run("Completed boundary", _ => { });
        }
        finally { release.Set(); }
        await blocked;
        await completed;
        switch (group)
        {
            case "configuration":
                Assert.Equal(initialConfiguration.Parameters.ForceScale, session.Publication.Configuration.Parameters.ForceScale);
                Assert.Equal(0.5, session.Publication.Configuration.Parameters.DampingHalfLife);
                break;
            case "matrix":
                Assert.Equal(initialConfiguration.Matrix[0, 0], session.Publication.Configuration.Matrix[0, 0]);
                Assert.Equal(0.456, session.Publication.Configuration.Matrix[0, 1]);
                break;
            case "generation":
                Assert.Equal(initialGeneration.Seed, session.Publication.Generation.Seed);
                Assert.Equal(2, session.Publication.Generation.Speed);
                break;
            case "appearance":
                Assert.Equal(initialAppearance.ParticleRadius, session.Publication.Appearance.ParticleRadius);
                Assert.Equal(1.25, session.Publication.Appearance.UiScale);
                break;
            case "playback":
                Assert.Equal(initialPlayback.Speed, session.Publication.Playback.Speed);
                Assert.Equal(16, session.Publication.Playback.MaxStepsPerFrame);
                break;
        }
    }

    [Fact]
    public async Task FailedNumericalStepPublishesPausedLastCompletedState()
    {
        ModelParameters parameters = new() { ForceScale = 0, WallStrength = 0, DampingHalfLife = null };
        ParticleSnapshot initial = new([91], [new Vector2d(double.MaxValue, 0)], [new Vector2d(double.MaxValue, 0)], [0]);
        ExperimentDefinition experiment = new("overflow", parameters, new InteractionMatrix(1, [0]), initial,
            1, new GeneratorMetadata("manual", "1", null, "test"));
        using BackgroundExperiment owner = new(new ExperimentEditor(new PlaybackController(experiment, diagnosticEverySteps: 0)));
        owner.AdvanceFrame(2);
        await WaitUntil(() => owner.Publication.Error is not null);
        Assert.True(owner.Publication.IsPaused);
        Assert.Equal(0, owner.Publication.Step);
        using RenderFrameLease frame = owner.AcquireRenderFrame()!;
        Assert.Equal(initial.Positions.ToArray(), frame.Positions.ToArray());
    }

    [Fact]
    public async Task PauseDiscardsBusyElapsedAndResumeDoesNotCatchUpOldFrames()
    {
        using BackgroundExperiment owner = CreateOwner();
        await owner.Submit("Resume", editor => editor.Playback.SetPaused(false));
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        Task blocked = owner.Submit("Busy boundary", _ => { entered.Set(); release.Wait(); });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        try
        {
            owner.AdvanceFrame(100);
            owner.RequestPause();
        }
        finally { release.Set(); }
        await blocked;
        await owner.Submit("Paused boundary", _ => { });
        Assert.True(owner.Publication.IsPaused);
        Assert.Equal(0, owner.Publication.Step);
        await owner.Submit("Resume", editor => editor.Playback.SetPaused(false));
        await owner.Submit("Resumed boundary", _ => { });
        Assert.Equal(0, owner.Publication.Step);
        owner.AdvanceFrame(owner.Publication.Configuration.TimeStep);
        await WaitUntil(() => owner.Publication.Step == 1);
    }

    [Fact]
    public async Task DragReturningToItsStartStillRestoresAnAlreadyAppliedPreview()
    {
        using EditorSession session = new(CreateEditor(), runInBackground: true);
        double initial = session.Configuration.Parameters.ForceScale;
        session.BeginGesture("Force drag");
        SimulationConfiguration configuration = session.Configuration;
        session.ApplyConfiguration(new SimulationConfiguration(configuration.Parameters with { ForceScale = initial + 2 },
            configuration.Matrix, configuration.TimeStep, configuration.Backend, configuration.WorkerCount));
        await session.Run("Applied preview boundary", _ => { });
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        Task blocked = session.Run("Busy boundary", _ => { entered.Set(); release.Wait(); });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Task completed;
        try
        {
            configuration = session.Configuration;
            session.ApplyConfiguration(new SimulationConfiguration(configuration.Parameters with { ForceScale = initial + 3 },
                configuration.Matrix, configuration.TimeStep, configuration.Backend, configuration.WorkerCount));
            configuration = session.Configuration;
            session.ApplyConfiguration(new SimulationConfiguration(configuration.Parameters with { ForceScale = initial },
                configuration.Matrix, configuration.TimeStep, configuration.Backend, configuration.WorkerCount));
            session.EndGesture();
            completed = session.Run("Returned boundary", _ => { });
        }
        finally { release.Set(); }
        await blocked;
        await completed;
        Assert.Equal(initial, session.Publication.Configuration.Parameters.ForceScale);
        Assert.Equal(2, session.Publication.SessionEventCount);
    }

    [Fact]
    public async Task AppearanceDoesNotCopyUnchangedParticleBuffers()
    {
        using BackgroundExperiment owner = CreateOwner();
        using RenderFrameLease before = owner.AcquireRenderFrame()!;
        await owner.Submit("Appearance", editor => editor.SetAppearance(editor.Appearance with { ParticleRadius = 7 }));
        using RenderFrameLease after = owner.AcquireRenderFrame()!;
        Assert.Equal(before.Sequence, after.Sequence);
        Assert.Equal(7, owner.Publication.Appearance.ParticleRadius);
        Assert.Equal(before.Positions.ToArray(), after.Positions.ToArray());
    }

    [Fact]
    public async Task NumericalFailureSurvivesALaterSuccessfulMetadataPublication()
    {
        ModelParameters parameters = new() { ForceScale = 0, WallStrength = 0, DampingHalfLife = null };
        ParticleSnapshot initial = new([91], [new Vector2d(double.MaxValue, 0)], [new Vector2d(double.MaxValue, 0)], [0]);
        ExperimentDefinition experiment = new("overflow", parameters, new InteractionMatrix(1, [0]), initial,
            1, new GeneratorMetadata("manual", "1", null, "test"));
        using EditorSession session = new(new ExperimentEditor(new PlaybackController(experiment, diagnosticEverySteps: 0)), true);
        string? failure = null;
        session.Failed += message => failure = message;
        session.Playback.AdvanceFrame(2);
        await WaitUntil(() => session.Publication.Error is not null);
        await session.Run("Later successful command", editor => editor.EndGesture());
        Assert.Null(session.Publication.Error);
        session.Pump();
        Assert.NotNull(failure);
        Assert.True(session.Publication.IsPaused);
        Assert.Equal(0, session.Publication.Step);
    }

    private static BackgroundExperiment CreateOwner() => new(CreateEditor());
    private static ExperimentEditor CreateEditor()
    {
        PlaybackController playback = new(PresetGenerator.Create(PresetKind.Chains, 12, 3, 9), diagnosticEverySteps: 0);
        playback.SetPaused(true);
        return new ExperimentEditor(playback);
    }
    private static void SetForce(ExperimentEditor editor, double value)
    {
        SimulationConfiguration current = editor.Configuration;
        editor.ApplyConfiguration(new SimulationConfiguration(current.Parameters with { ForceScale = value }, current.Matrix,
            current.TimeStep, current.Backend, current.WorkerCount));
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        while (!condition()) { await Task.Delay(5, deadline.Token); }
    }
}
