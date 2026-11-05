using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Benchmarks;

/// <summary>Derives explicit P7 fixtures from hashed P1 states without evolving or resizing their population.</summary>
internal static class P7Inputs
{
    internal static void Prepare(string parentDirectory, string outputDirectory, string selection, string variants)
    {
        if (variants is not "global" and not "heterogeneous" and not "both")
        {
            throw new ArgumentException("P7 variants must be global, heterogeneous or both.");
        }
        string[] requested = Select(selection).ToArray();
        parentDirectory = Path.GetFullPath(parentDirectory);
        outputDirectory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            throw new IOException("Prepare P7 fixtures in a fresh empty output directory.");
        }
        string parentManifestPath = Path.Combine(parentDirectory, "inputs.json");
        string parentManifestHash = P1Inputs.HashFile(parentManifestPath);
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(parentManifestPath));
        if (manifest.RootElement.GetProperty("schemaVersion").GetInt32() != 1
            || manifest.RootElement.GetProperty("recipeVersion").GetString() != "p1/1")
        {
            throw new NotSupportedException("P7 preparation requires a P1 p1/1 input manifest.");
        }
        Dictionary<string, JsonElement> parents = manifest.RootElement.GetProperty("inputs").EnumerateArray()
            .ToDictionary(input => input.GetProperty("name").GetString()!, input => input.Clone(), StringComparer.Ordinal);
        Directory.CreateDirectory(outputDirectory);
        List<object> inputs = [], skipped = [];
        foreach (string name in requested)
        {
            if (!parents.TryGetValue(name, out JsonElement descriptor))
            {
                skipped.Add(new { name, reason = "Not present in the specified P1 manifest; no replacement was generated." });
                continue;
            }
            string parentFile = descriptor.GetProperty("file").GetString()!;
            if (Path.GetFileName(parentFile) != parentFile) { throw new IOException("P1 manifest files must be simple relative filenames."); }
            string parentPath = Path.Combine(parentDirectory, parentFile);
            string parentHash = P1Inputs.HashFile(parentPath);
            if (parentHash != descriptor.GetProperty("sha256").GetString()) { throw new IOException("P1 input hash mismatch: " + name); }
            ExperimentDefinition parent = ExperimentFile.Load(parentPath);
            if (parent.Matrix.Ranges is not null) { throw new ArgumentException("P7 P1 derivatives require an unchanged global-range parent."); }
            if (variants is "global" or "both") { Save("global", parent.Matrix); }
            // Heterogeneity is meaningful on the controlled sparse ladder; do not duplicate every huge dense fixture.
            if ((variants is "heterogeneous" or "both") && name.StartsWith("uniform-density-", StringComparison.Ordinal))
            {
                int species = parent.Matrix.SpeciesCount;
                double[] cores = new double[species * species], cutoffs = new double[cores.Length];
                for (int index = 0; index < cores.Length; index++)
                {
                    double cutoff = parent.Parameters.Cutoff * (index / species == 0 ? 1 : 0.1);
                    cutoffs[index] = cutoff;
                    cores[index] = cutoff * parent.Parameters.RepulsionFraction;
                }
                Save("heterogeneous", parent.Matrix.WithRanges(new PairInteractionRanges(species, cores, cutoffs)));
            }
            else if (variants == "heterogeneous")
            {
                skipped.Add(new { name, reason = "Heterogeneous derivatives are defined only for the uniform-density ladder." });
            }
            if (P1Inputs.HashFile(parentPath) != parentHash) { throw new IOException("P1 input changed during P7 derivation: " + name); }

            void Save(string mode, InteractionMatrix matrix)
            {
                string derivedName = $"p7-{mode}-{name}";
                ExperimentDefinition derived = new(derivedName, parent.Parameters, matrix, parent.InitialState,
                    parent.TimeStep, new GeneratorMetadata("benchmark/p7/" + mode, "1", parent.Generator.Seed, RuntimeInformation.FrameworkDescription),
                    ForceBackend.UniformGrid, 1, new ExperimentProvenance(0, 0,
                        mode == "global" ? "Unchanged P1 particle values, coefficients, world and global ranges. No global evolution yet."
                            : "Unchanged P1 particle values, coefficients and world. Receiver row 0 retains R; other receiver rows use 0.1 R, with the same core fraction. No global evolution yet.",
                        parent.Generator, parent.Name));
                string path = Path.Combine(outputDirectory, derivedName + ".experiment.json");
                SaveExperimentNew(path, derived);
                double[] actualCutoffs = Enumerable.Range(0, matrix.SpeciesCount * matrix.SpeciesCount)
                    .Select(index => matrix.CutoffRadius(index / matrix.SpeciesCount, index % matrix.SpeciesCount, parent.Parameters)).ToArray();
                inputs.Add(new
                {
                    name = derivedName,
                    file = Path.GetFileName(path),
                    sha256 = P1Inputs.HashFile(path),
                    parentFile = parentPath,
                    parentSha256 = parentHash,
                    parentDescriptor = descriptor,
                    rangeMode = mode,
                    population = parent.InitialState.Count,
                    speciesCount = matrix.SpeciesCount,
                    derived.Parameters,
                    derived.TimeStep,
                    derived.Generator,
                    derived.Provenance,
                    nominalDensity = parent.InitialState.Count / (parent.Parameters.WorldWidth * parent.Parameters.WorldHeight),
                    cutoffMinimum = actualCutoffs.Min(),
                    cutoffMaximum = actualCutoffs.Max(),
                    cutoffByDirectedPair = actualCutoffs,
                    coreByDirectedPair = Enumerable.Range(0, actualCutoffs.Length)
                        .Select(index => matrix.CoreRadius(index / matrix.SpeciesCount, index % matrix.SpeciesCount, parent.Parameters)).ToArray(),
                    globalEvolutionSteps = 0,
                    initialState = DescribeState(parent.InitialState.Observe())
                });
                Console.WriteLine("Prepared " + derivedName);
            }
        }
        if (P1Inputs.HashFile(parentManifestPath) != parentManifestHash) { throw new IOException("P1 manifest changed during preparation."); }
        WriteJsonAtomic(Path.Combine(outputDirectory, "inputs.json"), new
        {
            schemaVersion = 1,
            recipeVersion = "p7/1",
            parentManifest = parentManifestPath,
            parentManifestSha256 = parentManifestHash,
            preparedAtUtc = DateTimeOffset.UtcNow,
            selection,
            variants,
            preparedInputCount = inputs.Count,
            skippedSourceCount = skipped.Count,
            environment = LatencyRunner.CaptureEnvironment("P7 saved-fixture derivation only; no numerical steps", 1),
            stateMeaning = "Derived actual P1 values, not globally evolved states. Evolved-tiled parents retain their original tiled-motif provenance. Use measure-p7-cpu with explicit evolution to obtain an actual full-population evolved snapshot.",
            heterogeneity = "Only selected uniform-density parents receive heterogeneous derivatives. Receiver row 0 keeps global cutoff R; every other row uses 0.1 R. Core fraction, coefficients, world and population are retained. This is an explicit changed rule workload, not an equivalent optimization.",
            inputs,
            skipped
        }, overwrite: false);
    }

    private static IEnumerable<string> Select(string selection)
    {
        int[] ladder = [10_000, 50_000, 100_000, 250_000];
        return selection switch
        {
            "screen" => ["uniform-density-10000", "evolved-tiled-10000", "cluster-10000"],
            "ladder" => ladder.SelectMany(count => new[] { $"uniform-density-{count}", $"evolved-tiled-{count}" })
                .Concat(["cluster-10000", "cluster-50000"]),
            "stretch" => ["uniform-density-1000000", "evolved-tiled-1000000"],
            "all" => P1Inputs.Select("all"),
            _ => ExplicitSelection(selection)
        };
    }

    private static string[] ExplicitSelection(string selection)
    {
        string[] names = selection.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        HashSet<string> known = P1Inputs.Select("all").ToHashSet(StringComparer.Ordinal);
        if (names.Length == 0 || names.Any(name => !known.Contains(name))) { throw new ArgumentException("Unknown P7 case selection."); }
        return names.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Describes exact particle values with a stable little-endian hash, separately from the clock.</summary>
    internal static object DescribeState(ParticleStateView state) => new
    {
        count = state.Count,
        state.Step,
        state.Time,
        stateSha256 = HashState(state, includeClock: true),
        particleValuesSha256 = HashState(state, includeClock: false),
        hashFormat = "p7-state/1: little-endian count:int32, optional step:int64/time:binary64, then stored-order id:int64, x/y/vx/vy:binary64, species:int32. Clock-free particle hash compares rebased snapshots; neither hash promises cross-precision equality."
    };

    private static string HashState(ParticleStateView state, bool includeClock)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> bytes = stackalloc byte[44];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, state.Count);
        if (includeClock)
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes[4..], state.Step);
            BinaryPrimitives.WriteInt64LittleEndian(bytes[12..], BitConverter.DoubleToInt64Bits(state.Time));
        }
        hash.AppendData(bytes[..(includeClock ? 20 : 4)]);
        for (int index = 0; index < state.Count; index++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes, state.Ids[index]);
            BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], BitConverter.DoubleToInt64Bits(state.Positions[index].X));
            BinaryPrimitives.WriteInt64LittleEndian(bytes[16..], BitConverter.DoubleToInt64Bits(state.Positions[index].Y));
            BinaryPrimitives.WriteInt64LittleEndian(bytes[24..], BitConverter.DoubleToInt64Bits(state.Velocities[index].X));
            BinaryPrimitives.WriteInt64LittleEndian(bytes[32..], BitConverter.DoubleToInt64Bits(state.Velocities[index].Y));
            BinaryPrimitives.WriteInt32LittleEndian(bytes[40..], state.Species[index]);
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static void SaveExperimentNew(string path, ExperimentDefinition experiment) =>
        WriteTextAtomic(path, ExperimentFile.Serialize(experiment) + "\n", overwrite: false);

    internal static void WriteJsonAtomic(string path, object value, bool overwrite) =>
        WriteTextAtomic(path, JsonSerializer.Serialize(value, P1Inputs.JsonOptions), overwrite);

    internal static void WriteTextNew(string path, string text) => WriteTextAtomic(path, text, overwrite: false);

    private static void WriteTextAtomic(string path, string content, bool overwrite)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new(stream)) { writer.Write(content); }
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }
}
