using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using ParticleLife.Viewer;

namespace ParticleLife.Viewer.Tests;

public sealed class ExperimentEditingTests
{
    [Theory]
    [InlineData(ForceBackend.Direct)]
    [InlineData(ForceBackend.UniformGrid)]
    public void StateResamplePreservesExactMatrixModelIdentitiesAndSpecies(ForceBackend backend)
    {
        ExperimentDefinition original = PresetGenerator.Create(PresetKind.Snakes, 12, 3, 31, backend: backend);
        ExperimentDefinition resampled = ExperimentEditing.ResampleState(original, 917);
        Assert.Same(original.Parameters, resampled.Parameters);
        Assert.Same(original.Matrix, resampled.Matrix);
        Assert.Equal(original.TimeStep, resampled.TimeStep);
        Assert.Equal(backend, resampled.Backend);
        Assert.Equal(original.InitialState.Ids.ToArray(), resampled.InitialState.Ids.ToArray());
        Assert.Equal(original.InitialState.Species.ToArray(), resampled.InitialState.Species.ToArray());
        Assert.NotEqual(original.InitialState.Positions.ToArray(), resampled.InitialState.Positions.ToArray());
        foreach (Vector2d velocity in resampled.InitialState.Velocities)
        {
            Assert.Equal(default, velocity);
        }

        foreach (Vector2d position in resampled.InitialState.Positions)
        {
            Assert.InRange(position.X, 0, original.Parameters.WorldWidth);
            Assert.InRange(position.Y, 0, original.Parameters.WorldHeight);
        }

        Assert.Equal(917, resampled.Generator.Seed);
        Assert.Equal("viewer-uniform-state-resample", resampled.Generator.Name);
        PlaybackController controller = new(resampled);
        controller.SingleStep();
        controller.Reset();
        PlaybackControllerTests.SameState(resampled.InitialState, controller.Observe());
    }

    [Theory]
    [InlineData(ForceBackend.Direct)]
    [InlineData(ForceBackend.UniformGrid)]
    public void MatrixChangePreservesExactInitialStateModelAndTimestep(ForceBackend backend)
    {
        ExperimentDefinition original = PresetGenerator.Create(PresetKind.Chains, 12, 3, 31, backend: backend);
        PlaybackController controller = new(original);
        controller.SingleStep();
        ExperimentDefinition changed = ExperimentEditing.RandomizeMatrix(controller.Experiment, 19);
        Assert.Same(original.Parameters, changed.Parameters);
        Assert.Same(original.InitialState, changed.InitialState);
        Assert.Equal(original.TimeStep, changed.TimeStep);
        Assert.Equal(backend, changed.Backend);
        Assert.NotEqual(original.Matrix.Values.ToArray(), changed.Matrix.Values.ToArray());
        Assert.Equal(19, changed.Generator.Seed);
        Assert.Equal("viewer-random-matrix", changed.Generator.Name);
        controller.ReplaceExperiment(changed);
        PlaybackControllerTests.SameState(original.InitialState, controller.Observe());
        Assert.Equal(0, controller.Observe().Time);
    }

    [Theory]
    [InlineData(true, ForceBackend.Direct)]
    [InlineData(false, ForceBackend.Direct)]
    [InlineData(true, ForceBackend.UniformGrid)]
    [InlineData(false, ForceBackend.UniformGrid)]
    public void GeneratedValuesAndOperationSeedSurviveExistingExperimentFormat(bool resample, ForceBackend backend)
    {
        ExperimentDefinition original = PresetGenerator.Create(PresetKind.Random, 8, 4, 33, backend: backend);
        ExperimentDefinition changed = resample
            ? ExperimentEditing.ResampleState(original, -771)
            : ExperimentEditing.RandomizeMatrix(original, -771);
        ExperimentDefinition loaded = ExperimentFile.Deserialize(ExperimentFile.Serialize(changed));
        Assert.Equal(changed.Generator, loaded.Generator);
        Assert.Equal(backend, loaded.Backend);
        Assert.Equal(changed.Matrix.Values.ToArray(), loaded.Matrix.Values.ToArray());
        PlaybackControllerTests.SameState(changed.InitialState, loaded.InitialState.Observe());
    }

    [Fact]
    public void SaveNewCreatesParentsAndPreservesExistingFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "particle-life-save-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "nested", "experiment.json");
        ExperimentDefinition experiment = PresetGenerator.Create(PresetKind.Random, 8, 3, 23);
        try
        {
            ExperimentStorage.SaveNew(path, experiment);
            byte[] originalBytes = File.ReadAllBytes(path);
            Assert.Equal(ExperimentFile.Serialize(experiment), ExperimentFile.Serialize(ExperimentFile.Load(path)));
            Assert.Throws<IOException>(() => ExperimentStorage.SaveNew(path, ExperimentEditing.RandomizeMatrix(experiment, 55)));
            Assert.Equal(originalBytes, File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentSavesAllowOnlyOneWriter()
    {
        string directory = Path.Combine(Path.GetTempPath(), "particle-life-save-race-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "experiment.json");
        ExperimentDefinition experiment = PresetGenerator.Create(PresetKind.Random, 8, 3, 23);
        try
        {
            Task<bool>[] tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                try
                {
                    ExperimentStorage.SaveNew(path, experiment);
                    return true;
                }
                catch (IOException)
                {
                    return false;
                }
            })).ToArray();
            bool[] saved = await Task.WhenAll(tasks);
            Assert.Single(saved, value => value);
            Assert.Equal(ExperimentFile.Serialize(experiment), ExperimentFile.Serialize(ExperimentFile.Load(path)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
