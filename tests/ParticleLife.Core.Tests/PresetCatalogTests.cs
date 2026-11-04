using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Core.Tests;

public sealed class PresetCatalogTests
{
    public static TheoryData<PresetKind, double[]> CyclicMatrixRows => new()
    {
        { PresetKind.AlternatingBands, [-0.6, 0.8] },
        { PresetKind.Ribbons, [-0.2, 0.8, -0.5, -0.5, -0.5, -0.5, 0.8] },
        { PresetKind.CyclicPursuit, [0.3, 0.8, -0.8] },
        { PresetKind.ChaseCycle5, [0.141 / 1.07, -1, 0, 0, 0.929 / 1.07] },
        { PresetKind.ChaseCycle6, [0.141 / 1.07, -1, 0, 0, 0, 0.929 / 1.07] },
        { PresetKind.SpeciesIslands, [0.7, -0.35, -0.35, -0.35, -0.35, -0.35] },
        { PresetKind.BiasedRibbons, [-0.2, 0.95, -0.5, -0.5, -0.5, -0.5, 0.65] }
    };

    [Fact]
    public void CatalogueHasStableKeysAndDoesNotExposeMutableEntries()
    {
        string[] expected = ["random", "chains", "snakes", "core-halo", "alternating-bands", "ribbons",
            "cyclic-pursuit", "chase-cycle-5", "chase-cycle-6", "species-islands", "biased-ribbons"];
        Assert.Equal(expected, PresetCatalog.All.Select(recipe => recipe.Key));
        Assert.Equal(Enum.GetValues<PresetKind>(), PresetCatalog.All.Select(recipe => recipe.Kind));
        Assert.Equal(0, (int)PresetKind.Random);
        Assert.Equal(1, (int)PresetKind.Chains);
        Assert.Equal(2, (int)PresetKind.Snakes);
        foreach (PresetInfo recipe in PresetCatalog.All)
        {
            Assert.Same(recipe, PresetCatalog.Get(recipe.Kind));
            Assert.Equal(recipe.Kind, PresetCatalog.Parse(recipe.Key));
            Assert.True(PresetCatalog.SupportsSpeciesCount(recipe.Kind, recipe.DefaultSpeciesCount));
        }

        Assert.True(PresetCatalog.Get(PresetKind.BiasedRibbons).IsExperimental);
        Assert.Contains("Experimental", PresetCatalog.Get(PresetKind.BiasedRibbons).Description);
        IList<PresetInfo> list = Assert.IsAssignableFrom<IList<PresetInfo>>(PresetCatalog.All);
        Assert.Throws<NotSupportedException>(() => list[0] = list[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("0")]
    [InlineData("CoreHalo")]
    [InlineData("core-halo ")]
    [InlineData("unknown")]
    public void InvalidKeysAreNotSilentlyCoerced(string key)
    {
        Assert.ThrowsAny<ArgumentException>(() => PresetCatalog.Parse(key));
    }

    [Fact]
    public void UndefinedKindsFailAcrossCatalogueAndGeneration()
    {
        PresetKind unknown = (PresetKind)99;
        Assert.Throws<ArgumentOutOfRangeException>(() => PresetCatalog.Get(unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => PresetCatalog.SupportsSpeciesCount(unknown, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => PresetCatalog.CreatePopulations(unknown, 0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => PresetGenerator.CreateMatrix(unknown, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => PresetGenerator.Create(unknown));
    }

    [Theory]
    [MemberData(nameof(CyclicMatrixRows))]
    public void MatricesMatchTheirDirectedRowsIncludingWraparound(PresetKind kind, double[] firstRow)
    {
        InteractionMatrix matrix = PresetGenerator.CreateMatrix(kind, firstRow.Length);
        Assert.Equal(firstRow, matrix.Values[..firstRow.Length].ToArray());
        for (int receiver = 0; receiver < firstRow.Length; receiver++)
        {
            for (int source = 0; source < firstRow.Length; source++)
            {
                // Relabelling all species cyclically preserves the network; this also exercises wraparound.
                int offset = (source - receiver + firstRow.Length) % firstRow.Length;
                Assert.Equal(firstRow[offset], matrix[receiver, source]);
            }
        }

        Assert.Equal(matrix.Values.ToArray(), PresetGenerator.CreateMatrix(kind, firstRow.Length, 987).Values.ToArray());
    }

    [Fact]
    public void CoreHaloRolesAndPursuitOrientationAreExplicit()
    {
        InteractionMatrix coreHalo = PresetGenerator.CreateMatrix(PresetKind.CoreHalo, 2);
        Assert.Equal(new double[] { 0.7, 0.2, 0.9, -0.6 }, coreHalo.Values.ToArray());
        InteractionMatrix pursuit = PresetGenerator.CreateMatrix(PresetKind.CyclicPursuit, 3);
        Assert.Equal(new double[] { 0.3, 0.8, -0.8, -0.8, 0.3, 0.8, 0.8, -0.8, 0.3 }, pursuit.Values.ToArray());
        Assert.Equal(0.9, coreHalo[1, 0]); // Halo receives strong attraction toward a core.
        Assert.Equal(0.8, pursuit[2, 0]); // Last species pursues the first.
        Assert.Equal(-0.8, pursuit[0, 2]);
    }

    [Fact]
    public void RibbonBiasRetainsReciprocalAttractionWhileAddingAntisymmetricDrive()
    {
        InteractionMatrix ribbons = PresetGenerator.CreateMatrix(PresetKind.Ribbons, 7);
        InteractionMatrix biased = PresetGenerator.CreateMatrix(PresetKind.BiasedRibbons, 7);
        for (int i = 0; i < 7; i++)
        {
            for (int j = 0; j < 7; j++)
            {
                Assert.Equal(ribbons[i, j], (biased[i, j] + biased[j, i]) / 2, 14);
            }
        }
        Assert.Equal(0.15, (biased[0, 1] - biased[1, 0]) / 2, 14);
    }

    [Theory]
    [InlineData(PresetKind.CoreHalo, 3)]
    [InlineData(PresetKind.AlternatingBands, 1)]
    [InlineData(PresetKind.CyclicPursuit, 2)]
    [InlineData(PresetKind.ChaseCycle5, 6)]
    [InlineData(PresetKind.ChaseCycle6, 5)]
    [InlineData(PresetKind.Ribbons, 2)]
    [InlineData(PresetKind.BiasedRibbons, 2)]
    [InlineData(PresetKind.SpeciesIslands, 1)]
    [InlineData(PresetKind.Snakes, 0)]
    [InlineData(PresetKind.Random, -1)]
    public void IncompatibleSpeciesCountsAreRejectedBeforeGeneration(PresetKind kind, int count)
    {
        Assert.False(PresetCatalog.SupportsSpeciesCount(kind, count));
        Assert.Throws<ArgumentOutOfRangeException>(() => PresetGenerator.CreateMatrix(kind, count));
        Assert.Throws<ArgumentOutOfRangeException>(() => PresetCatalog.CreatePopulations(kind, 0, count));
        Assert.Throws<ArgumentOutOfRangeException>(() => PresetGenerator.Create(kind, 0, count));
    }

    [Theory]
    [InlineData(PresetKind.Ribbons, 3)]
    [InlineData(PresetKind.Ribbons, 8)]
    [InlineData(PresetKind.BiasedRibbons, 3)]
    [InlineData(PresetKind.SpeciesIslands, 2)]
    [InlineData(PresetKind.Chains, 1)]
    [InlineData(PresetKind.Snakes, 2)]
    public void VariableSpeciesRecipesRemainAvailableForExperimentation(PresetKind kind, int count)
    {
        Assert.True(PresetCatalog.SupportsSpeciesCount(kind, count));
        Assert.Equal(count, PresetGenerator.CreateMatrix(kind, count).SpeciesCount);
    }

    [Fact]
    public void PopulationPoliciesKeepEveryParticleAndHandleExtremeTotals()
    {
        Assert.Equal(new[] { 84, 336 }, PresetCatalog.CreatePopulations(PresetKind.CoreHalo, 420, 2));
        Assert.Equal(new[] { 0, 1 }, PresetCatalog.CreatePopulations(PresetKind.CoreHalo, 1, 2));
        Assert.Equal(new[] { 1, 2 }, PresetCatalog.CreatePopulations(PresetKind.CoreHalo, 3, 2));
        Assert.Equal(new[] { 2, 6 }, PresetCatalog.CreatePopulations(PresetKind.CoreHalo, 8, 2));
        Assert.Equal(new[] { 3, 2, 2 }, PresetCatalog.CreatePopulations(PresetKind.CyclicPursuit, 7, 3));
        Assert.Equal(new[] { 1, 1, 0, 0, 0 }, PresetCatalog.CreatePopulations(PresetKind.ChaseCycle5, 2, 5));
        foreach (PresetInfo recipe in PresetCatalog.All)
        {
            int[] empty = PresetCatalog.CreatePopulations(recipe.Kind, 0, recipe.DefaultSpeciesCount);
            Assert.All(empty, count => Assert.Equal(0, count));
            int[] extreme = PresetCatalog.CreatePopulations(recipe.Kind, int.MaxValue, recipe.DefaultSpeciesCount);
            Assert.Equal(int.MaxValue, extreme.Sum(count => (long)count));
            Assert.All(extreme, count => Assert.True(count >= 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => PresetCatalog.CreatePopulations(recipe.Kind, -1, recipe.DefaultSpeciesCount));
        }

        int[] first = PresetCatalog.CreatePopulations(PresetKind.CoreHalo, 420, 2);
        first[0] = 0;
        Assert.Equal(84, PresetCatalog.CreatePopulations(PresetKind.CoreHalo, 420, 2)[0]);
    }

    [Fact]
    public void NewRecipesUseExactPopulationsAndCommonPositionSamples()
    {
        Vector2d[]? commonPositions = null;
        foreach (PresetInfo recipe in PresetCatalog.All.Skip(3))
        {
            ExperimentDefinition experiment = PresetGenerator.Create(recipe.Kind, seed: 31);
            Assert.Equal(420, experiment.InitialState.Count);
            Assert.Equal(recipe.DefaultSpeciesCount, experiment.Matrix.SpeciesCount);
            Assert.Equal(PresetCatalog.CreatePopulations(recipe.Kind, 420, recipe.DefaultSpeciesCount),
                CountSpecies(experiment));
            Assert.All(experiment.InitialState.Velocities.ToArray(), velocity => Assert.Equal(default, velocity));
            Assert.Equal(Enumerable.Range(0, 420).Select(i => (long)i), experiment.InitialState.Ids.ToArray());
            Vector2d[] positions = experiment.InitialState.Positions.ToArray();
            Assert.All(positions, p => Assert.True(p.X >= 0 && p.X < 1 && p.Y >= 0 && p.Y < 1));
            commonPositions ??= positions;
            Assert.Equal(commonPositions, positions);
            Assert.Equal($"uniform-prescribed-{recipe.Key}", experiment.Generator.Name);
            Assert.Equal("1", experiment.Generator.Version);
            Assert.Equal(31, experiment.Generator.Seed);
            Assert.NotEmpty(experiment.Generator.Runtime);
            Assert.Equal(0, experiment.InitialState.Time);
            Assert.Equal(0, experiment.InitialState.Step);
            string serialized = ExperimentFile.Serialize(experiment);
            Assert.Equal(serialized, ExperimentFile.Serialize(PresetGenerator.Create(recipe.Kind, seed: 31)));
            Assert.Equal(serialized, ExperimentFile.Serialize(ExperimentFile.Deserialize(serialized)));
        }
    }

    [Fact]
    public void CoreHaloSeedOneRetainsTheScreenedInitialPrefix()
    {
        // Captured from the 2026-11-04 actual-core recipe screen, not read from ignored research files.
        // This is a regression for the pinned runtime, not a promise across different generator runtimes.
        ExperimentDefinition experiment = PresetGenerator.Create(PresetKind.CoreHalo);
        Vector2d[] expected = [new(0.24866858415709278, 0.11074397718102856),
            new(0.46701067987224587, 0.7716041220219825), new(0.657518893786482, 0.43278260130099144),
            new(0.3540837636003661, 0.9438622761256351)];
        Assert.Equal(expected, experiment.InitialState.Positions[..4].ToArray());
        Assert.Equal(new[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 1 }, experiment.InitialState.Species[..16].ToArray());
    }

    [Fact]
    public void ExplicitGenerationSettingsOverrideDefaultsWithoutLosingRecipePopulations()
    {
        ModelParameters parameters = new() { WorldWidth = 2, WorldHeight = 3, ForceScale = 0.6 };
        ExperimentDefinition experiment = PresetGenerator.Create(PresetKind.CoreHalo, 17, 2, 9,
            parameters, 0.002, ForceBackend.UniformGrid, 4);
        Assert.Equal(new[] { 3, 14 }, CountSpecies(experiment));
        Assert.Equal(parameters, experiment.Parameters);
        Assert.Equal(0.002, experiment.TimeStep);
        Assert.Equal(ForceBackend.UniformGrid, experiment.Backend);
        Assert.Equal(4, experiment.WorkerCount);
        Assert.All(experiment.InitialState.Positions.ToArray(), p => Assert.True(p.X >= 0 && p.X < 2 && p.Y >= 0 && p.Y < 3));
        Assert.Empty(PresetGenerator.Create(PresetKind.CoreHalo, 0).InitialState.Positions.ToArray());
    }

    [Theory]
    [InlineData(PresetKind.Random)]
    [InlineData(PresetKind.Chains)]
    [InlineData(PresetKind.Snakes)]
    public void HistoricalRecipesRetainTheirVersionOneRandomStream(PresetKind kind)
    {
        const int seed = 726, count = 31, speciesCount = 7;
        ExperimentDefinition experiment = PresetGenerator.Create(kind, count, speciesCount, seed);
        Random versionOne = new(seed);
        if (kind == PresetKind.Random)
        {
            // Version one consumes the entire random matrix before interleaving x, y and species draws.
            foreach (double coefficient in experiment.Matrix.Values)
            {
                Assert.Equal(2 * versionOne.NextDouble() - 1, coefficient);
            }
        }

        for (int i = 0; i < count; i++)
        {
            Assert.Equal(new Vector2d(versionOne.NextDouble(), versionOne.NextDouble()), experiment.InitialState.Positions[i]);
            Assert.Equal(versionOne.Next(speciesCount), experiment.InitialState.Species[i]);
        }
        string key = kind.ToString().ToLowerInvariant();
        Assert.Equal(key, experiment.Name);
        Assert.Equal($"uniform-{key}", experiment.Generator.Name);
        Assert.Equal("1", experiment.Generator.Version);
        ExperimentDefinition defaults = PresetGenerator.Create(kind);
        Assert.Equal(500, defaults.InitialState.Count);
        Assert.Equal(7, defaults.Matrix.SpeciesCount);
        Assert.Equal(experiment.Matrix.Values.ToArray(), PresetGenerator.CreateMatrix(kind, speciesCount, seed).Values.ToArray());
    }

    private static int[] CountSpecies(ExperimentDefinition experiment)
    {
        int[] counts = new int[experiment.Matrix.SpeciesCount];
        foreach (int species in experiment.InitialState.Species)
        {
            counts[species]++;
        }
        return counts;
    }
}
