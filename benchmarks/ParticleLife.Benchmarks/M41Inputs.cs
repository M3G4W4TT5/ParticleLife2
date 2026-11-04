using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Benchmarks;

/// <summary>Prepares complete, immutable inputs before any M4.1 timing session.</summary>
/// <remarks>
/// Saved snapshots start a new experiment at zero; the manifest preserves their original evolution
/// time, step, complete originating experiment and runtime. Preset names describe matrices, not an
/// assertion that the resulting geometry is a mature cluster or chain. The SVGs and numerical
/// morphology observations must be inspected before making such a claim.
/// </remarks>
internal static class M41Inputs
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static readonly int[] CheckpointSteps = [3_600, 14_400, 28_800];

    internal static void Prepare(string directory, string m4Directory)
    {
        Directory.CreateDirectory(directory);
        string manifestPath = Path.Combine(directory, "inputs.json");
        if (File.Exists(manifestPath))
        {
            using JsonDocument existing = JsonDocument.Parse(File.ReadAllText(manifestPath));
            foreach (JsonElement input in existing.RootElement.GetProperty("inputs").EnumerateArray())
            {
                string path = Path.Combine(directory, input.GetProperty("file").GetString()!);
                if (HashFile(path) != input.GetProperty("sha256").GetString())
                {
                    throw new IOException($"Saved input hash mismatch: {path}.");
                }
            }

            Console.WriteLine($"Verified existing manifest and input bytes: {Path.GetFullPath(manifestPath)}");
            return;
        }

        object environment = LatencyRunner.CaptureEnvironment("input generation: uniform-grid, one worker, double precision");
        List<object> inputs = [];
        List<object> evolutions = [];
        foreach (string name in BenchmarkWorkload.AllCases)
        {
            string destination = Path.Combine(directory, name + ".experiment.json");
            string retained = Path.Combine(m4Directory, name + ".experiment.json");
            if (File.Exists(retained))
            {
                CopyUnchanged(retained, destination);
            }
            else if (!File.Exists(destination))
            {
                SaveUnchanged(destination, BenchmarkWorkload.Create(name));
            }

            ExperimentDefinition experiment = ExperimentFile.Load(destination);
            inputs.Add(Describe(destination, experiment, File.Exists(retained) ? "retained-m4-bytes" : "generated-static"));
        }

        string cutoffBasePath = Path.Combine(directory, "uniform-density-10000.experiment.json");
        ExperimentDefinition cutoffBase = ExperimentFile.Load(cutoffBasePath);
        foreach (double boundary in new[] { 0.0625, 0.125 })
        {
            foreach ((string offset, double cutoff) in new[]
            {
                ("below", Math.BitDecrement(boundary)), ("at", boundary), ("above", Math.BitIncrement(boundary))
            })
            {
                string name = $"cutoff-{boundary.ToString(CultureInfo.InvariantCulture)}-{offset}-10000";
                ExperimentDefinition experiment = new(name, cutoffBase.Parameters with { Cutoff = cutoff },
                    cutoffBase.Matrix, cutoffBase.InitialState, cutoffBase.TimeStep, cutoffBase.Generator, cutoffBase.Backend);
                string path = Path.Combine(directory, name + ".experiment.json");
                SaveUnchanged(path, experiment);
                inputs.Add(Describe(path, experiment, "cutoff-sweep"));
            }
        }

        foreach (PresetKind preset in new[] { PresetKind.Random, PresetKind.Chains, PresetKind.Snakes })
        {
            string presetName = preset.ToString().ToLowerInvariant();
            string initialPath = Path.Combine(directory, $"origin-{presetName}-500.experiment.json");
            if (!File.Exists(initialPath))
            {
                SaveUnchanged(initialPath, PresetGenerator.Create(preset, 500, 7, BenchmarkWorkload.Seed,
                    backend: ForceBackend.UniformGrid));
            }

            ExperimentDefinition initial = ExperimentFile.Load(initialPath);
            if (initial.WorkerCount != 1)
            {
                throw new IOException("Evolution origins must use one worker for baseline-compatible inputs.");
            }
            inputs.Add(Describe(initialPath, initial, "evolution-origin"));
            WriteScatter(Path.Combine(directory, $"origin-{presetName}-500.svg"), initial.InitialState,
                initial.Parameters, $"{presetName}, step 0, time 0");
            Simulation simulation = initial.CreateSimulation(ForceBackend.UniformGrid, workerCountOverride: 1);
            try
            {
                List<object> checkpoints = [];
                foreach (int step in CheckpointSteps)
                {
                    Console.WriteLine($"Preparing {presetName}: advancing to step {step}, nominal time {step * initial.TimeStep:G17}.");
                    while (simulation.Observe().Step < step)
                    {
                        simulation.Step();
                    }

                    ParticleSnapshot state = simulation.CaptureSnapshot();
                    string name = $"evolved-{presetName}-500-t{step / 144}";
                    ParticleSnapshot newInitial = new(state.Ids, state.Positions, state.Velocities, state.Species);
                    ExperimentDefinition snapshot = new(name, initial.Parameters, initial.Matrix, newInitial,
                        initial.TimeStep, new GeneratorMetadata($"evolution/{presetName}", "1", initial.Generator.Seed,
                            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription), ForceBackend.UniformGrid);
                    string path = Path.Combine(directory, name + ".experiment.json");
                    SaveUnchanged(path, snapshot);
                    string imagePath = Path.Combine(directory, name + ".svg");
                    WriteScatter(imagePath, state, initial.Parameters, $"{presetName}, step {step}, time {state.Time:G6}");
                    object morphology = DescribeMorphology(state, initial.Parameters.Cutoff, initial.Parameters);
                    inputs.Add(Describe(path, snapshot, "evolved-snapshot"));
                    checkpoints.Add(new
                    {
                        file = Path.GetFileName(path),
                        sha256 = HashFile(path),
                        sampledStep = state.Step,
                        sampledTime = state.Time,
                        nominalTime = step * initial.TimeStep,
                        experimentClockRebasedToZero = true,
                        actualLastExecution = simulation.LastExecution,
                        simulation.DirectFallbackSteps,
                        simulation.AccumulationFallbackSteps,
                        morphology,
                        plot = Path.GetFileName(imagePath)
                    });
                }

                evolutions.Add(new
                {
                    preset = presetName,
                    originFile = Path.GetFileName(initialPath),
                    originSha256 = HashFile(initialPath),
                    requestedBackend = ForceBackend.UniformGrid,
                    workerCount = 1,
                    initial.TimeStep,
                    initial.Parameters,
                    initial.Generator,
                    originMorphology = DescribeMorphology(initial.InitialState, initial.Parameters.Cutoff, initial.Parameters),
                    checkpoints
                });
            }
            finally
            {
                simulation.Dispose();
            }
        }

        WriteJson(manifestPath, new
        {
            schemaVersion = 1,
            preparedAtUtc = DateTimeOffset.UtcNow,
            environment,
            modelVersion = ExperimentFile.ModelVersion,
            integratorVersion = ExperimentFile.IntegratorVersion,
            generationAlgorithmVersion = UniformGridForceEvaluator.AlgorithmVersion,
            inputFormat = "Schema 1 retained where supplied; generated inputs use original schema 2 without the optional workerCount field, which means one worker in both M4 and M4.1. Timing overrides are recorded separately in run reports.",
            selection = "19 unchanged static M4 cases; six adjacent-double cutoff cases on one fixed-density 10000 state; three preset origins and nine evolved 500-particle states.",
            cutoffSweep = new
            {
                baseFile = Path.GetFileName(cutoffBasePath),
                baseSha256 = HashFile(cutoffBasePath),
                boundaries = new[] { 0.0625, 0.125 },
                rule = "Only cutoff changes: immediately preceding representable value, boundary, immediately following representable value. Coordinates, velocities, identities, species, matrix, domain, force scale, damping, walls and timestep stay fixed."
            },
            evolutionInterpretation = "Finite observed time checkpoints, not an assertion of maturity or convergence. Saved values are authoritative. Numerical morphology and plots support a separate visual interpretation.",
            inputs,
            evolutions
        });
        WriteMorphologyGallery(directory);
        Console.WriteLine($"Prepared {inputs.Count} saved inputs: {Path.GetFullPath(manifestPath)}");
    }

    internal static IEnumerable<string> Select(string directory, string selection)
    {
        if (File.Exists(selection))
        {
            return [Path.GetFullPath(selection)];
        }

        IEnumerable<string> files = Directory.EnumerateFiles(directory, "*.experiment.json")
            .OrderBy(path => path, StringComparer.Ordinal);
        return selection switch
        {
            "all" => files.Where(path => !Path.GetFileName(path).StartsWith("origin-", StringComparison.Ordinal)),
            "static" => files.Where(path => BenchmarkWorkload.AllCases.Contains(Path.GetFileName(path).Replace(".experiment.json", "", StringComparison.Ordinal))),
            "cutoff" => files.Where(path => Path.GetFileName(path).StartsWith("cutoff-", StringComparison.Ordinal)),
            "evolved" => files.Where(path => Path.GetFileName(path).StartsWith("evolved-", StringComparison.Ordinal)),
            "representative" => files.Where(path => new[]
            {
                "uniform-fixed-500", "uniform-fixed-2000", "uniform-density-10000", "cluster-2000", "band-2000",
                "boundary-overlap-2000", "evolved-random-500-t200", "evolved-chains-500-t200", "evolved-snakes-500-t200"
            }.Contains(Path.GetFileName(path).Replace(".experiment.json", "", StringComparison.Ordinal))),
            _ => [Path.Combine(directory, selection.EndsWith(".experiment.json", StringComparison.Ordinal) ? selection : selection + ".experiment.json")]
        };
    }

    internal static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    internal static void WriteJson(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions) + "\n");

    private static object Describe(string path, ExperimentDefinition experiment, string kind) => new
    {
        file = Path.GetFileName(path),
        sha256 = HashFile(path),
        kind,
        experiment.Name,
        particleCount = experiment.InitialState.Count,
        speciesCount = experiment.Matrix.SpeciesCount,
        experiment.Parameters,
        experiment.TimeStep
    };

    private static void SaveUnchanged(string path, ExperimentDefinition experiment)
    {
        string content = SerializeSerialInput(experiment);
        if (File.Exists(path) && File.ReadAllText(path) != content)
        {
            throw new IOException($"Refusing to replace different saved values at {path}. Use a fresh input directory.");
        }

        if (!File.Exists(path))
        {
            File.WriteAllText(path, content);
        }
    }

    /// <summary>Writes original schema-2 serial semantics so accepted M4 and M4.1 consume identical bytes.</summary>
    /// <remarks>Only the optional scheduling field is omitted. Actual floating-point values, matrix,
    /// saved search and all model settings are retained. The experiment serializer itself is unchanged.</remarks>
    private static string SerializeSerialInput(ExperimentDefinition experiment)
    {
        if (experiment.WorkerCount != 1)
        {
            throw new ArgumentException("Baseline-compatible input preparation requires one worker.", nameof(experiment));
        }

        JsonNode document = JsonNode.Parse(ExperimentFile.Serialize(experiment))!;
        document.AsObject().Remove("workerCount");
        return document.ToJsonString(JsonOptions) + "\n";
    }

    private static void CopyUnchanged(string source, string destination)
    {
        if (File.Exists(destination) && HashFile(source) != HashFile(destination))
        {
            throw new IOException($"Different retained input already exists at {destination}.");
        }

        if (!File.Exists(destination))
        {
            File.Copy(source, destination);
        }
    }

    /// <summary>Records quantitative geometry without assigning a morphological or physical label.</summary>
    private static object DescribeMorphology(ParticleSnapshot state, double cutoff, ModelParameters parameters)
    {
        const int side = 20;
        int[] bins = new int[side * side];
        int offWorld = 0;
        double[] nearest = new double[state.Count];
        for (int i = 0; i < state.Count; i++)
        {
            Vector2d position = state.Positions[i];
            int x = (int)Math.Floor(position.X / parameters.WorldWidth * side);
            int y = (int)Math.Floor(position.Y / parameters.WorldHeight * side);
            if ((uint)x < side && (uint)y < side)
            {
                bins[y * side + x]++;
            }
            else
            {
                offWorld++;
            }

            double minimum = double.PositiveInfinity;
            for (int j = 0; j < state.Count; j++)
            {
                if (j != i)
                {
                    Vector2d delta = state.Positions[j] - position;
                    minimum = Math.Min(minimum, double.Hypot(delta.X, delta.Y));
                }
            }

            nearest[i] = minimum;
        }

        Array.Sort(nearest);
        double meanOccupancy = bins.Average();
        return new
        {
            gridBinsPerSide = side,
            gridBinWidth = parameters.WorldWidth / side,
            gridBinHeight = parameters.WorldHeight / side,
            occupiedBins = bins.Count(count => count > 0),
            maximumBinPopulation = bins.Max(),
            meanBinPopulation = meanOccupancy,
            binPopulationCoefficientOfVariation = Math.Sqrt(bins.Average(count => Math.Pow(count - meanOccupancy, 2))) / meanOccupancy,
            offWorldParticles = offWorld,
            nearestNeighbourMedian = nearest[(nearest.Length - 1) / 2],
            nearestNeighbourP95 = nearest[(int)Math.Ceiling(0.95 * nearest.Length) - 1],
            components = new[] { 0.03, 0.05, cutoff }.Distinct().Select(radius =>
                ParticleLife.Core.Experiments.Diagnostics.Sample(state.Observe(), radius)).ToArray()
        };
    }

    private static void WriteScatter(string path, ParticleSnapshot state, ModelParameters parameters, string label)
    {
        string[] palette = ["#df496e", "#f3b233", "#52c18d", "#61b2f1", "#ba83ec", "#ff8e51", "#e1e5f1"];
        double minX = Math.Min(0, state.Positions.ToArray().Min(point => point.X));
        double minY = Math.Min(0, state.Positions.ToArray().Min(point => point.Y));
        double extent = Math.Max(Math.Max(parameters.WorldWidth, state.Positions.ToArray().Max(point => point.X)) - minX,
            Math.Max(parameters.WorldHeight, state.Positions.ToArray().Max(point => point.Y)) - minY);
        double scale = 540 / extent;
        StringBuilder svg = new();
        svg.AppendLine("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"600\" height=\"620\" viewBox=\"0 0 600 620\">");
        svg.AppendLine("<rect width=\"600\" height=\"620\" fill=\"#101522\"/>");
        svg.AppendLine($"<text x=\"30\" y=\"24\" fill=\"#f0f2f7\" font-family=\"sans-serif\" font-size=\"15\">{label}</text>");
        svg.AppendLine(FormattableString.Invariant($"<rect x=\"{30 - minX * scale:G8}\" y=\"{50 - minY * scale:G8}\" width=\"{parameters.WorldWidth * scale:G8}\" height=\"{parameters.WorldHeight * scale:G8}\" fill=\"none\" stroke=\"#586071\"/>"));
        for (int i = 0; i < state.Count; i++)
        {
            Vector2d position = state.Positions[i];
            svg.AppendLine(FormattableString.Invariant($"<circle cx=\"{30 + (position.X - minX) * scale:G8}\" cy=\"{50 + (position.Y - minY) * scale:G8}\" r=\"2.6\" fill=\"{palette[state.Species[i] % palette.Length]}\"/>"));
        }

        svg.AppendLine("<text x=\"30\" y=\"610\" fill=\"#aeb8c9\" font-family=\"sans-serif\" font-size=\"12\">Colours identify species. Outline is the nominal soft-wall world.</text></svg>");
        File.WriteAllText(path, svg.ToString());
    }

    private static void WriteMorphologyGallery(string directory)
    {
        StringBuilder html = new("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>M4.1 observed geometries</title>");
        html.Append("<style>body{margin:24px;background:#101522;color:#f0f2f7;font:16px system-ui}h1{font-size:24px}.row{display:grid;grid-template-columns:repeat(4,minmax(240px,1fr));gap:8px}img{width:100%}p{max-width:1000px;line-height:1.5}</style>");
        html.Append("<h1>Saved geometry observations</h1><p>Each row uses one explicit preset matrix and the same saved originating experiment. Columns show time 0, 25, 100 and 200 in reduced units. These observations do not establish maturity or convergence. Colours identify species; outlines show the nominal soft-wall world. Exact values and generation provenance are in inputs.json and the linked experiments.</p>");
        foreach (string preset in new[] { "random", "chains", "snakes" })
        {
            html.Append($"<h2>{preset} matrix</h2><div class=\"row\">");
            html.Append($"<a href=\"origin-{preset}-500.experiment.json\"><img alt=\"{preset} initial state\" src=\"origin-{preset}-500.svg\"></a>");
            foreach (int step in CheckpointSteps)
            {
                string stem = $"evolved-{preset}-500-t{step / 144}";
                html.Append($"<a href=\"{stem}.experiment.json\"><img alt=\"{preset}, time {step / 144}\" src=\"{stem}.svg\"></a>");
            }

            html.Append("</div>");
        }

        html.Append("</html>");
        File.WriteAllText(Path.Combine(directory, "morphology.html"), html.ToString());
    }
}
