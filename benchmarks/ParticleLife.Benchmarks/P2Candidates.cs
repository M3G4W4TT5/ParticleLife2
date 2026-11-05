using System.Diagnostics;
using System.Numerics;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Benchmarks;

/// <summary>Contains disposable benchmark probes; no probe is a saved or production numerical mode.</summary>
/// <remarks>Ordinary-range candidates deliberately exclude subnormal and extreme coordinates. The
/// production direct evaluator remains independent and is the force/count oracle for bounded inputs.
/// Layout, geometry and arithmetic probes are individually labelled so a changed neighbour set or
/// precision cannot be mistaken for an equivalent exact optimization.</remarks>
internal static class P2Candidates
{
    internal static void Prepare(string outputDirectory, string inputPath)
    {
        Directory.CreateDirectory(outputDirectory);
        if (Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            throw new IOException("Prepare P2 derivatives in a fresh directory.");
        }
        ExperimentDefinition source = ExperimentFile.Load(inputPath);
        if (source.InitialState.Count > 10_000)
        {
            throw new ArgumentException("Bounded P2 derivatives support at most 10000 particles.");
        }
        List<object> inputs = [];
        foreach ((string name, double cutoff) in new[] { ("below", Math.BitDecrement(0.125)), ("at", 0.125), ("above", Math.BitIncrement(0.125)) })
        {
            Save(new ExperimentDefinition("cutoff-" + name, source.Parameters with { Cutoff = cutoff }, source.Matrix,
                source.InitialState, source.TimeStep, source.Generator, ForceBackend.UniformGrid, 1,
                new ExperimentProvenance(0, 0, "P2 cutoff-transition derivative", source.Generator, source.Name)));
        }
        using Simulation simulation = source.CreateSimulation(ForceBackend.UniformGrid, 4);
        for (int i = 0; i < 120; i++) { simulation.Step(); }
        ParticleSnapshot completed = simulation.CaptureSnapshot();
        Save(new ExperimentDefinition("evolved-120", source.Parameters, source.Matrix,
            new ParticleSnapshot(completed.Ids, completed.Positions, completed.Velocities, completed.Species),
            source.TimeStep, source.Generator, ForceBackend.UniformGrid, 1,
            new ExperimentProvenance(completed.Time, completed.Step, "P2 completed 120-step derivative", source.Generator, source.Name)));
        P1Inputs.WriteJson(Path.Combine(outputDirectory, "inputs.json"), new
        {
            sourceFile = Path.GetFullPath(inputPath),
            sourceSha256 = P1Inputs.HashFile(inputPath),
            environment = LatencyRunner.CaptureEnvironment("P2 exact source derivative preparation", 4),
            inputs
        });

        void Save(ExperimentDefinition experiment)
        {
            string file = experiment.Name + ".json";
            string path = Path.Combine(outputDirectory, file);
            ExperimentFile.Save(path, experiment);
            inputs.Add(new { file, sha256 = P1Inputs.HashFile(path), experiment.Parameters, experiment.Provenance });
        }
    }

    internal static void Run(string outputPath, string inputPath, int samples)
    {
        if (samples is < 5 or > 100 || File.Exists(outputPath))
        {
            throw new ArgumentException("Use 5..100 samples and a fresh output file.");
        }

        ExperimentDefinition experiment = ExperimentFile.Load(inputPath);
        if (experiment.InitialState.Count > 10_000 || experiment.Parameters.Cutoff is < 1e-6 or > 1e6)
        {
            throw new ArgumentException("Candidate probes support at most 10000 particles and cutoff 1e-6..1e6.");
        }

        ParticleSnapshot state = experiment.InitialState;
        Vector2d[] reference = new Vector2d[state.Count];
        ForceEvaluationStatistics referenceCounts = new DirectForceEvaluator(experiment.Parameters, experiment.Matrix)
            .Evaluate(state.Observe(), reference);
        List<object> cases = [];
        foreach (string name in new[] { "packed-aos", "component-soa", "ordinary-reciprocal", "simd-filter", "float32", "sqrt-estimate-newton", "half-cells", "quarter-cells", "selective-stencil", "half-selective" })
        {
            Probe probe = new(experiment, name);
            Vector2d[] forces = new Vector2d[state.Count];
            int warmup = 0;
            long began = Stopwatch.GetTimestamp();
            do
            {
                probe.Build(state);
                probe.Evaluate(forces);
                warmup++;
            }
            while (warmup < 8 || Stopwatch.GetElapsedTime(began).TotalSeconds < 0.5);

            P2Runner.Series total = new(samples), build = new(samples), interaction = new(samples);
            for (int i = 0; i < samples; i++)
            {
                total.Measure(i, () => { probe.Build(state); probe.Evaluate(forces); });
                build.Measure(i, () => probe.Build(state));
                interaction.Measure(i, () => probe.Evaluate(forces));
            }

            double maximumAbsoluteError = 0, rootMeanSquare = 0;
            for (int i = 0; i < forces.Length; i++)
            {
                Vector2d difference = forces[i] - reference[i];
                maximumAbsoluteError = Math.Max(maximumAbsoluteError, Math.Max(Math.Abs(difference.X), Math.Abs(difference.Y)));
                rootMeanSquare += difference.X * difference.X + difference.Y * difference.Y;
            }

            cases.Add(new
            {
                name,
                warmup,
                cellWidth = probe.CellWidth,
                build = build.Summarize(),
                interaction = interaction.Summarize(),
                total = total.Summarize(),
                probe.Candidates,
                probe.Accepted,
                acceptedDifference = probe.Accepted - referenceCounts.AcceptedPairs,
                maximumAbsoluteForceComponentError = maximumAbsoluteError,
                rootMeanSquareForceComponentError = Math.Sqrt(rootMeanSquare / Math.Max(1, 2L * forces.Length))
            });
            Console.WriteLine($"P2 probe {name}: {total.Median:F4} ms, {probe.Candidates} candidates, {probe.Accepted} accepted.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        P1Inputs.WriteJson(outputPath, new
        {
            schemaVersion = 1,
            environment = LatencyRunner.CaptureEnvironment("P2 benchmark-only ordinary-range arithmetic/layout/stencil probes", 1),
            inputFile = Path.GetFullPath(inputPath),
            inputSha256 = P1Inputs.HashFile(inputPath),
            experiment.Name,
            population = state.Count,
            experiment.Parameters,
            referenceCounts,
            vectorLanes = Vector<double>.Count,
            vectorHardwareAccelerated = Vector.IsHardwareAccelerated,
            method = "Each named probe uses identical stable scatter, dictionary index and scalar receiver sum order except cell subdivision. Timed rebuild and prepared interaction are separate from total. Minimum 8 calls and 0.5 s warmup per probe. Probes run sequentially in listed order; repeat fresh processes when a close decision requires it. float32 and sqrt-estimate-newton are deliberately approximate benchmark candidates, not shipped modes. Direct force/count reference excludes timing.",
            cases
        });
    }

    private sealed class Probe
    {
        private readonly ModelParameters parameters;
        private readonly InteractionMatrix matrix;
        private readonly PairForceKernel kernel;
        private readonly string name;
        private readonly double inverseCutoff, inverseBeta, inverseOuter;
        private readonly int exponent;
        private readonly int reach;
        private readonly bool selective;
        private readonly Dictionary<(long X, long Y), int> cells = [];
        private readonly int[] cellIds, starts, ends, sources, species;
        private readonly (long X, long Y)[] coordinates;
        private readonly Vector2d[] positions;
        private readonly double[] xs, ys;
        private ParticleSnapshot state;

        internal Probe(ExperimentDefinition experiment, string name)
        {
            parameters = experiment.Parameters;
            inverseCutoff = 1 / parameters.Cutoff;
            inverseBeta = 1 / parameters.RepulsionFraction;
            inverseOuter = 1 / (1 - parameters.RepulsionFraction);
            matrix = experiment.Matrix;
            kernel = new(parameters, matrix);
            this.name = name;
            state = experiment.InitialState;
            int subdivision = name is "half-cells" or "half-selective" ? 1 : name == "quarter-cells" ? 2 : 0;
            int baseExponent = Math.ILogB(parameters.Cutoff);
            if (Math.ScaleB(1, baseExponent) < parameters.Cutoff) { baseExponent++; }
            exponent = baseExponent - subdivision;
            reach = 1 << subdivision;
            selective = name.Contains("selective", StringComparison.Ordinal);
            CellWidth = Math.ScaleB(1, exponent);
            int count = state.Count;
            cellIds = new int[count]; starts = new int[count]; ends = new int[count]; sources = new int[count]; species = new int[count];
            coordinates = new (long X, long Y)[count]; positions = new Vector2d[count]; xs = new double[count]; ys = new double[count];
            cells.EnsureCapacity(count);
        }

        internal double CellWidth { get; }
        internal long Candidates { get; private set; }
        internal long Accepted { get; private set; }

        internal void Build(ParticleSnapshot input)
        {
            state = input;
            cells.Clear();
            for (int i = 0; i < input.Count; i++)
            {
                Vector2d point = input.Positions[i];
                (long X, long Y) cell = ((long)Math.Floor(Math.ScaleB(point.X, -exponent)), (long)Math.Floor(Math.ScaleB(point.Y, -exponent)));
                coordinates[i] = cell;
                if (!cells.TryGetValue(cell, out int id))
                {
                    id = cells.Count;
                    cells.Add(cell, id);
                    ends[id] = 0;
                }
                cellIds[i] = id;
                ends[id]++;
            }
            int offset = 0;
            for (int id = 0; id < cells.Count; id++)
            {
                starts[id] = offset;
                offset += ends[id];
                ends[id] = starts[id];
            }
            for (int i = 0; i < input.Count; i++)
            {
                int slot = ends[cellIds[i]]++;
                sources[slot] = i;
                positions[slot] = input.Positions[i];
                xs[slot] = input.Positions[i].X; ys[slot] = input.Positions[i].Y;
                species[slot] = input.Species[i];
            }
        }

        internal void Evaluate(Vector2d[] forces)
        {
            long candidates = 0, accepted = 0;
            for (int receiver = 0; receiver < state.Count; receiver++)
            {
                int original = sources[receiver];
                (long X, long Y) center = coordinates[original];
                Vector2d point = positions[receiver];
                Vector2d total = default;
                int row = species[receiver] * matrix.SpeciesCount;
                for (int dy = -reach; dy <= reach; dy++)
                {
                    for (int dx = -reach; dx <= reach; dx++)
                    {
                        (long X, long Y) cell = (center.X + dx, center.Y + dy);
                        if (selective && Outside(cell, point)) { continue; }
                        if (!cells.TryGetValue(cell, out int id)) { continue; }
                        int end = ends[id], source = starts[id];
                        if (name == "simd-filter")
                        {
                            Vector<double> vx = new(point.X), vy = new(point.Y), square = new(parameters.Cutoff * parameters.Cutoff);
                            for (; source <= end - Vector<double>.Count; source += Vector<double>.Count)
                            {
                                Vector<double> x = new Vector<double>(xs, source) - vx;
                                Vector<double> y = new Vector<double>(ys, source) - vy;
                                Vector<long> mask = Vector.LessThan(x * x + y * y, square);
                                for (int lane = 0; lane < Vector<double>.Count; lane++)
                                {
                                    int slot = source + lane;
                                    if (slot == receiver) { continue; }
                                    candidates++;
                                    if (mask[lane] == 0) { continue; }
                                    Add(slot);
                                }
                            }
                        }
                        for (; source < end; source++)
                        {
                            if (source == receiver) { continue; }
                            candidates++;
                            Add(source);
                        }
                    }
                }
                forces[original] = total + SoftWall.EvaluateValidated(point, parameters);

                void Add(int source)
                {
                    Vector2d delta = name == "component-soa" ? new(xs[source] - point.X, ys[source] - point.Y) : positions[source] - point;
                    PairKind kind;
                    Vector2d force;
                    if (name is "ordinary-reciprocal" or "float32" or "sqrt-estimate-newton")
                    {
                        kind = Ordinary(delta, row + species[source], out force);
                    }
                    else
                    {
                        kind = kernel.Evaluate(delta, row + species[source], out force);
                    }
                    if (kind == PairKind.Accepted) { accepted++; total += force; }
                }
            }
            Candidates = candidates;
            Accepted = accepted;
        }

        // Conservative interval-distance test: one outward ULP on each lower bound avoids rejecting
        // a cell because its face subtraction rounded up. This probe supports ordinary coordinates.
        private bool Outside((long X, long Y) cell, Vector2d point)
        {
            double left = Math.ScaleB(cell.X, exponent), bottom = Math.ScaleB(cell.Y, exponent);
            double x = Math.Max(0, Math.Max(left - point.X, point.X - (left + CellWidth)));
            double y = Math.Max(0, Math.Max(bottom - point.Y, point.Y - (bottom + CellWidth)));
            x = Math.Max(0, Math.BitDecrement(x)); y = Math.Max(0, Math.BitDecrement(y));
            return Math.BitDecrement(x * x + y * y) >= Math.BitIncrement(parameters.Cutoff * parameters.Cutoff);
        }

        private PairKind Ordinary(Vector2d delta, int coefficient, out Vector2d force)
        {
            force = default;
            if (delta.X == 0 && delta.Y == 0) { return PairKind.Overlap; }
            double square = delta.X * delta.X + delta.Y * delta.Y;
            double cutoff = parameters.Cutoff;
            if (Math.Abs(delta.X) >= cutoff || Math.Abs(delta.Y) >= cutoff || square >= cutoff * cutoff) { return PairKind.Rejected; }
            double distance = Math.Sqrt(square);
            if (name == "float32")
            {
                float x = (float)delta.X, y = (float)delta.Y;
                float r = MathF.Sqrt(x * x + y * y), q = r / (float)cutoff, beta = (float)parameters.RepulsionFraction;
                float g = q < beta ? q / beta - 1 : (float)matrix.Values[coefficient] * (q <= (1 + beta) / 2 ? 2 * ((q - beta) / (1 - beta)) : 2 * ((1 - q) / (1 - beta)));
                float scale = (float)parameters.ForceScale * g / r;
                force = new(x * scale, y * scale);
                return PairKind.Accepted;
            }
            double inverse = 1 / distance;
            if (name == "sqrt-estimate-newton")
            {
                inverse = 1 / MathF.Sqrt((float)square);
                inverse *= 1.5 - 0.5 * square * inverse * inverse;
                distance = square * inverse;
            }
            if (distance >= cutoff) { return PairKind.Rejected; }
            double ratio = distance * inverseCutoff, beta64 = parameters.RepulsionFraction;
            double curve = ratio < beta64 ? ratio * inverseBeta - 1 : matrix.Values[coefficient] * (ratio <= (1 + beta64) / 2
                ? 2 * ((ratio - beta64) * inverseOuter) : 2 * ((1 - ratio) * inverseOuter));
            force = delta * (parameters.ForceScale * curve * inverse);
            return PairKind.Accepted;
        }
    }
}
