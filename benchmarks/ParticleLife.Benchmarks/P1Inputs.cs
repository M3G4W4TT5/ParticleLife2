using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Benchmarks;

/// <summary>Defines the tracked P1 workload recipe and materializes complete inputs outside timing.</summary>
/// <remarks>
/// Large evolved inputs repeat a small completed motif, including its velocities, in translated unit
/// tiles. They have not themselves evolved for 720 steps. Tiles interact normally at their seams;
/// there are no internal walls or independent tile simulations in the saved large experiment.
/// </remarks>
internal static class P1Inputs
{
    internal const int Seed = 1729;
    internal const int MotifPopulation = 500;
    internal const int MotifSteps = 720;
    internal static readonly int[] Populations = [10_000, 50_000, 100_000, 250_000, 1_000_000];
    internal static readonly string[] Geometries = ["uniform-density", "uniform-fixed", "band", "cluster", "evolved-tiled"];
    internal static JsonSerializerOptions JsonOptions => M41Inputs.JsonOptions;

    internal static IEnumerable<string> Select(string selection, bool preparing = false)
    {
        IEnumerable<string> all = Populations.SelectMany(count => Geometries.Select(geometry => $"{geometry}-{count}"));
        return selection switch
        {
            "screen" => preparing ? all.Where(name => Population(name) == 10_000)
                : ["uniform-density-10000", "evolved-tiled-10000"],
            "density" => all.Where(name => name.StartsWith("uniform-density-", StringComparison.Ordinal) && Population(name) < 1_000_000),
            "ladder" => all.Where(name => Population(name) < 1_000_000),
            "stress" => all.Where(name => Population(name) < 1_000_000 && IsDense(name)),
            "stretch" => all.Where(name => Population(name) == 1_000_000),
            "all" => all,
            _ when all.Contains(selection, StringComparer.Ordinal) => [selection],
            _ => throw new ArgumentException($"Unknown P1 selection '{selection}'.")
        };
    }

    internal static void Prepare(string directory, string selection)
    {
        string[] requested = Select(selection, preparing: true).ToArray();
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        string manifestPath = Path.Combine(directory, "inputs.json");
        if (File.Exists(manifestPath))
        {
            using JsonDocument manifest = ReadVerifiedManifest(directory);
            HashSet<string> names = manifest.RootElement.GetProperty("inputs").EnumerateArray()
                .Select(input => input.GetProperty("name").GetString()!).ToHashSet(StringComparer.Ordinal);
            if (requested.Any(name => !names.Contains(name)))
            {
                throw new IOException("This immutable input directory lacks requested cases; prepare a new directory.");
            }

            Console.WriteLine($"Verified saved inputs: {manifestPath}");
            return;
        }

        if (Directory.EnumerateFileSystemEntries(directory).Any())
        {
            throw new IOException("Prepare P1 inputs in an empty directory; partial preparations are not silently overwritten.");
        }

        List<object> sources = [];
        ExperimentDefinition? motif = null;
        if (requested.Any(name => name.StartsWith("evolved-tiled-", StringComparison.Ordinal)))
        {
            ExperimentDefinition origin = PresetGenerator.Create(PresetKind.Chains, MotifPopulation, 7, Seed,
                backend: ForceBackend.UniformGrid);
            sources.Add(Save(directory, "motif-origin", origin, "uniform Chains origin, not timed"));
            using Simulation simulation = origin.CreateSimulation();
            for (int step = 0; step < MotifSteps; step++)
            {
                simulation.Step();
            }

            ParticleSnapshot completed = simulation.CaptureSnapshot();
            motif = new ExperimentDefinition("p1-evolved-motif", origin.Parameters, origin.Matrix,
                new ParticleSnapshot(completed.Ids, completed.Positions, completed.Velocities, completed.Species),
                origin.TimeStep, origin.Generator, ForceBackend.UniformGrid,
                provenance: new ExperimentProvenance(completed.Time, completed.Step, "completed motif rebased to zero", origin.Generator, origin.Name));
            sources.Add(Save(directory, "motif-evolved", motif, "500-particle Chains motif after 720 serial grid steps; clock rebased"));
        }

        List<object> inputs = [];
        foreach (string name in requested)
        {
            ExperimentDefinition experiment = Create(name, motif);
            inputs.Add(Save(directory, name, experiment, name[..name.LastIndexOf('-')]));
            Console.WriteLine($"Prepared {name}");
        }

        WriteJson(manifestPath, new
        {
            schemaVersion = 1,
            recipeVersion = "p1/1",
            preparedAtUtc = DateTimeOffset.UtcNow,
            environment = LatencyRunner.CaptureEnvironment("P1 generation: CPU double, serial uniform-grid motif evolution"),
            seed = Seed,
            selection,
            modelVersion = ExperimentFile.ModelVersion,
            integratorVersion = ExperimentFile.IntegratorVersion,
            stateMeaning = "Complete saved initial state is authoritative. Seeds and generation runtime alone are not a cross-runtime identity guarantee.",
            geometryRules = new
            {
                uniformDensity = "Square side sqrt(N/500), density 500 per reduced area.",
                uniformFixed = "Unit square, increasing density at unchanged cutoff.",
                band = "Unit square, x uniform in [0,1), y uniform in [0.49,0.51).",
                cluster = "Unit square, x/y uniform in [0.475,0.525); every initial distinct pair lies inside cutoff 0.1.",
                evolvedTiled = "Exact translated copies of motif-evolved, near-square factorized unit tiles with no empty tiles. New IDs; unchanged velocities/species/matrix. Not globally evolved geometry."
            },
            sources,
            inputs
        });
    }

    /// <summary>Verifies every authoritative saved file, including evolved-source provenance, before reuse.</summary>
    internal static JsonDocument ReadVerifiedManifest(string directory)
    {
        JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "inputs.json")));
        try
        {
            if (document.RootElement.GetProperty("schemaVersion").GetInt32() != 1
                || document.RootElement.GetProperty("recipeVersion").GetString() != "p1/1")
            {
                throw new IOException("Unsupported P1 input manifest.");
            }

            foreach (string group in new[] { "sources", "inputs" })
            {
                foreach (JsonElement input in document.RootElement.GetProperty(group).EnumerateArray())
                {
                    string file = input.GetProperty("file").GetString()!;
                    if (Path.GetFileName(file) != file || HashFile(Path.Combine(directory, file)) != input.GetProperty("sha256").GetString())
                    {
                        throw new IOException($"Saved P1 input hash mismatch: {file}.");
                    }
                }
            }

            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    /// <remarks>
    /// All five geometries retain cutoff 0.1 and core fraction 0.3. Only uniform-density and tiled
    /// evolved worlds grow with population. Band/cluster velocities begin at zero; evolved velocities
    /// are copied from the actual completed motif. No neighbours are truncated during generation.
    /// </remarks>
    private static ExperimentDefinition Create(string name, ExperimentDefinition? motif)
    {
        int count = Population(name);
        bool tiled = name.StartsWith("evolved-tiled-", StringComparison.Ordinal);
        double side = name.StartsWith("uniform-density-", StringComparison.Ordinal) ? Math.Sqrt(count / 500d) : 1;
        int tilesX = 1;
        if (tiled)
        {
            int tiles = count / MotifPopulation;
            tilesX = (int)Math.Sqrt(tiles);
            while (tiles % tilesX != 0)
            {
                tilesX--;
            }

            side = tilesX;
        }

        ModelParameters parameters = new() { WorldWidth = side, WorldHeight = tiled ? count / MotifPopulation / tilesX : side };
        ExperimentDefinition source = tiled ? motif! : PresetGenerator.Create(PresetKind.Random, count, 7, Seed, parameters,
            backend: ForceBackend.UniformGrid);
        long[] ids = new long[count];
        Vector2d[] positions = new Vector2d[count];
        Vector2d[] velocities = new Vector2d[count];
        int[] species = new int[count];
        for (int i = 0; i < count; i++)
        {
            int sourceIndex = tiled ? i % MotifPopulation : i;
            Vector2d position = source.InitialState.Positions[sourceIndex];
            if (tiled)
            {
                int tile = i / MotifPopulation;
                position = new Vector2d(position.X + tile % tilesX, position.Y + tile / tilesX);
            }
            else if (name.StartsWith("band-", StringComparison.Ordinal))
            {
                position = new Vector2d(position.X, 0.49 + 0.02 * position.Y);
            }
            else if (name.StartsWith("cluster-", StringComparison.Ordinal))
            {
                position = new Vector2d(0.475 + 0.05 * position.X, 0.475 + 0.05 * position.Y);
            }

            ids[i] = i;
            positions[i] = position;
            velocities[i] = source.InitialState.Velocities[sourceIndex];
            species[i] = source.InitialState.Species[sourceIndex];
        }

        return new ExperimentDefinition(name, parameters, source.Matrix, new ParticleSnapshot(ids, positions, velocities, species),
            source.TimeStep, new GeneratorMetadata($"benchmark/p1/{name}", "1", Seed, RuntimeInformation.FrameworkDescription),
            ForceBackend.UniformGrid, provenance: tiled ? new ExperimentProvenance(source.Provenance!.SourceTime,
                MotifSteps, "translated copies of a completed 500-particle motif; large world has not evolved", source.Generator, source.Name) : null);
    }

    private static object Save(string directory, string name, ExperimentDefinition experiment, string geometry)
    {
        string path = Path.Combine(directory, name + ".experiment.json");
        ExperimentFile.Save(path, experiment);
        return new
        {
            name,
            file = Path.GetFileName(path),
            sha256 = HashFile(path),
            fileBytes = new FileInfo(path).Length,
            population = experiment.InitialState.Count,
            speciesCount = experiment.Matrix.SpeciesCount,
            geometry,
            experiment.Parameters,
            experiment.TimeStep,
            experiment.Backend,
            experiment.WorkerCount,
            experiment.Generator,
            experiment.Provenance,
            precision = "IEEE 754 binary64 state/force/integration",
            boundary = "soft walls",
            nominalDensity = experiment.InitialState.Count / (experiment.Parameters.WorldWidth * experiment.Parameters.WorldHeight),
            ranges = new
            {
                pairCount = experiment.Matrix.SpeciesCount * experiment.Matrix.SpeciesCount,
                cutoffMinimum = experiment.Parameters.Cutoff,
                cutoffMaximum = experiment.Parameters.Cutoff,
                coreMinimum = experiment.Parameters.Cutoff * experiment.Parameters.RepulsionFraction,
                coreMaximum = experiment.Parameters.Cutoff * experiment.Parameters.RepulsionFraction,
                distribution = "All directed species pairs use one global cutoff/core. Complete matrix coefficients are in the hashed experiment."
            }
        };
    }

    internal static int Population(string name) => int.Parse(name[(name.LastIndexOf('-') + 1)..], CultureInfo.InvariantCulture);

    private static bool IsDense(string name) => name.StartsWith("uniform-fixed-", StringComparison.Ordinal)
        || name.StartsWith("band-", StringComparison.Ordinal) || name.StartsWith("cluster-", StringComparison.Ordinal);

    internal static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static void WriteJson(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));
}
