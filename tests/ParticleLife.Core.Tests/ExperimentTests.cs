using System.Text.Json;
using System.Text.Json.Nodes;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Core.Tests;

public sealed class ExperimentTests
{
    private static ExperimentDefinition Fixture() => ExperimentFile.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "two-species.json"));

    [Theory]
    [InlineData(ForceBackend.Direct)]
    [InlineData(ForceBackend.UniformGrid)]
    public void SavedFixtureLoadsAndReplaysExactlyAfterRoundTrip(ForceBackend backend)
    {
        ExperimentDefinition first = Fixture().WithBackend(backend);
        string json = ExperimentFile.Serialize(first);
        ExperimentDefinition second = ExperimentFile.Deserialize(json);
        Assert.Equal(json, ExperimentFile.Serialize(second));
        Assert.Equal(first.Matrix.Values.ToArray(), second.Matrix.Values.ToArray());
        Assert.Equal(first.InitialState.Ids.ToArray(), second.InitialState.Ids.ToArray());
        Assert.Equal(first.InitialState.Positions.ToArray(), second.InitialState.Positions.ToArray());
        Assert.Equal(first.InitialState.Velocities.ToArray(), second.InitialState.Velocities.ToArray());
        Assert.Equal(first.InitialState.Species.ToArray(), second.InitialState.Species.ToArray());
        Assert.Equal(backend, second.Backend);
        var a = first.CreateSimulation();
        var b = second.CreateSimulation();
        for (int i = 0; i < 200; i++)
        {
            a.Step();
            b.Step();
        }

        // Exact equality is appropriate here: same saved doubles, runtime, solver and operation order.
        Assert.Equal(a.Observe().Positions.ToArray(), b.Observe().Positions.ToArray());
        Assert.Equal(a.Observe().Velocities.ToArray(), b.Observe().Velocities.ToArray());
        Assert.Equal(a.Observe().Time, b.Observe().Time);
        Assert.Equal(backend, b.LastExecution!.Value.ActualBackend);
    }

    [Fact]
    public void LegacyFixtureKeepsDirectMeaningAndSavesSeparateModelIntegratorAndSearch()
    {
        ExperimentDefinition legacy = Fixture();
        Assert.Equal(ForceBackend.Direct, legacy.Backend);
        using JsonDocument saved = JsonDocument.Parse(ExperimentFile.Serialize(legacy));
        JsonElement root = saved.RootElement;
        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("particle-life/1", root.GetProperty("modelVersion").GetString());
        Assert.Equal("damped-semi-implicit-euler/1", root.GetProperty("integratorVersion").GetString());
        Assert.Equal("direct/1", root.GetProperty("neighbourSearchVersion").GetString());
        Assert.False(root.TryGetProperty("solverVersion", out _));
    }

    [Fact]
    public void ExplicitOverrideChangesExecutionWithoutMutatingSourceInitialConditions()
    {
        ExperimentDefinition source = Fixture();
        Simulation overridden = source.CreateSimulation(ForceBackend.UniformGrid);
        Assert.Equal(ForceBackend.Direct, source.Backend);
        Assert.Equal(ForceBackend.UniformGrid, overridden.Backend);
        Assert.Null(overridden.LastExecution);
        overridden.Step();
        Assert.Equal(ForceBackend.UniformGrid, overridden.LastExecution!.Value.ActualBackend);
        ExperimentDefinition effective = source.WithBackend(ForceBackend.UniformGrid);
        Assert.Same(source.InitialState, effective.InitialState);
        Assert.Same(source.Matrix, effective.Matrix);
        Assert.Equal(source.Generator, effective.Generator);
        Assert.Equal(ForceBackend.UniformGrid, ExperimentFile.Deserialize(ExperimentFile.Serialize(effective)).Backend);
    }

    [Fact]
    public void InvalidBackendEnumsAreRejectedAtConfigurationBoundaries()
    {
        ExperimentDefinition fixture = Fixture();
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.WithBackend((ForceBackend)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.CreateSimulation((ForceBackend)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => PresetGenerator.Create(PresetKind.Random, backend: (ForceBackend)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExperimentFile.GetNeighbourSearchVersion((ForceBackend)99));
    }

    [Fact]
    public void LegacySolverIdentityCannotBeReinterpretedAsGrid()
    {
        string legacy = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "two-species.json"));
        Assert.Throws<NotSupportedException>(() => ExperimentFile.Deserialize(legacy.Replace(ExperimentFile.SolverVersion, "grid/1")));
        Assert.Throws<JsonException>(() => ExperimentFile.Deserialize(legacy.Replace("\"solverVersion\":", "\"unexpected\": 1, \"solverVersion\":")));
        JsonObject missing = JsonNode.Parse(legacy)!.AsObject();
        missing.Remove("solverVersion");
        Assert.Throws<JsonException>(() => ExperimentFile.Deserialize(missing.ToJsonString()));
    }

    [Fact]
    public void ActualValuesTakePrecedenceOverGeneratorMetadata()
    {
        ExperimentDefinition original = Fixture();
        ExperimentDefinition alteredProvenance = new(original.Name, original.Parameters, original.Matrix,
            original.InitialState, original.TimeStep, new GeneratorMetadata("different-generator", "99", 999, "other-runtime"));
        var a = original.CreateSimulation();
        var b = ExperimentFile.Deserialize(ExperimentFile.Serialize(alteredProvenance)).CreateSimulation();
        a.Step();
        b.Step();
        Assert.Equal(a.Observe().Positions.ToArray(), b.Observe().Positions.ToArray());
        Assert.Equal(a.Observe().Velocities.ToArray(), b.Observe().Velocities.ToArray());
    }

    [Theory]
    [InlineData("schemaVersion", "999")]
    [InlineData("modelVersion", "\"other/1\"")]
    [InlineData("integratorVersion", "\"other/1\"")]
    [InlineData("neighbourSearchVersion", "\"approximate/1\"")]
    [InlineData("boundary", "\"periodic\"")]
    [InlineData("units", "\"SI\"")]
    public void UnsupportedConventionsAreRejected(string property, string value)
    {
        JsonNode node = JsonNode.Parse(ExperimentFile.Serialize(Fixture()))!;
        node[property] = JsonNode.Parse(value);
        Assert.Throws<NotSupportedException>(() => ExperimentFile.Deserialize(node.ToJsonString()));
    }

    [Theory]
    [InlineData("matrix")]
    [InlineData("initialState")]
    [InlineData("generator")]
    [InlineData("timeStep")]
    [InlineData("integratorVersion")]
    [InlineData("neighbourSearchVersion")]
    public void MissingRequiredFieldsAreRejected(string property)
    {
        JsonObject node = JsonNode.Parse(ExperimentFile.Serialize(Fixture()))!.AsObject();
        node.Remove(property);
        Assert.Throws<JsonException>(() => ExperimentFile.Deserialize(node.ToJsonString()));
    }

    [Fact]
    public void MissingParameterOrCoordinateDoesNotSilentlyUseADefault()
    {
        JsonNode node = JsonNode.Parse(ExperimentFile.Serialize(Fixture()))!;
        node["parameters"]!.AsObject().Remove("wallStrength");
        Assert.Throws<JsonException>(() => ExperimentFile.Deserialize(node.ToJsonString()));
        node = JsonNode.Parse(ExperimentFile.Serialize(Fixture()))!;
        node["initialState"]!["positions"]![0]!.AsObject().Remove("x");
        Assert.Throws<JsonException>(() => ExperimentFile.Deserialize(node.ToJsonString()));
    }

    [Fact]
    public void UnknownAndDuplicateFieldsAreRejected()
    {
        string json = ExperimentFile.Serialize(Fixture());
        Assert.Throws<JsonException>(() => ExperimentFile.Deserialize(json.Replace("\"name\":", "\"unknown\": 1, \"name\":")));
        Assert.Throws<JsonException>(() => ExperimentFile.Deserialize(json.Replace("\"schemaVersion\": 2", "\"schemaVersion\": 2, \"schemaVersion\": 2")));
        Assert.Throws<JsonException>(() => ExperimentFile.Deserialize(json.Replace("\"name\":", "\"solverVersion\": \"direct-damped-semi-implicit-euler/1\", \"name\":")));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"x\":1e999,\"y\":0}")]
    public void InvalidPositionJsonIsRejected(string point)
    {
        JsonNode node = JsonNode.Parse(ExperimentFile.Serialize(Fixture()))!;
        node["initialState"]!["positions"]![0] = JsonNode.Parse(point);
        Exception? error = Record.Exception(() => ExperimentFile.Deserialize(node.ToJsonString()));
        Assert.True(error is JsonException or ArgumentException, $"Expected a format or validation error; got {error?.GetType().Name ?? "no error"}.");
    }

    [Fact]
    public void InvalidSavedSpeciesAndIdentitiesAreRejected()
    {
        JsonNode node = JsonNode.Parse(ExperimentFile.Serialize(Fixture()))!;
        node["initialState"]!["species"]![0] = 2;
        Assert.Throws<ArgumentException>(() => ExperimentFile.Deserialize(node.ToJsonString()));
        node = JsonNode.Parse(ExperimentFile.Serialize(Fixture()))!;
        node["initialState"]!["ids"]![0] = 20;
        Assert.Throws<ArgumentException>(() => ExperimentFile.Deserialize(node.ToJsonString()));
    }

    [Theory]
    [InlineData(PresetKind.Random)]
    [InlineData(PresetKind.Chains)]
    [InlineData(PresetKind.Snakes)]
    public void PresetsRepeatWithinTheSameRuntimeAndRoundTrip(PresetKind kind)
    {
        ExperimentDefinition first = PresetGenerator.Create(kind, 31, 7, 726);
        ExperimentDefinition second = PresetGenerator.Create(kind, 31, 7, 726);
        Assert.Equal(ExperimentFile.Serialize(first), ExperimentFile.Serialize(second));
        Assert.Equal(ExperimentFile.Serialize(first), ExperimentFile.Serialize(ExperimentFile.Deserialize(ExperimentFile.Serialize(first))));
        Assert.All(first.InitialState.Positions.ToArray(), p => Assert.True(p.X >= 0 && p.X < 1 && p.Y >= 0 && p.Y < 1));
        Assert.All(first.InitialState.Velocities.ToArray(), v => Assert.Equal(default, v));
        Assert.All(first.Matrix.Values.ToArray(), value => Assert.InRange(value, -1, 1));
    }

    [Fact]
    public void LegacyChainAndSnakeMatricesHaveExplicitDirectedRules()
    {
        InteractionMatrix chains = PresetGenerator.Create(PresetKind.Chains, 0, 5).Matrix;
        InteractionMatrix snakes = PresetGenerator.Create(PresetKind.Snakes, 0, 5).Matrix;
        Assert.Equal(1, chains[0, 4]);
        Assert.Equal(1, chains[4, 0]);
        Assert.Equal(-1, chains[0, 2]);
        Assert.Equal(1, snakes[0, 0]);
        Assert.Equal(0.2, snakes[0, 1]);
        Assert.Equal(0, snakes[1, 0]);
        Assert.Equal(0.2, snakes[4, 0]);
        Assert.Equal(0.2, PresetGenerator.Create(PresetKind.Snakes, 0, 1).Matrix[0, 0]);
    }

    [Fact]
    public void DiagnosticsUseConnectedComponentsAndDoNotChangeTrajectories()
    {
        ParticleSnapshot state = new([0, 1, 2, 3, 4],
            [new(0, 0), new(0, 0), new(0.1, 0), new(0.2, 0), new(1, 0)],
            [new(3, 4), default, default, default, default], [0, 0, 0, 0, 0]);
        DiagnosticSample sample = Diagnostics.Sample(state.Observe(), 0.1);
        Assert.Equal(5, sample.FiniteCount);
        Assert.Equal(1, sample.OverlapPairs);
        Assert.Equal(2, sample.ClusterCount);
        Assert.Equal(4, sample.LargestCluster);
        Assert.Equal(5, sample.MaxSpeed);
        Assert.InRange(Math.Abs(sample.MeanSpeed - 1), 0, 1e-15);

        var measured = Fixture().CreateSimulation();
        var unmeasured = Fixture().CreateSimulation();
        for (int i = 0; i < 20; i++)
        {
            measured.Step();
            _ = Diagnostics.Sample(measured.Observe(), 0.1);
            unmeasured.Step();
        }

        Assert.Equal(unmeasured.Observe().Positions.ToArray(), measured.Observe().Positions.ToArray());
        Assert.Equal(unmeasured.Observe().Velocities.ToArray(), measured.Observe().Velocities.ToArray());
    }

    [Fact]
    public void EmptyDiagnosticsAndDisabledDampingRoundTrip()
    {
        ExperimentDefinition empty = PresetGenerator.Create(PresetKind.Random, 0, parameters: new ModelParameters { DampingHalfLife = null });
        Assert.Null(ExperimentFile.Deserialize(ExperimentFile.Serialize(empty)).Parameters.DampingHalfLife);
        DiagnosticSample sample = Diagnostics.Sample(empty.InitialState.Observe(), 0.1);
        Assert.Equal(0, sample.ClusterCount);
        Assert.Equal(0, sample.LargestCluster);
        Assert.Equal(0, sample.MeanSpeed);
    }
}
