using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using ParticleLife.Viewer;

namespace ParticleLife.Viewer.Tests;

public sealed class ExplorationTests
{
    [Theory]
    [InlineData(0.02, -0.4, 1.7)]
    [InlineData(1, 0.5, 0.5)]
    [InlineData(100, 0.123, 0.876)]
    public void CameraInverseAndPointerAnchoredZoomAgree(double zoom, double centreX, double centreY)
    {
        CameraSettings camera = new() { Zoom = zoom, CenterX = centreX, CenterY = centreY };
        WorldTransform transform = new(4, 2, 100, 50, 900, 500, camera);
        Vector2d position = new(0.75, 1.2);
        Vector2d recovered = transform.ToWorld(transform.ToScreen(position));
        Close(position.X, recovered.X); Close(position.Y, recovered.Y);
        Vector2d anchor = new(470, 230);
        Vector2d anchoredWorld = transform.ToWorld(anchor);
        CameraSettings target = camera.ZoomAt(transform, anchor, 1.7, 4, 2);
        WorldTransform zoomed = new(4, 2, 100, 50, 900, 500, target);
        Vector2d mapped = zoomed.ToScreen(anchoredWorld);
        Close(anchor.X, mapped.X); Close(anchor.Y, mapped.Y);
        Close(transform.WorldLengthToPixels(0.1), transform.NormalizedScale * 0.1 / transform.WorldExtent);
    }

    [Theory]
    [InlineData(PlacementKind.Uniform)]
    [InlineData(PlacementKind.Grid)]
    [InlineData(PlacementKind.Disk)]
    [InlineData(PlacementKind.Ring)]
    [InlineData(PlacementKind.Line)]
    [InlineData(PlacementKind.Spiral)]
    [InlineData(PlacementKind.Stripe)]
    [InlineData(PlacementKind.Cluster)]
    [InlineData(PlacementKind.SpeciesGroups)]
    public void IndependentGenerationRetainsOtherComponentsAndReplaysActualValues(PlacementKind placement)
    {
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Chains, 18, 3, 31), diagnosticEverySteps: 0);
        ExperimentEditor editor = new(playback);
        playback.SingleStep();
        editor.SetGeneration(editor.Generation with { Placement = placement, Velocity = VelocityKind.Tangential, Speed = 0.4 });
        ParticleSnapshot original = Copy(playback.Observe());
        editor.RegeneratePositions();
        ParticleSnapshot positioned = Copy(playback.Observe());
        Assert.Equal(original.Ids.ToArray(), positioned.Ids.ToArray());
        Assert.Equal(original.Species.ToArray(), positioned.Species.ToArray());
        Assert.Equal(original.Velocities.ToArray(), positioned.Velocities.ToArray());
        Assert.Equal(original.Time, positioned.Time);
        Assert.All(positioned.Positions.ToArray(), point =>
        {
            Assert.InRange(point.X, 0, playback.Configuration.Parameters.WorldWidth);
            Assert.InRange(point.Y, 0, playback.Configuration.Parameters.WorldHeight);
        });
        editor.RegenerateVelocities(1);
        ParticleSnapshot velocities = Copy(playback.Observe());
        Assert.Equal(positioned.Positions.ToArray(), velocities.Positions.ToArray());
        for (int i = 0; i < velocities.Count; i++)
        {
            if (velocities.Species[i] != 1) { Assert.Equal(positioned.Velocities[i], velocities.Velocities[i]); }
        }
        editor.ReassignSpecies(SpeciesAssignmentKind.Rotate, 0.5);
        ParticleSnapshot reassigned = Copy(playback.Observe());
        Assert.Equal(velocities.Positions.ToArray(), reassigned.Positions.ToArray());
        Assert.Equal(velocities.Velocities.ToArray(), reassigned.Velocities.ToArray());
        Assert.Equal(9, Enumerable.Range(0, reassigned.Count).Count(i => reassigned.Species[i] != velocities.Species[i]));
        playback.SingleStep();
        using Simulation replay = SessionReplay.Replay(editor.CaptureSession("independent generators"));
        Same(playback.Observe(), replay.Observe());
    }

    [Fact]
    public void BrushStrokeGroupsUndoAndPreservesUnrelatedCurrentMotion()
    {
        using PlaybackController playback = new(Fixture(), diagnosticEverySteps: 0);
        ExperimentEditor editor = new(playback);
        BrushSettings brush = new() { Kind = BrushKind.Move, Radius = 0.12 };
        editor.BeginBrushStroke(brush, new(0.25, 0.2));
        editor.ApplyBrushSample(new(0.25, 0.2));
        playback.SingleStep();
        ParticleSnapshot firstTouched = Copy(playback.Observe());
        editor.ApplyBrushSample(new(0.35, 0.3));
        playback.SingleStep();
        editor.ApplyBrushSample(new(0.4, 0.35));
        editor.EndBrushStroke();
        ParticleSnapshot moved = Copy(playback.Observe());
        Assert.Equal(2, editor.SessionEventCount);
        editor.Undo();
        Assert.False(editor.CanUndo);
        Assert.Equal(2, playback.Observe().Step);
        Assert.Equal(firstTouched.Positions[0], playback.Observe().Positions[0]);
        Assert.Equal(moved.Positions[2], playback.Observe().Positions[2]);
        playback.SingleStep();
        Vector2d unrelated = playback.Observe().Positions[2];
        editor.Redo();
        Assert.Equal(moved.Positions[0], playback.Observe().Positions[0]);
        Assert.Equal(unrelated, playback.Observe().Positions[2]);
        using Simulation replay = SessionReplay.Replay(editor.CaptureSession("brush undo"));
        Same(playback.Observe(), replay.Observe());
    }

    [Fact]
    public void RejectedBrushSampleDoesNotExpandTheEarlierUndoFootprint()
    {
        ExperimentDefinition fixture = new("failed sample", new ModelParameters { ForceScale = 0, WallStrength = 0, DampingHalfLife = null },
            new InteractionMatrix(1, [0]), new ParticleSnapshot([1, 2], [new(0.2, 0.2), new(0.8, 0.8)],
                [default, new(1e308, 0)], [0, 0]), 1, new GeneratorMetadata("fixture", "1", 1, "test"));
        using PlaybackController playback = new(fixture, diagnosticEverySteps: 0);
        ExperimentEditor editor = new(playback);
        BrushSettings brush = new() { Kind = BrushKind.DirectionalForce, Radius = 0.05, Intensity = 1e308 };
        editor.BeginBrushStroke(brush, new(0.2, 0.2));
        editor.ApplyBrushSample(new(0.2, 0.2));
        Assert.ThrowsAny<ArgumentException>(() => editor.ApplyBrushSample(new(0.8, 0.8)));
        Assert.Equal(1, editor.SessionEventCount);
        playback.SingleStep();
        Vector2d unaffectedMotion = playback.Observe().Positions[1];
        editor.EndBrushStroke();
        editor.Undo();
        Assert.Equal(unaffectedMotion, playback.Observe().Positions[1]);
        Assert.Equal(fixture.InitialState.Positions[0], playback.Observe().Positions[0]);
        using Simulation replay = SessionReplay.Replay(editor.CaptureSession("failed brush sample"));
        Same(playback.Observe(), replay.Observe());
    }

    [Fact]
    public async Task CoalescedPointerPreviewsRetainTheWholeMoveAndOneUndoAction()
    {
        using EditorSession session = new(new ExperimentEditor(new PlaybackController(Fixture(), diagnosticEverySteps: 0)), true);
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        Task blocked = session.Run("Busy boundary", _ => { entered.Set(); release.Wait(); });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        SessionDefinition? saved = null;
        Task completed;
        try
        {
            session.BeginBrushStroke(new BrushSettings { Kind = BrushKind.Move, Radius = 0.12 }, new(0.25, 0.2));
            session.ApplyBrushSample(new(0.25, 0.2));
            session.ApplyBrushSample(new(0.3, 0.25));
            session.ApplyBrushSample(new(0.4, 0.4));
            session.EndBrushStroke();
            completed = session.Run("Capture", editor => saved = editor.CaptureSession("coalesced move"));
        }
        finally { release.Set(); }
        await blocked;
        await completed;
        Assert.Single(saved!.Events);
        using Simulation replay = SessionReplay.Replay(saved);
        Close(0.35, replay.Observe().Positions[0].X); Close(0.4, replay.Observe().Positions[0].Y);
        await session.Run("Undo", editor => editor.Undo());
        Assert.False(session.CanUndo);
        Assert.Equal(2, session.SessionEventCount);
    }

    [Theory]
    [InlineData(BrushKind.Add)]
    [InlineData(BrushKind.PaintSpecies)]
    [InlineData(BrushKind.Erase)]
    [InlineData(BrushKind.Attract)]
    [InlineData(BrushKind.Repel)]
    [InlineData(BrushKind.DirectionalForce)]
    public void DirectToolsCaptureActualBoundaryStateAndUndoWithoutAdvancing(BrushKind kind)
    {
        using PlaybackController playback = new(Fixture(), diagnosticEverySteps: 0);
        ExperimentEditor editor = new(playback);
        BrushSettings brush = new() { Kind = kind, Radius = 0.15, Species = 1, AddCount = 3, Intensity = 2 };
        editor.BeginBrushStroke(brush, new(0.25, 0.2));
        editor.ApplyBrushSample(new(0.25, 0.2));
        editor.EndBrushStroke();
        ParticleSnapshot applied = Copy(playback.Observe());
        Assert.Equal(0, applied.Step);
        Assert.Equal(kind == BrushKind.Add ? 6 : kind == BrushKind.Erase ? 1 : 3, applied.Count);
        if (kind == BrushKind.PaintSpecies) { Assert.Equal(new[] { 1, 1, 0 }, applied.Species.ToArray()); }
        using (Simulation replay = SessionReplay.Replay(editor.CaptureSession("direct tool"))) { Same(playback.Observe(), replay.Observe()); }
        editor.Undo();
        Same(Fixture().InitialState.Observe(), playback.Observe());
        editor.Redo();
        Same(applied.Observe(), playback.Observe());
        if (kind == BrushKind.Add)
        {
            long highWater = applied.Ids.ToArray().Max();
            editor.Undo();
            editor.BeginBrushStroke(brush, new(0.7, 0.7)); editor.ApplyBrushSample(new(0.7, 0.7)); editor.EndBrushStroke();
            Assert.All(playback.Observe().Ids.ToArray().Skip(3), identity => Assert.True(identity > highWater));
        }
    }

    [Fact]
    public void MatrixSelectionBulkAndClipboardRetainDirectedExactValues()
    {
        InteractionMatrix matrix = new(3, [-0.9, 0.12345678901234567, 0.3, 0.4, 0.5, -0.6, 0.7, -0.8, 0.9]);
        MatrixSelection selection = new(3);
        selection.Select(0, 1); selection.Select(2, 2, extend: true);
        Assert.Equal(6, selection.Count);
        selection.Select(1, 1, additive: true);
        Assert.Equal(5, selection.Count);
        double[] changed = MatrixEditing.Apply(matrix, selection.Cells, MatrixBulkOperation.Set, 0.2, 1, linkReverse: true);
        Assert.Equal(-0.9, changed[0]);
        Assert.Equal(0.2, changed[1]); Assert.Equal(0.2, changed[3]);
        Assert.Equal(matrix.Values.ToArray(), MatrixEditing.Import(MatrixEditing.Export(matrix), 3));
        Assert.Throws<ArgumentException>(() => MatrixEditing.Import("0\tNaN\n0\t0", 2));
        Assert.Throws<ArgumentException>(() => MatrixEditing.Import("0\t1.1\n0\t0", 2));
        Assert.Throws<ArgumentException>(() => MatrixEditing.Import("0\t0", 2));
        Assert.Throws<ArgumentException>(() => MatrixEditing.Import("0,,1\n0,0", 2));
        selection.SelectRow(1); Assert.Equal(3, selection.Count);
        selection.SelectColumn(2); Assert.Equal(3, selection.Count);
        double[] random = MatrixEditing.Apply(matrix, selection.Cells, MatrixBulkOperation.Randomize, 0, 54);
        Assert.Equal(random, MatrixEditing.Apply(matrix, selection.Cells, MatrixBulkOperation.Randomize, 0, 54));
        double[] linked = MatrixEditing.Apply(matrix, [new MatrixCell(0, 1)], MatrixBulkOperation.Randomize, 0, 54, linkReverse: true);
        Assert.Equal(linked[1], linked[3]);
    }

    [Fact]
    public void CameraPaletteAndSizePersistWithoutScientificEventsOrTrajectoryChanges()
    {
        ExperimentDefinition fixture = Fixture();
        using PlaybackController playback = new(fixture, diagnosticEverySteps: 0);
        using PlaybackController reference = new(fixture, diagnosticEverySteps: 0);
        ExperimentEditor editor = new(playback);
        AppearanceSettings appearance = editor.Appearance.WithPalette(PaletteCatalog.Create(PaletteKind.HighContrast, 2)) with
        {
            Camera = new CameraSettings { Zoom = 2.5, CenterX = 1.1, CenterY = -0.2, Smooth = true },
            ParticleSizeMode = ParticleSizeMode.WorldRelative,
            WorldParticleRadius = 0.02
        };
        editor.SetAppearance(appearance);
        PresetDefinition restored = PresetFile.Deserialize(PresetFile.Serialize(editor.CapturePreset("appearance")));
        Assert.Equal(appearance.Camera, restored.Appearance.Camera);
        Assert.Equal(appearance.ParticleSizeMode, restored.Appearance.ParticleSizeMode);
        Assert.Equal(appearance.WorldParticleRadius, restored.Appearance.WorldParticleRadius);
        Assert.Equal(appearance.SpeciesColors.ToArray(), restored.Appearance.SpeciesColors.ToArray());
        Assert.Equal(0, editor.SessionEventCount);
        playback.SingleStep(); reference.SingleStep();
        Same(reference.Observe(), playback.Observe());
    }

    private static ExperimentDefinition Fixture() => new("tools", new ModelParameters { ForceScale = 0, WallStrength = 0, DampingHalfLife = null },
        new InteractionMatrix(2, [0, 0, 0, 0]),
        new ParticleSnapshot([10, 11, 12], [new(0.2, 0.2), new(0.3, 0.2), new(0.8, 0.8)],
            [new(0.01, 0.02), new(-0.01, 0), new(0.03, 0.04)], [0, 0, 0]), 0.01,
        new GeneratorMetadata("fixture", "1", 7, "test"));
    private static ParticleSnapshot Copy(ParticleStateView state) => new(state.Ids, state.Positions, state.Velocities, state.Species, state.Time, state.Step);
    private static void Same(ParticleStateView expected, ParticleStateView actual)
    {
        Assert.Equal(expected.Ids.ToArray(), actual.Ids.ToArray()); Assert.Equal(expected.Species.ToArray(), actual.Species.ToArray());
        Assert.Equal(expected.Positions.ToArray(), actual.Positions.ToArray()); Assert.Equal(expected.Velocities.ToArray(), actual.Velocities.ToArray());
        Assert.Equal(expected.Step, actual.Step); Assert.Equal(expected.Time, actual.Time);
    }
    private static void Close(double expected, double actual) => Assert.True(Math.Abs(expected - actual) <= 1e-9 * Math.Max(1, Math.Abs(expected)));
}
