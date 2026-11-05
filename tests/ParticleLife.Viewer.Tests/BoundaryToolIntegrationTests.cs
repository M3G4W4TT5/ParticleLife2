using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using ParticleLife.Viewer;

namespace ParticleLife.Viewer.Tests;

public sealed class BoundaryToolIntegrationTests
{
    [Theory]
    [InlineData(BoundaryKind.Periodic)]
    [InlineData(BoundaryKind.Reflecting)]
    public void DensityResizeRecordsMappingAndRestoresOnlyRemappedSurvivorMotion(BoundaryKind boundary)
    {
        ExperimentDefinition initial = Fixture(boundary);
        using PlaybackController playback = new(initial, diagnosticEverySteps: 0);
        ExperimentEditor editor = new(playback);
        editor.ApplyPopulation(1, [2], preserveDensity: true);
        ParticleSnapshot mapped = Copy(playback.Observe());
        double extent = Math.Sqrt(0.5);
        Close(extent, editor.Configuration.Parameters.WorldWidth);
        Assert.InRange(mapped.Positions[0].X, 0, extent);
        Assert.Equal(boundary == BoundaryKind.Reflecting ? -0.05 : 0.05, mapped.Velocities[0].X);
        playback.SingleStep();
        Vector2d unaffected = playback.Observe().Positions[1];
        editor.Undo();
        Assert.Equal(initial.InitialState.Positions[0], playback.Observe().Positions[0]);
        Assert.Equal(initial.InitialState.Velocities[0], playback.Observe().Velocities[0]);
        Assert.Equal(unaffected, playback.Observe().Positions[1]);
        Assert.Equal(1, playback.Observe().Step);
        editor.Redo();
        Assert.Equal(mapped.Positions[0], playback.Observe().Positions[0]);
        Assert.Equal(unaffected, playback.Observe().Positions[1]);
        ReplayMatches(editor);
    }

    [Fact]
    public void UnsupportedDensityShrinkRejectsPeriodicCutoffWithoutPartialChanges()
    {
        ExperimentDefinition initial = Fixture(BoundaryKind.Periodic, cutoff: 0.4);
        using PlaybackController playback = new(initial, diagnosticEverySteps: 0);
        ExperimentEditor editor = new(playback);
        SimulationConfiguration configuration = editor.Configuration;
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.ApplyPopulation(1, [1], preserveDensity: true));
        Assert.Same(configuration, editor.Configuration);
        Assert.Equal(0, editor.SessionEventCount);
        Same(initial.InitialState.Observe(), playback.Observe());
    }

    [Fact]
    public void PeriodicMoveAndForceBrushesUseTheSameMinimumImageAcrossTheSeam()
    {
        ExperimentDefinition initial = new("seam brush", Parameters(BoundaryKind.Periodic), new InteractionMatrix(1, [0]),
            new ParticleSnapshot([1, 2, 3], [new(0.98, 0.5), new(0.02, 0.5), new(0.5, 0.5)], new Vector2d[3], [0, 0, 0]),
            0.01, new GeneratorMetadata("test", "1", 1, "test"));
        using PlaybackController playback = new(initial, diagnosticEverySteps: 0);
        ExperimentEditor editor = new(playback);
        editor.BeginBrushStroke(new() { Kind = BrushKind.Move, Radius = 0.05 }, new(0.99, 0.5));
        editor.ApplyBrushSample(new(0.03, 0.5));
        editor.EndBrushStroke();
        Close(0.02, playback.Observe().Positions[0].X); Close(0.06, playback.Observe().Positions[1].X);
        Assert.Equal(initial.InitialState.Positions[2], playback.Observe().Positions[2]);
        editor.Undo();
        Same(initial.InitialState.Observe(), playback.Observe());
        editor.BeginBrushStroke(new() { Kind = BrushKind.Attract, Radius = 0.05, Intensity = 2 }, new(0.99, 0.5));
        editor.ApplyBrushSample(new(0.99, 0.5)); editor.EndBrushStroke();
        Assert.True(playback.Observe().Velocities[0].X > 0);
        Assert.True(playback.Observe().Velocities[1].X < 0);
        Assert.Equal(default, playback.Observe().Velocities[2]);
        ReplayMatches(editor);
    }

    [Theory]
    [InlineData(BoundaryKind.Periodic)]
    [InlineData(BoundaryKind.Reflecting)]
    public void AddedAndMovedActualPayloadsObeyTheBoundaryAndUndoPremapState(BoundaryKind boundary)
    {
        ExperimentDefinition initial = new("edge tools", Parameters(boundary), new InteractionMatrix(1, [0]),
            new ParticleSnapshot([1], [new(0.95, 0.5)], [new(0.2, 0)], [0]), 0.01, new GeneratorMetadata("test", "1", 3, "test"));
        using PlaybackController playback = new(initial, diagnosticEverySteps: 0);
        ExperimentEditor editor = new(playback);
        editor.BeginBrushStroke(new() { Kind = BrushKind.Move, Radius = 0.1 }, new(0.95, 0.5));
        editor.ApplyBrushSample(new(1.15, 0.5)); editor.EndBrushStroke();
        Close(boundary == BoundaryKind.Periodic ? 0.15 : 0.85, playback.Observe().Positions[0].X);
        Assert.Equal(boundary == BoundaryKind.Periodic ? 0.2 : -0.2, playback.Observe().Velocities[0].X);
        editor.Undo(); Same(initial.InitialState.Observe(), playback.Observe());
        editor.SetGeneration(editor.Generation with { Velocity = VelocityKind.UniformDirection, Speed = 0.2 });
        editor.BeginBrushStroke(new() { Kind = BrushKind.Add, Radius = 0.08, AddCount = 4 }, new(1.15, 0.5));
        editor.ApplyBrushSample(new(1.15, 0.5)); editor.EndBrushStroke();
        Assert.Equal(5, playback.Observe().Count);
        for (int i = 1; i < playback.Observe().Count; i++)
        {
            Assert.InRange(playback.Observe().Positions[i].X, 0, 1);
            Assert.Equal(boundary == BoundaryKind.Periodic ? 0.2 : -0.2, playback.Observe().Velocities[i].X);
        }
        Assert.Equal(0, playback.Observe().Step);
        ReplayMatches(editor);
    }

    [Theory]
    [InlineData(BoundaryKind.Periodic)]
    [InlineData(BoundaryKind.Reflecting)]
    public void ExplicitGenerationRecordsMappedValuesWithoutSilentlyMappingLoadedInitials(BoundaryKind boundary)
    {
        ExperimentDefinition initial = new("off-world capture", Parameters(boundary), new InteractionMatrix(1, [0]),
            new ParticleSnapshot([1], [new(1.1, -0.2)], [new(0.1, 0)], [0]), 0.01, new GeneratorMetadata("test", "1", 3, "test"));
        using PlaybackController playback = new(initial, diagnosticEverySteps: 0);
        ExperimentEditor editor = new(playback);
        Assert.Equal(initial.InitialState.Positions[0], playback.Observe().Positions[0]);
        editor.SetGeneration(editor.Generation with { Velocity = VelocityKind.UniformDirection, Speed = 0.3 });
        editor.RegenerateVelocities();
        Assert.InRange(playback.Observe().Positions[0].X, 0, 1); Assert.InRange(playback.Observe().Positions[0].Y, 0, 1);
        Assert.Equal(boundary == BoundaryKind.Periodic ? 0.3 : -0.3, playback.Observe().Velocities[0].X);
        editor.Undo();
        Same(initial.InitialState.Observe(), playback.Observe());
        ReplayMatches(editor);
    }

    [Fact]
    public void DirectedGraphUsesEachDirectionOwnPhysicalKnotsAndZeroCorePolicy()
    {
        InteractionMatrix matrix = new(2, [0.1, 0.8, -0.6, 0.3], new(2, [0.01, 0, 0.05, 0.01], [0.1, 0.2, 0.4, 0.1]));
        SimulationConfiguration configuration = new(Parameters(BoundaryKind.Open), matrix, 0.01);
        DirectedForceCurve forward = new(configuration, 0, 1), reverse = new(configuration, 1, 0);
        Assert.Equal(0, forward.Core); Assert.Equal(0.2, forward.Cutoff); Assert.Equal(0.4, reverse.Cutoff);
        Assert.Equal(0, forward.Evaluate(0)); Assert.Equal(0, reverse.Evaluate(0));
        Close(0.16, forward.Evaluate(0.02)); Close(-0.8, reverse.Evaluate(0.01));
        Close(0.8, forward.Evaluate(forward.Peak)); Close(-0.6, reverse.Evaluate(reverse.Peak));
        Assert.Equal(0, forward.Evaluate(0.2)); Assert.NotEqual(0, reverse.Evaluate(0.2));
        foreach (double distance in new[] { 0, 0.01, 0.05, 0.1, 0.2, 0.4 })
        {
            Assert.Equal(ParticleLifeForce.RadialCoefficientAtDistance(distance, -0.6, 0.05, 0.4), reverse.Evaluate(distance));
        }
    }

    [Fact]
    public void BulkAndClipboardCoefficientChangesRetainAllDirectedRadii()
    {
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 8, 2, 1), diagnosticEverySteps: 0);
        ExperimentEditor editor = new(playback);
        editor.SetSelectedPairRanges([1], 0, 0.23);
        PairInteractionRanges ranges = editor.Configuration.Matrix.Ranges!;
        editor.ApplyMatrixCoefficients(MatrixEditing.Apply(editor.Configuration.Matrix, [new(0, 1)], MatrixBulkOperation.Set, 0.7, 1));
        Assert.True(ranges.EquivalentTo(editor.Configuration.Matrix.Ranges));
        editor.ApplyMatrixCoefficients(MatrixEditing.Import(MatrixEditing.Export(editor.Configuration.Matrix), 2), "Paste");
        Assert.True(ranges.EquivalentTo(editor.Configuration.Matrix.Ranges));
        ReplayMatches(editor);
    }

    [Fact]
    public async Task QueuedSelectedRadiiTrackLogicalPairsAcrossEarlierSpeciesGrowth()
    {
        using EditorSession session = new(new ExperimentEditor(new PlaybackController(PresetGenerator.Create(PresetKind.Random, 8, 2, 1), diagnosticEverySteps: 0)), true);
        using ManualResetEventSlim entered = new(), release = new();
        Task held = session.Run("Hold boundary", _ => { entered.Set(); release.Wait(); });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Task completed;
        try
        {
            session.ApplyPopulation(3, [3, 3, 2]);
            session.SetSelectedPairRanges([2], 0.02, 0.2); // The displayed two-species cell is receiver 1, source 0.
            session.RandomizeInteractionSelection([3], InteractionRandomization.Radii, 19, 0.25, 0.3, 0); // Receiver 1, source 1.
            completed = session.Run("Boundary", _ => { });
        }
        finally { release.Set(); }
        await held; await completed;
        PairInteractionRanges ranges = session.Publication.Configuration.Matrix.Ranges!;
        Assert.Equal(0.2, ranges.CutoffRadii[3]); Assert.Equal(0.02, ranges.CoreRadii[3]);
        Assert.InRange(ranges.CutoffRadii[4], 0.25, 0.3); Assert.Equal(0.1, ranges.CutoffRadii[2]);
    }

    private static ModelParameters Parameters(BoundaryKind boundary) => new() { Boundary = boundary, ForceScale = 0, WallStrength = 0, DampingHalfLife = null };
    private static ExperimentDefinition Fixture(BoundaryKind boundary, double cutoff = 0.1) => new("resize mapping", Parameters(boundary) with { Cutoff = cutoff },
        new InteractionMatrix(1, [0]), new ParticleSnapshot([1, 2, 3, 4], [new(0.8, 0.2), new(0.2, 0.2), new(0.4, 0.8), new(0.8, 0.8)],
            [new(0.05, 0.01), new(0.01, 0), default, default], [0, 0, 0, 0]), 0.01, new GeneratorMetadata("test", "1", 1, "test"));
    private static ParticleSnapshot Copy(ParticleStateView state) => new(state.Ids, state.Positions, state.Velocities, state.Species, state.Time, state.Step);
    private static void ReplayMatches(ExperimentEditor editor)
    {
        using Simulation replay = SessionReplay.Replay(SessionFile.Deserialize(SessionFile.Serialize(editor.CaptureSession("boundary tools"))));
        Same(editor.Playback.Observe(), replay.Observe()); Assert.True(editor.Configuration.EquivalentTo(replay.Configuration));
    }
    private static void Same(ParticleStateView expected, ParticleStateView actual)
    {
        Assert.Equal(expected.Ids.ToArray(), actual.Ids.ToArray()); Assert.Equal(expected.Species.ToArray(), actual.Species.ToArray());
        Assert.Equal(expected.Positions.ToArray(), actual.Positions.ToArray()); Assert.Equal(expected.Velocities.ToArray(), actual.Velocities.ToArray());
        Assert.Equal(expected.Time, actual.Time); Assert.Equal(expected.Step, actual.Step);
    }
    private static void Close(double expected, double actual) => Assert.InRange(Math.Abs(expected - actual), 0, 1e-12);
}
