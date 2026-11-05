using System.Text.Json.Nodes;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;

namespace ParticleLife.Core.Tests;

public sealed class WorkerConfigurationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void WorkerSelectionRoundTripsAndCreatesMatchingSimulation(int workers)
    {
        ExperimentDefinition initial = PresetGenerator.Create(PresetKind.Random, 12, 3, 19,
            backend: ForceBackend.UniformGrid, workerCount: workers);
        ExperimentDefinition saved = ExperimentFile.Deserialize(ExperimentFile.Serialize(initial));
        Assert.Equal(workers, saved.WorkerCount);
        using Simulation simulation = saved.CreateSimulation();
        simulation.Step();
        Assert.Equal(workers, simulation.LastExecution!.Value.ConfiguredWorkerCount);
        Assert.Equal(workers, simulation.LastExecution.Value.ActualWorkerCount);
        using Simulation direct = saved.CreateSimulation(ForceBackend.Direct);
        Assert.Equal(1, direct.WorkerCount);
        Assert.Equal(workers, saved.WithBackend(ForceBackend.UniformGrid).WorkerCount);
        Assert.Equal(1, saved.WithBackend(ForceBackend.Direct).WorkerCount);
    }

    [Fact]
    public void OlderSchemaTwoDefaultsToSerialWithoutChangingSavedSearch()
    {
        ExperimentDefinition experiment = PresetGenerator.Create(PresetKind.Random, 8, 2, backend: ForceBackend.UniformGrid);
        JsonObject document = JsonNode.Parse(ExperimentFile.Serialize(experiment))!.AsObject();
        document["schemaVersion"] = 2;
        document["modelVersion"] = ExperimentFile.LegacyModelVersion;
        document["matrix"]!.AsObject().Remove("pairRanges");
        document.Remove("workerCount");
        ExperimentDefinition loaded = ExperimentFile.Deserialize(document.ToJsonString());
        Assert.Equal(ForceBackend.UniformGrid, loaded.Backend);
        Assert.Equal(1, loaded.WorkerCount);
    }

    [Theory]
    [InlineData(ForceBackend.Direct, 2)]
    [InlineData(ForceBackend.UniformGrid, 0)]
    [InlineData(ForceBackend.UniformGrid, 9)]
    public void InvalidWorkerSettingsAreRejectedAtExperimentBoundary(ForceBackend backend, int workers)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PresetGenerator.Create(PresetKind.Random,
            backend: backend, workerCount: workers));
        JsonObject document = JsonNode.Parse(ExperimentFile.Serialize(PresetGenerator.Create(PresetKind.Random,
            backend: backend)))!.AsObject();
        document["workerCount"] = workers;
        Assert.Throws<ArgumentOutOfRangeException>(() => ExperimentFile.Deserialize(document.ToJsonString()));
    }

    [Fact]
    public void DisposingWorkersIsIdempotentAndPreventsNewStepsAndResets()
    {
        ExperimentDefinition experiment = PresetGenerator.Create(PresetKind.Random, 8, 2,
            backend: ForceBackend.UniformGrid, workerCount: 4);
        Simulation simulation = experiment.CreateSimulation();
        simulation.Step();
        simulation.Dispose();
        simulation.Dispose();
        Assert.Equal(1, simulation.Observe().Step);
        Assert.Throws<ObjectDisposedException>(simulation.Step);
        Assert.Throws<ObjectDisposedException>(() => simulation.Reset(experiment.InitialState));
    }
}
