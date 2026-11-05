using System.Diagnostics;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using ParticleLife.Viewer;

namespace ParticleLife.Benchmarks;

/// <summary>Screens receiver-row range pruning on bounded saved nonperiodic inputs; these probes are not execution modes.</summary>
internal static class P4RangeProfile
{
    internal static void PrepareProductionComparison(string sourceDirectory, string directory)
    {
        Directory.CreateDirectory(directory);
        if (Directory.EnumerateFileSystemEntries(directory).Any()) { throw new IOException("Use a fresh production-comparison input directory."); }
        List<object> inputs = [];
        foreach ((string name, string source, double? cutoff, double scale, BoundaryKind boundary, bool global) in new[]
        {
            ("uniform-heterogeneous", "uniform-heterogeneous", (double?)null, 1d, BoundaryKind.Open, false),
            ("clustered-heterogeneous", "clustered-heterogeneous", (double?)null, 1d, BoundaryKind.Open, false),
            ("sparse-uniform-heterogeneous", "uniform-heterogeneous", (double?)null, 8d, BoundaryKind.Open, false),
            ("sparse-clustered-heterogeneous", "clustered-heterogeneous", (double?)null, 8d, BoundaryKind.Open, false),
            ("sparse-mild-heterogeneous", "uniform-equal", (double?)null, 8d, BoundaryKind.Open, false),
            ("cutoff-below", "uniform-heterogeneous", (double?)Math.BitDecrement(0.125), 1d, BoundaryKind.Open, false),
            ("cutoff-at", "uniform-heterogeneous", (double?)0.125, 1d, BoundaryKind.Open, false),
            ("cutoff-above", "uniform-heterogeneous", (double?)Math.BitIncrement(0.125), 1d, BoundaryKind.Open, false),
            ("global-control", "uniform-equal", (double?)null, 1d, BoundaryKind.Open, true),
            ("equal-pair-control", "uniform-equal", (double?)null, 1d, BoundaryKind.Open, false),
            ("periodic-control", "uniform-heterogeneous", (double?)null, 1d, BoundaryKind.Periodic, false)
        })
        {
            string sourcePath = Path.Combine(sourceDirectory, source + ".json");
            ExperimentDefinition original = ExperimentFile.Load(sourcePath);
            double maximum = cutoff ?? original.Matrix.MaximumCutoff(original.Parameters);
            double ratio = maximum / original.Matrix.MaximumCutoff(original.Parameters);
            PairInteractionRanges oldRanges = original.Matrix.Ranges!;
            PairInteractionRanges? ranges = global ? null : new(oldRanges.SpeciesCount,
                oldRanges.CoreRadii.ToArray().Select(value => value * ratio).ToArray(), oldRanges.CutoffRadii.ToArray().Select(value => value * ratio).ToArray());
            if (name == "sparse-mild-heterogeneous")
            {
                double[] mildCutoffs = new double[36], mildCores = new double[36];
                for (int cell = 0; cell < mildCutoffs.Length; cell++)
                {
                    mildCutoffs[cell] = cell / 6 == 0 ? maximum : maximum * 0.95;
                    mildCores[cell] = mildCutoffs[cell] * 0.3;
                }
                ranges = new(6, mildCores, mildCutoffs);
            }
            ParticleSnapshot state = original.InitialState;
            ExperimentDefinition derived = new(name, original.Parameters with
            {
                Boundary = boundary,
                Cutoff = maximum,
                WorldWidth = original.Parameters.WorldWidth * scale,
                WorldHeight = original.Parameters.WorldHeight * scale
            },
                original.Matrix.WithRanges(ranges), new ParticleSnapshot(state.Ids,
                    state.Positions.ToArray().Select(point => point * scale).ToArray(), state.Velocities, state.Species),
                original.TimeStep, original.Generator, ForceBackend.UniformGrid, 1,
                new ExperimentProvenance(0, 0, "P4 matched production comparison derivative", original.Generator, original.Name));
            string path = Path.Combine(directory, name + ".json"); ExperimentFile.Save(path, derived);
            inputs.Add(new
            {
                name,
                path,
                sha256 = P1Inputs.HashFile(path),
                sourcePath,
                sourceSha256 = P1Inputs.HashFile(sourcePath),
                population = state.Count,
                maximumCutoff = maximum,
                worldScale = scale,
                boundary,
                global
            });
        }
        P1Inputs.WriteJson(Path.Combine(directory, "inputs.json"), new { environment = LatencyRunner.CaptureEnvironment("P4 immutable production comparison preparation"), inputs });
    }

    internal static void Run(string directory, int samples)
    {
        if (samples is < 5 or > 30) { throw new ArgumentOutOfRangeException(nameof(samples)); }
        Directory.CreateDirectory(directory);
        if (Directory.EnumerateFileSystemEntries(directory).Any()) { throw new IOException("Use a fresh P4 range-profile directory."); }
        List<object> cases = [];
        foreach (bool clustered in new[] { false, true })
        {
            foreach (bool heterogeneous in new[] { false, true })
            {
                string name = (clustered ? "clustered" : "uniform") + (heterogeneous ? "-heterogeneous" : "-equal");
                ExperimentDefinition source = PresetGenerator.Create(PresetKind.Random, 4000, 6, 413, backend: ForceBackend.UniformGrid);
                double[] cutoffs = new double[36], cores = new double[36];
                for (int row = 0; row < 6; row++)
                {
                    for (int column = 0; column < 6; column++)
                    {
                        int cell = row * 6 + column;
                        cutoffs[cell] = heterogeneous && row > 0 ? 0.02 : 0.2;
                        cores[cell] = cutoffs[cell] * 0.3;
                    }
                }
                using PlaybackController playback = new(source, diagnosticEverySteps: 0);
                ExperimentEditor editor = new(playback);
                editor.ApplyConfiguration(new(source.Parameters with { Boundary = BoundaryKind.Open, Cutoff = 0.2 },
                    source.Matrix.WithRanges(new(6, cores, cutoffs)), source.TimeStep, ForceBackend.UniformGrid));
                if (clustered)
                {
                    editor.SetGeneration(editor.Generation with { Placement = PlacementKind.Cluster });
                    editor.RegeneratePositions();
                }
                ExperimentDefinition experiment = editor.CaptureCurrentExperiment(name);
                string path = Path.Combine(directory, name + ".json");
                ExperimentFile.Save(path, experiment);
                ParticleSnapshot state = experiment.InitialState;
                Vector2d[] reference = new Vector2d[state.Count], output = new Vector2d[state.Count];
                ForceEvaluationStatistics oracle = new DirectForceEvaluator(experiment.Parameters, experiment.Matrix).Evaluate(state.Observe(), reference);
                List<object> probes = [];
                using (UniformGridForceEvaluator production = new(experiment.Parameters, experiment.Matrix))
                {
                    ForceEvaluationStatistics productionStatistics = default;
                    Action evaluate = () => productionStatistics = production.Evaluate(state.Observe(), output);
                    evaluate();
                    probes.Add(Measure("production-max-cutoff", evaluate, () => productionStatistics, output));
                }
                foreach ((string probe, int subdivision, bool prune) in new[] { ("coarse-packed-control", 0, false), ("coarse-row-stencil", 0, true), ("quarter-row-stencil", 2, true) })
                {
                    Probe candidate = new(experiment, subdivision, prune);
                    Action evaluate = () => candidate.Evaluate(output);
                    evaluate();
                    probes.Add(Measure(probe, evaluate, () => candidate.Statistics, output));
                }
                cases.Add(new
                {
                    name,
                    input = path,
                    sha256 = P1Inputs.HashFile(path),
                    particles = state.Count,
                    species = 6,
                    rowCutoffs = Enumerable.Range(0, 6).Select(row => cutoffs[row * 6]).ToArray(),
                    probes
                });

                object Measure(string probe, Action evaluate, Func<ForceEvaluationStatistics> statistics, Vector2d[] forces)
                {
                    long began = Stopwatch.GetTimestamp(); int warmup = 0;
                    do { evaluate(); warmup++; } while (warmup < 8 || Stopwatch.GetElapsedTime(began).TotalSeconds < 0.3);
                    if (statistics().AcceptedPairs != oracle.AcceptedPairs || statistics().OverlapPairs != oracle.OverlapPairs)
                    {
                        throw new InvalidOperationException(probe + " lost accepted directed or overlap pairs.");
                    }
                    double maximumError = 0;
                    for (int i = 0; i < forces.Length; i++)
                    {
                        double error = Math.Max(Math.Abs(forces[i].X - reference[i].X), Math.Abs(forces[i].Y - reference[i].Y));
                        double tolerance = 1e-10 + 1e-10 * Math.Max(Math.Abs(reference[i].X), Math.Abs(reference[i].Y));
                        if (error > tolerance) { throw new InvalidOperationException(probe + " failed the direct force comparison."); }
                        maximumError = Math.Max(maximumError, error);
                    }
                    P2Runner.Series series = new(samples);
                    for (int sample = 0; sample < samples; sample++) { series.Measure(sample, evaluate); }
                    return new { probe, warmup, completeForceEvaluation = series.Summarize(), statistics = statistics(), maximumAbsoluteError = maximumError };
                }
            }
        }
        P1Inputs.WriteJson(Path.Combine(directory, "report.json"), new
        {
            environment = LatencyRunner.CaptureEnvironment("P4 static force evaluation including validation, index rebuild and walls; CPU one worker"),
            scope = "4000 particles; saved ordinary-range nonperiodic uniform/clustered inputs; equal rows or one long-range receiver row and five short rows; not a production backend",
            cases
        });
    }

    private sealed class Probe
    {
        private readonly ExperimentDefinition experiment;
        private readonly PairForceKernel kernel;
        private readonly Dictionary<(long X, long Y), int> cells = [];
        private readonly int[] cellIds, starts, ends, indices, species;
        private readonly Vector2d[] positions;
        private readonly (long X, long Y)[] coordinates;
        private readonly double[] rowRadii;
        private readonly double width;
        private readonly int exponent;
        private readonly bool prune;
        internal ForceEvaluationStatistics Statistics { get; private set; }
        internal Probe(ExperimentDefinition experiment, int subdivision, bool prune)
        {
            this.experiment = experiment; this.prune = prune;
            kernel = new(experiment.Parameters, experiment.Matrix);
            double maximum = experiment.Matrix.MaximumCutoff(experiment.Parameters);
            int baseExponent = Math.ILogB(maximum);
            if (Math.ScaleB(1, baseExponent) < maximum) { baseExponent++; }
            exponent = baseExponent - subdivision; width = Math.ScaleB(1, exponent);
            int count = experiment.InitialState.Count;
            cellIds = new int[count]; starts = new int[count]; ends = new int[count]; indices = new int[count]; species = new int[count];
            positions = new Vector2d[count]; coordinates = new (long, long)[count]; cells.EnsureCapacity(count);
            rowRadii = new double[experiment.Matrix.SpeciesCount];
            for (int row = 0; row < rowRadii.Length; row++)
            {
                for (int column = 0; column < rowRadii.Length; column++) { rowRadii[row] = Math.Max(rowRadii[row], experiment.Matrix.CutoffRadius(row, column, experiment.Parameters)); }
            }
        }
        internal void Evaluate(Vector2d[] forces)
        {
            ParticleStateView state = experiment.InitialState.Observe();
            DirectForceEvaluator.ValidateState(state, experiment.Matrix.SpeciesCount);
            cells.Clear();
            for (int i = 0; i < state.Count; i++)
            {
                var key = ((long)Math.Floor(Math.ScaleB(state.Positions[i].X, -exponent)), (long)Math.Floor(Math.ScaleB(state.Positions[i].Y, -exponent)));
                coordinates[i] = key;
                if (!cells.TryGetValue(key, out int id)) { id = cells.Count; cells.Add(key, id); ends[id] = 0; }
                cellIds[i] = id; ends[id]++;
            }
            int offset = 0;
            for (int id = 0; id < cells.Count; id++) { starts[id] = offset; offset += ends[id]; ends[id] = starts[id]; }
            for (int i = 0; i < state.Count; i++)
            {
                int slot = ends[cellIds[i]]++; indices[slot] = i; positions[slot] = state.Positions[i]; species[slot] = state.Species[i];
            }
            long candidates = 0, accepted = 0, overlaps = 0;
            for (int receiver = 0; receiver < state.Count; receiver++)
            {
                Vector2d point = state.Positions[receiver], total = default;
                int row = state.Species[receiver], coefficientOffset = row * rowRadii.Length;
                double radius = prune ? rowRadii[row] : experiment.Matrix.MaximumCutoff(experiment.Parameters);
                int reach = (int)Math.Ceiling(radius / width);
                var centre = coordinates[receiver];
                for (int dy = -reach; dy <= reach; dy++)
                {
                    for (int dx = -reach; dx <= reach; dx++)
                    {
                        var key = (X: centre.X + dx, Y: centre.Y + dy);
                        if (prune && Outside(key, point, radius)) { continue; }
                        if (!cells.TryGetValue(key, out int id)) { continue; }
                        for (int slot = starts[id]; slot < ends[id]; slot++)
                        {
                            int source = indices[slot]; if (source == receiver) { continue; }
                            candidates++;
                            PairKind kind = kernel.Evaluate(positions[slot] - point, coefficientOffset + species[slot], out Vector2d force);
                            if (kind == PairKind.Accepted) { accepted++; total += force; }
                            else if (kind == PairKind.Overlap && source > receiver) { overlaps++; }
                        }
                    }
                }
                forces[receiver] = total + SoftWall.EvaluateValidated(point, experiment.Parameters);
                if (!forces[receiver].IsFinite) { throw new ArithmeticException("Non-finite profile force."); }
            }
            Statistics = new(candidates, accepted, overlaps);
        }
        private bool Outside((long X, long Y) cell, Vector2d point, double radius)
        {
            double left = Math.ScaleB(cell.X, exponent), top = Math.ScaleB(cell.Y, exponent);
            double x = Math.Max(0, Math.Max(left - point.X, point.X - (left + width)));
            double y = Math.Max(0, Math.Max(top - point.Y, point.Y - (top + width)));
            x = Math.Max(0, Math.BitDecrement(x)); y = Math.Max(0, Math.BitDecrement(y));
            return Math.BitDecrement(x * x + y * y) >= Math.BitIncrement(radius * radius);
        }
    }
}
