using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using SimulationDiagnostics = ParticleLife.Core.Experiments.Diagnostics;

namespace ParticleLife.Benchmarks;

/// <summary>Compares exact serial searches using saved inputs and separately reported timing boundaries.</summary>
/// <remarks>
/// Each complete step includes validation, index construction, interactions and integration. Initial-state
/// copying is outside static timings. Backend order alternates in each repetition and sample; numerical
/// stages and diagnostics run separately. Raw samples are retained, with no timing assertions or CI budget.
/// </remarks>
internal static class ComparisonRunner
{
    private const int MinimumWarmupSteps = 64;
    private const double MinimumWarmupSeconds = 1;
    private const int EvolvingSteps = 120;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    internal static void Run(string outputDirectory, int samples, string selection, int repetitions)
    {
        if (samples is < 5 or > 10_000 || repetitions is < 1 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(samples), "Use 5..10000 samples and 1..20 repetitions.");
        }

        Directory.CreateDirectory(outputDirectory);
        List<(ExperimentDefinition Experiment, string InputPath)> workloads = [];
        if (File.Exists(selection))
        {
            string inputPath = Path.Combine(outputDirectory, Path.GetFileName(selection));
            if (!string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(selection), StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(inputPath) && HashFile(inputPath) != HashFile(selection))
                {
                    throw new IOException("A different saved input already occupies the output path; use a fresh directory.");
                }

                if (!File.Exists(inputPath))
                {
                    File.Copy(selection, inputPath);
                }
            }

            workloads.Add((ExperimentFile.Load(inputPath), inputPath));
        }
        else
        {
            foreach (string caseName in BenchmarkWorkload.Select(selection))
            {
                string inputPath = Path.Combine(outputDirectory, caseName + ".experiment.json");
                // Replays use saved values, including historical schema-1 inputs; never regenerate over them.
                if (!File.Exists(inputPath))
                {
                    ExperimentFile.Save(inputPath, BenchmarkWorkload.Create(caseName));
                }

                workloads.Add((ExperimentFile.Load(inputPath), inputPath));
            }
        }

        object environment = LatencyRunner.CaptureEnvironment("direct and uniform-grid; serial double precision");
        List<object> results = [];
        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        foreach ((ExperimentDefinition experiment, string inputPath) in workloads)
        {
            Console.WriteLine($"Comparing {experiment.Name}: {MinimumWarmupSteps}+ warm-ups, {repetitions} x {samples} step samples.");
            object result = Measure(experiment, inputPath, samples, repetitions);
            results.Add(result);
            WriteReport(Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(inputPath) + ".comparison.json"), result);
            // Retain completed cases if a long session is interrupted.
            WriteReport(Path.Combine(outputDirectory, "comparison.json"), Report(null));
        }

        (ExperimentDefinition evolvingExperiment, string evolvingPath) = workloads.FirstOrDefault(
            item => item.Experiment.Name == "uniform-fixed-2000", workloads[0]);
        Console.WriteLine($"Evolving comparison: {evolvingExperiment.Name}, {repetitions} x {EvolvingSteps} consecutive steps.");
        object evolving = MeasureEvolving(evolvingExperiment, evolvingPath, repetitions);
        string reportPath = Path.Combine(outputDirectory, "comparison.json");
        WriteReport(reportPath, Report(evolving));
        Console.WriteLine($"Report: {Path.GetFullPath(reportPath)}");

        object Report(object? evolution) => new
        {
            schemaVersion = 1,
            startedAtUtc,
            updatedAtUtc = DateTimeOffset.UtcNow,
            environment,
            arguments = new { outputDirectory = Path.GetFullPath(outputDirectory), samples, selection, repetitions },
            minimumWarmupSteps = MinimumWarmupSteps,
            minimumWarmupSeconds = MinimumWarmupSeconds,
            method = "Static: reset outside each timed step. Alternate backend order per sample and repetition. Stages sampled separately; nearest-rank percentiles. Evolving: reset only between repetitions, then 120 consecutive timed steps.",
            stageBoundaries = "Grid build includes state validation. Grid interaction uses that immutable prepared input and checks output buffers. Direct interaction includes state validation. Integration includes its validation. No resets, diagnostics, snapshots, serialization or rendering in numerical timing.",
            numericalComparison = "Independent direct and grid enumeration on identical saved input; forces and completed next states. Local errors are maximum vector norms in reduced units. Evolving trajectories use short-horizon comparisons; longer chaotic divergence is not a local-force error estimate.",
            results,
            evolving = evolution
        };
    }

    private static object Measure(ExperimentDefinition experiment, string inputPath, int samples, int repetitions)
    {
        int stageSamples = Math.Min(samples, 15);
        BackendRun direct = new(experiment, ForceBackend.Direct, repetitions, samples, stageSamples);
        BackendRun grid = new(experiment, ForceBackend.UniformGrid, repetitions, samples, stageSamples);
        int warmupCount = Warmup(experiment.InitialState, direct, grid);
        for (int repetition = 0; repetition < repetitions; repetition++)
        {
            for (int sample = 0; sample < samples; sample++)
            {
                BackendRun first = (sample + repetition) % 2 == 0 ? direct : grid;
                BackendRun second = ReferenceEquals(first, direct) ? grid : direct;
                first.MeasureStep(experiment.InitialState, repetition, sample);
                second.MeasureStep(experiment.InitialState, repetition, sample);
            }

            for (int sample = 0; sample < stageSamples; sample++)
            {
                BackendRun first = (sample + repetition) % 2 == 0 ? grid : direct;
                BackendRun second = ReferenceEquals(first, direct) ? grid : direct;
                first.MeasureStages(experiment, repetition, sample);
                second.MeasureStages(experiment, repetition, sample);
            }
        }

        ValidatePairCounts(direct.Statistics, grid.Statistics);
        Difference forceDifference = Compare(direct.Forces, grid.Forces);
        StateDifference stateDifference = CompareStates(direct.Simulation.Observe(), grid.Simulation.Observe());
        StateDifference directStageDifference = CompareStageState(direct);
        StateDifference gridStageDifference = CompareStageState(grid);
        Series diagnostics = new(1, 5);
        _ = SimulationDiagnostics.Sample(experiment.InitialState.Observe(), experiment.Parameters.Cutoff);
        for (int sample = 0; sample < 5; sample++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            _ = SimulationDiagnostics.Sample(experiment.InitialState.Observe(), experiment.Parameters.Cutoff);
            long ticks = Stopwatch.GetTimestamp() - started;
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            diagnostics.Record(0, sample, ticks, bytes);
        }

        double speedup = direct.Steps.MedianMilliseconds / grid.Steps.MedianMilliseconds;
        Console.WriteLine(FormattableString.Invariant(
            $"  direct {direct.Steps.MedianMilliseconds:F4} ms; grid {grid.Steps.MedianMilliseconds:F4} ms; p50 ratio {speedup:F3}x; force error {forceDifference.MaximumAbsoluteVectorError:E3}"));
        return new
        {
            name = experiment.Name,
            particleCount = experiment.InitialState.Count,
            speciesCount = experiment.Matrix.SpeciesCount,
            experiment.Parameters,
            experiment.TimeStep,
            experiment.Generator,
            inputFile = Path.GetFileName(inputPath),
            inputSha256 = HashFile(inputPath),
            warmupCountPerBackend = warmupCount,
            direct = direct.Summarize(),
            grid = grid.Summarize(),
            directOverGridP50 = speedup,
            forceDifference,
            nextStateDifference = stateDifference,
            directSplitStageDifference = directStageDifference,
            gridSplitStageDifference = gridStageDifference,
            setupAndGrowth = new
            {
                method = "Cold instance after shared code warm-up; single observations, not latency percentiles. Snapshot creation and metadata inspection excluded. First steps include index capacity establishment. Reset growth begins at one quarter population.",
                direct = MeasureCapacity(experiment, ForceBackend.Direct),
                grid = MeasureCapacity(experiment, ForceBackend.UniformGrid)
            },
            diagnostics = diagnostics.Summarize(),
            diagnosticClusterDistance = experiment.Parameters.Cutoff
        };
    }

    /// <summary>Warms both complete call paths, establishing worst-case cell capacity before measurements.</summary>
    private static int Warmup(ParticleSnapshot initial, BackendRun direct, BackendRun grid)
    {
        int count = 0;
        long started = Stopwatch.GetTimestamp();
        do
        {
            direct.Simulation.Reset(initial);
            grid.Simulation.Reset(initial);
            if (count % 2 == 0)
            {
                direct.Simulation.Step();
                grid.Simulation.Step();
            }
            else
            {
                grid.Simulation.Step();
                direct.Simulation.Step();
            }

            count++;
        }
        while (count < MinimumWarmupSteps || Stopwatch.GetElapsedTime(started).TotalSeconds < MinimumWarmupSeconds);

        // These instances share the warmed code paths, but need their own reusable index capacity.
        direct.Evaluator.Evaluate(initial.Observe(), direct.Forces);
        grid.Evaluator.Evaluate(initial.Observe(), grid.Forces);
        return count;
    }

    private static object MeasureCapacity(ExperimentDefinition experiment, ForceBackend backend)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        Simulation fresh = experiment.CreateSimulation(backend);
        long constructionTicks = Stopwatch.GetTimestamp() - started;
        long constructionBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        started = Stopwatch.GetTimestamp();
        fresh.Step();
        long firstStepTicks = Stopwatch.GetTimestamp() - started;
        long firstStepBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        ParticleSnapshot initial = experiment.InitialState;
        int smallerCount = initial.Count / 4;
        ParticleSnapshot smaller = new(initial.Ids[..smallerCount], initial.Positions[..smallerCount],
            initial.Velocities[..smallerCount], initial.Species[..smallerCount]);
        Simulation growing = new(smaller, experiment.Parameters, experiment.Matrix, experiment.TimeStep, backend);
        growing.Step();
        ForceExecution? smallerExecution = growing.LastExecution;
        before = GC.GetAllocatedBytesForCurrentThread();
        started = Stopwatch.GetTimestamp();
        growing.Reset(initial);
        long resetTicks = Stopwatch.GetTimestamp() - started;
        long resetBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        started = Stopwatch.GetTimestamp();
        growing.Step();
        long grownStepTicks = Stopwatch.GetTimestamp() - started;
        long grownStepBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        return new
        {
            construction = new { milliseconds = Milliseconds(constructionTicks), allocatedBytes = constructionBytes },
            firstCompleteStep = new { milliseconds = Milliseconds(firstStepTicks), allocatedBytes = firstStepBytes },
            smallerParticleCount = smallerCount,
            beforeGrowth = smallerExecution,
            resetToFullPopulation = new { milliseconds = Milliseconds(resetTicks), allocatedBytes = resetBytes },
            firstCompleteStepAfterGrowth = new { milliseconds = Milliseconds(grownStepTicks), allocatedBytes = grownStepBytes },
            afterGrowth = growing.LastExecution
        };
    }

    private static object MeasureEvolving(ExperimentDefinition experiment, string inputPath, int repetitions)
    {
        BackendRun direct = new(experiment, ForceBackend.Direct, repetitions, EvolvingSteps, 1);
        BackendRun grid = new(experiment, ForceBackend.UniformGrid, repetitions, EvolvingSteps, 1);
        int warmupCount = Warmup(experiment.InitialState, direct, grid);
        long[][] movedCells = Enumerable.Range(0, repetitions).Select(_ => new long[EvolvingSteps]).ToArray();
        long[][] candidates = Enumerable.Range(0, repetitions).Select(_ => new long[EvolvingSteps]).ToArray();
        int[][] occupiedCells = Enumerable.Range(0, repetitions).Select(_ => new int[EvolvingSteps]).ToArray();
        double[] maximumPositionDifference = new double[repetitions];
        double[] maximumVelocityDifference = new double[repetitions];
        double cellWidth = grid.Simulation.LastExecution!.Value.Index.CellWidth;
        Vector2d[] previousPositions = new Vector2d[experiment.InitialState.Count];
        for (int repetition = 0; repetition < repetitions; repetition++)
        {
            direct.Simulation.Reset(experiment.InitialState);
            grid.Simulation.Reset(experiment.InitialState);
            for (int sample = 0; sample < EvolvingSteps; sample++)
            {
                grid.Simulation.Observe().Positions.CopyTo(previousPositions);
                BackendRun first = (sample + repetition) % 2 == 0 ? direct : grid;
                BackendRun second = ReferenceEquals(first, direct) ? grid : direct;
                first.MeasureEvolvingStep(repetition, sample);
                second.MeasureEvolvingStep(repetition, sample);
                ParticleStateView completed = grid.Simulation.Observe();
                for (int particle = 0; particle < completed.Count; particle++)
                {
                    Vector2d previous = previousPositions[particle];
                    Vector2d current = completed.Positions[particle];
                    if (Math.Floor(previous.X / cellWidth) != Math.Floor(current.X / cellWidth)
                        || Math.Floor(previous.Y / cellWidth) != Math.Floor(current.Y / cellWidth))
                    {
                        movedCells[repetition][sample]++;
                    }
                }

                candidates[repetition][sample] = grid.Simulation.LastStatistics.CandidatePairs;
                occupiedCells[repetition][sample] = grid.Simulation.LastExecution!.Value.Index.OccupiedCells;
                StateDifference difference = CompareStates(direct.Simulation.Observe(), completed);
                maximumPositionDifference[repetition] = Math.Max(maximumPositionDifference[repetition], difference.Positions.MaximumAbsoluteVectorError);
                maximumVelocityDifference[repetition] = Math.Max(maximumVelocityDifference[repetition], difference.Velocities.MaximumAbsoluteVectorError);
            }
        }

        return new
        {
            name = experiment.Name,
            inputFile = Path.GetFileName(inputPath),
            inputSha256 = HashFile(inputPath),
            stepsPerRepetition = EvolvingSteps,
            warmupCountPerBackend = warmupCount,
            directStep = direct.Steps.Summarize(),
            gridStep = grid.Steps.Summarize(),
            directOverGridP50 = direct.Steps.MedianMilliseconds / grid.Steps.MedianMilliseconds,
            movedCells,
            totalCellMigrations = movedCells.Sum(values => values.Sum()),
            gridCandidatePairs = candidates,
            gridOccupiedCells = occupiedCells,
            maximumPositionDifference,
            maximumVelocityDifference,
            finalGridExecution = grid.Simulation.LastExecution,
            directFallbackSteps = grid.Simulation.DirectFallbackSteps,
            accumulationFallbackSteps = grid.Simulation.AccumulationFallbackSteps
        };
    }

    private static void ValidatePairCounts(ForceEvaluationStatistics direct, ForceEvaluationStatistics grid)
    {
        if (direct.AcceptedPairs != grid.AcceptedPairs || direct.OverlapPairs != grid.OverlapPairs)
        {
            throw new InvalidOperationException("Direct/grid accepted or overlap counts differ; performance evidence is invalid.");
        }
    }

    private static Difference Compare(ReadOnlySpan<Vector2d> reference, ReadOnlySpan<Vector2d> actual)
    {
        double absolute = 0;
        double relative = 0;
        for (int i = 0; i < reference.Length; i++)
        {
            double error = (reference[i] - actual[i]).Length;
            double scale = Math.Max(reference[i].Length, actual[i].Length);
            absolute = Math.Max(absolute, error);
            relative = Math.Max(relative, scale == 0 ? 0 : error / scale);
        }

        return new Difference(absolute, relative);
    }

    private static StateDifference CompareStates(ParticleStateView reference, ParticleStateView actual)
    {
        if (reference.Step != actual.Step || reference.Time != actual.Time || !reference.Ids.SequenceEqual(actual.Ids))
        {
            throw new InvalidOperationException("Backend state identity or clock differs.");
        }

        return new StateDifference(Compare(reference.Positions, actual.Positions), Compare(reference.Velocities, actual.Velocities));
    }

    private static StateDifference CompareStageState(BackendRun run) => new(
        Compare(run.Simulation.Observe().Positions, run.OutputPositions),
        Compare(run.Simulation.Observe().Velocities, run.OutputVelocities));

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static double Milliseconds(long ticks) => 1000d * ticks / Stopwatch.Frequency;

    private static void WriteReport(string path, object report) => File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions) + "\n");

    private readonly record struct Difference(double MaximumAbsoluteVectorError, double MaximumRelativeVectorError);

    private readonly record struct StateDifference(Difference Positions, Difference Velocities);

    /// <summary>Owns work buffers and raw samples; timing records are written only after each measured boundary.</summary>
    private sealed class BackendRun
    {
        private readonly ForceBackend backend;
        private readonly Series index;
        private readonly Series interaction;
        private readonly Series integration;

        internal BackendRun(ExperimentDefinition experiment, ForceBackend backend, int repetitions, int samples, int stageSamples)
        {
            this.backend = backend;
            Simulation = experiment.CreateSimulation(backend);
            Evaluator = backend == ForceBackend.Direct
                ? new DirectForceEvaluator(experiment.Parameters, experiment.Matrix)
                : new UniformGridForceEvaluator(experiment.Parameters, experiment.Matrix);
            Forces = new Vector2d[experiment.InitialState.Count];
            OutputPositions = new Vector2d[experiment.InitialState.Count];
            OutputVelocities = new Vector2d[experiment.InitialState.Count];
            Steps = new Series(repetitions, samples);
            index = new Series(repetitions, stageSamples);
            interaction = new Series(repetitions, stageSamples);
            integration = new Series(repetitions, stageSamples);
        }

        internal Simulation Simulation { get; }
        internal IForceEvaluator Evaluator { get; }
        internal Vector2d[] Forces { get; }
        internal Vector2d[] OutputPositions { get; }
        internal Vector2d[] OutputVelocities { get; }
        internal Series Steps { get; }
        internal ForceEvaluationStatistics Statistics { get; private set; }

        internal void MeasureStep(ParticleSnapshot initial, int repetition, int sample)
        {
            Simulation.Reset(initial);
            MeasureEvolvingStep(repetition, sample);
        }

        internal void MeasureEvolvingStep(int repetition, int sample)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            Simulation.Step();
            long ticks = Stopwatch.GetTimestamp() - started;
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Steps.Record(repetition, sample, ticks, bytes);
        }

        internal void MeasureStages(ExperimentDefinition experiment, int repetition, int sample)
        {
            ParticleStateView input = experiment.InitialState.Observe();
            UniformGridForceEvaluator? grid = Evaluator as UniformGridForceEvaluator;
            if (grid is not null)
            {
                long beforeIndex = GC.GetAllocatedBytesForCurrentThread();
                long startedIndex = Stopwatch.GetTimestamp();
                grid.BuildIndex(input);
                long indexTicks = Stopwatch.GetTimestamp() - startedIndex;
                long indexBytes = GC.GetAllocatedBytesForCurrentThread() - beforeIndex;
                index.Record(repetition, sample, indexTicks, indexBytes);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            Statistics = grid is null ? Evaluator.Evaluate(input, Forces) : grid.EvaluatePrepared(input, Forces);
            long ticks = Stopwatch.GetTimestamp() - started;
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            interaction.Record(repetition, sample, ticks, bytes);

            double damping = SemiImplicitEuler.DampingFactor(experiment.TimeStep, experiment.Parameters.DampingHalfLife);
            before = GC.GetAllocatedBytesForCurrentThread();
            started = Stopwatch.GetTimestamp();
            SemiImplicitEuler.Integrate(input, Forces, experiment.TimeStep, damping, OutputPositions, OutputVelocities);
            ticks = Stopwatch.GetTimestamp() - started;
            bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            integration.Record(repetition, sample, ticks, bytes);
        }

        internal object Summarize() => new
        {
            requestedBackend = backend,
            lastExecution = Simulation.LastExecution,
            step = Steps.Summarize(),
            indexConstruction = backend == ForceBackend.UniformGrid ? index.Summarize() : null,
            interactionIncludingWalls = interaction.Summarize(),
            integration = integration.Summarize(),
            candidateDirectedPairs = Statistics.CandidatePairs,
            acceptedDirectedPairs = Statistics.AcceptedPairs,
            coincidentUnorderedPairs = Statistics.OverlapPairs
        };
    }

    private sealed class Series
    {
        private readonly long[][] ticks;
        private readonly long[][] bytes;

        internal Series(int repetitions, int samples)
        {
            ticks = Enumerable.Range(0, repetitions).Select(_ => new long[samples]).ToArray();
            bytes = Enumerable.Range(0, repetitions).Select(_ => new long[samples]).ToArray();
        }

        internal double MedianMilliseconds => Percentile(ticks.SelectMany(row => row).Select(Milliseconds).ToArray(), 0.5);

        internal void Record(int repetition, int sample, long elapsedTicks, long allocatedBytes)
        {
            ticks[repetition][sample] = elapsedTicks;
            bytes[repetition][sample] = allocatedBytes;
        }

        internal object Summarize()
        {
            double[] milliseconds = ticks.SelectMany(row => row).Select(Milliseconds).ToArray();
            double mean = milliseconds.Average();
            return new
            {
                samples = milliseconds.Length,
                p50Milliseconds = Percentile(milliseconds, 0.5),
                p95Milliseconds = Percentile(milliseconds, 0.95),
                meanMilliseconds = mean,
                standardDeviationMilliseconds = milliseconds.Length > 1
                    ? Math.Sqrt(milliseconds.Sum(value => (value - mean) * (value - mean)) / (milliseconds.Length - 1)) : 0,
                minimumMilliseconds = milliseconds.Min(),
                maximumMilliseconds = milliseconds.Max(),
                allocatedBytes = bytes.Sum(row => row.Sum()),
                maximumAllocatedBytesPerSample = bytes.Max(row => row.Max()),
                repetitions = ticks.Select((row, repetition) => new
                {
                    p50Milliseconds = Percentile(row.Select(Milliseconds).ToArray(), 0.5),
                    p95Milliseconds = Percentile(row.Select(Milliseconds).ToArray(), 0.95),
                    milliseconds = row.Select(Milliseconds).ToArray(),
                    allocatedBytes = bytes[repetition]
                }).ToArray()
            };
        }

        private static double Percentile(double[] values, double fraction)
        {
            double[] sorted = (double[])values.Clone();
            Array.Sort(sorted);
            return sorted[(int)Math.Ceiling(fraction * sorted.Length) - 1];
        }
    }
}
