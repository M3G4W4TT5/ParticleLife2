using System.Diagnostics;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;
using SimulationDiagnostics = ParticleLife.Core.Experiments.Diagnostics;

namespace ParticleLife.Benchmarks;

/// <summary>Measures serial and bounded parallel CPU execution on previously saved complete inputs.</summary>
/// <remarks>
/// A command invocation is one independent process. Repetitions within it are explicitly labelled
/// and do not estimate independent-process uncertainty. All allocations use process-wide precise
/// counters, including background workers; counters, resets and reporting remain outside timing.
/// Diagnostics and stage probes are separate from complete-step samples. Run without competing work.
/// </remarks>
internal static class M41Runner
{
    private const int MinimumWarmupSteps = 8;
    private const double MinimumWarmupSeconds = 0.5;

    internal static void Run(string outputDirectory, string inputDirectory, string selection,
        int samples, int repetitions, int evolvingSteps)
    {
        if (samples is < 5 or > 10_000 || repetitions is < 1 or > 20 || evolvingSteps is < 0 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(samples), "Use 5..10000 samples, 1..20 repetitions and 0..100000 evolving steps.");
        }

        string[] paths = M41Inputs.Select(inputDirectory, selection).ToArray();
        if (paths.Length == 0)
        {
            throw new ArgumentException("The selection contains no saved inputs.", nameof(selection));
        }

        Directory.CreateDirectory(outputDirectory);
        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        object environment = LatencyRunner.CaptureEnvironment("direct and uniform-grid; explicit workers 1,2,4,8; double precision", null);
        List<object> results = [];
        foreach (string inputPath in paths)
        {
            ExperimentDefinition experiment = ExperimentFile.Load(inputPath);
            Console.WriteLine($"M4.1 {experiment.Name}: {repetitions} x {samples}, workers 1/2/4/8.");
            object result = Measure(experiment, inputPath, samples, repetitions, evolvingSteps);
            results.Add(result);
            M41Inputs.WriteJson(Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(inputPath) + ".comparison.json"), result);
            M41Inputs.WriteJson(Path.Combine(outputDirectory, "comparison.json"), new
            {
                schemaVersion = 2,
                startedAtUtc,
                updatedAtUtc = DateTimeOffset.UtcNow,
                processId = Environment.ProcessId,
                environment,
                modelVersion = ExperimentFile.ModelVersion,
                integratorVersion = ExperimentFile.IntegratorVersion,
                gridAlgorithmVersion = UniformGridForceEvaluator.AlgorithmVersion,
                arguments = new { inputDirectory = Path.GetFullPath(inputDirectory), selection, samples, repetitions, evolvingSteps },
                minimumWarmupSteps = MinimumWarmupSteps,
                minimumWarmupSecondsPerBackend = MinimumWarmupSeconds,
                method = "Static input reset outside each complete-step measurement; backend order rotates by sample and repetition. Separate stage and diagnostics samples. Nearest-rank percentiles, no trimming. Repetitions share one process; rerun the command in fresh output directories for independent processes.",
                allocationMethod = "GC.GetTotalAllocatedBytes(precise:true) around each boundary includes all workers and unrelated process allocation. No counter calls are timed. Zero is an observed process-wide result, not a calling-thread-only inference.",
                stageBoundaries = "Build includes validation, indexing and packed input copy; interaction reads the exact prepared state; integration validates outputs. Each parallel evaluator has sleeping persistent workers; only the measured evaluator is dispatched. Stage medians need not add to complete-step median.",
                evolutionInterpretation = "Each backend evolves independently from identical saved values; differences over long trajectories can amplify chaotic roundoff and are not local-force errors. Static force/next-step comparisons remain separate.",
                results
            });
        }

        Console.WriteLine($"Report: {Path.GetFullPath(Path.Combine(outputDirectory, "comparison.json"))}");
    }

    private static object Measure(ExperimentDefinition experiment, string inputPath, int samples, int repetitions, int evolvingSteps)
    {
        int stageSamples = Math.Min(samples, 15);
        BackendRun[] runs =
        [
            new(experiment, ForceBackend.Direct, 1, repetitions, samples, stageSamples),
            new(experiment, ForceBackend.UniformGrid, 1, repetitions, samples, stageSamples),
            new(experiment, ForceBackend.UniformGrid, 2, repetitions, samples, stageSamples),
            new(experiment, ForceBackend.UniformGrid, 4, repetitions, samples, stageSamples),
            new(experiment, ForceBackend.UniformGrid, 8, repetitions, samples, stageSamples)
        ];
        try
        {
            foreach (BackendRun run in runs)
            {
                run.Warmup(experiment);
            }

            for (int repetition = 0; repetition < repetitions; repetition++)
            {
                for (int sample = 0; sample < samples; sample++)
                {
                    for (int offset = 0; offset < runs.Length; offset++)
                    {
                        BackendRun run = runs[(offset + sample + repetition) % runs.Length];
                        run.Simulation.Reset(experiment.InitialState);
                        run.MeasureStep(run.Steps, repetition * samples + sample);
                    }
                }

                for (int sample = 0; sample < stageSamples; sample++)
                {
                    for (int offset = 0; offset < runs.Length; offset++)
                    {
                        runs[(offset + sample + repetition) % runs.Length]
                            .MeasureStages(experiment, repetition * stageSamples + sample);
                    }
                }
            }

            List<object> backends = [];
            BackendRun reference = runs[0];
            foreach (BackendRun run in runs)
            {
                if (run.Statistics.AcceptedPairs != reference.Statistics.AcceptedPairs
                    || run.Statistics.OverlapPairs != reference.Statistics.OverlapPairs)
                {
                    throw new InvalidOperationException("Accepted/overlap parity failed; timing evidence is invalid.");
                }

                object forceError = Difference(reference.Forces, run.Forces);
                object stateError = StateDifference(reference.Simulation.Observe(), run.Simulation.Observe());
                backends.Add(run.Summarize(forceError, stateError));
                Console.WriteLine(FormattableString.Invariant($"  {run.Name,-10} step {run.Steps.Median:F4} ms; all-thread bytes {run.Steps.TotalBytes}."));
            }

            object diagnostics = MeasureDiagnostics(experiment, samples, repetitions);
            object profile = TraversalProfile.Measure(experiment, stageSamples, repetitions);
            object? evolving = evolvingSteps == 0 ? null : MeasureEvolving(experiment, runs, evolvingSteps, repetitions);
            return new
            {
                experiment.Name,
                inputFile = Path.GetFullPath(inputPath),
                inputSha256 = M41Inputs.HashFile(inputPath),
                particleCount = experiment.InitialState.Count,
                speciesCount = experiment.Matrix.SpeciesCount,
                experiment.Parameters,
                experiment.TimeStep,
                experiment.Generator,
                backends,
                traversalProfile = profile,
                diagnostics,
                evolving,
                setupAndGrowth = MeasureCapacity(experiment)
            };
        }
        finally
        {
            foreach (BackendRun run in runs)
            {
                run.Dispose();
            }
        }
    }

    private static object MeasureDiagnostics(ExperimentDefinition experiment, int samples, int repetitions)
    {
        DiagnosticSampler sampler = new();
        int count = Math.Min(samples, 15);
        Series reference = new(repetitions, count);
        Series spatial = new(repetitions, count);
        ParticleStateView input = experiment.InitialState.Observe();
        double radius = experiment.Parameters.Cutoff;
        DiagnosticSample expected = SimulationDiagnostics.Sample(input, radius);
        DiagnosticSample actual = sampler.Sample(input, radius);
        if (expected != actual)
        {
            throw new InvalidOperationException("Diagnostic parity failed.");
        }

        int referenceWarmupCount = 0;
        long warmupStart = Stopwatch.GetTimestamp();
        do
        {
            _ = SimulationDiagnostics.Sample(input, radius);
            referenceWarmupCount++;
        }
        while (referenceWarmupCount < MinimumWarmupSteps || Stopwatch.GetElapsedTime(warmupStart).TotalSeconds < MinimumWarmupSeconds);

        int spatialWarmupCount = 0;
        warmupStart = Stopwatch.GetTimestamp();
        do
        {
            _ = sampler.Sample(input, radius);
            spatialWarmupCount++;
        }
        while (spatialWarmupCount < MinimumWarmupSteps || Stopwatch.GetElapsedTime(warmupStart).TotalSeconds < MinimumWarmupSeconds);

        for (int repetition = 0; repetition < repetitions; repetition++)
        {
            for (int sample = 0; sample < count; sample++)
            {
                for (int offset = 0; offset < 2; offset++)
                {
                    bool useSpatial = (offset + sample + repetition) % 2 == 0;
                    long before = GC.GetTotalAllocatedBytes(true);
                    long start = Stopwatch.GetTimestamp();
                    DiagnosticSample result = useSpatial ? sampler.Sample(input, radius) : SimulationDiagnostics.Sample(input, radius);
                    long ticks = Stopwatch.GetTimestamp() - start;
                    long bytes = GC.GetTotalAllocatedBytes(true) - before;
                    (useSpatial ? spatial : reference).Record(repetition * count + sample, ticks, bytes);
                    if (result != expected)
                    {
                        throw new InvalidOperationException("Repeated diagnostic parity failed.");
                    }
                }
            }
        }

        return new
        {
            radius,
            sampledState = "Saved completed input; force-preparation index is never reused.",
            result = actual,
            referenceWarmupCount,
            spatialWarmupCount,
            algorithmVersion = DiagnosticSampler.AlgorithmVersion,
            lastExecution = sampler.LastExecution,
            reference = reference.Summarize(),
            spatial = spatial.Summarize()
        };
    }

    private static object MeasureEvolving(ExperimentDefinition experiment, BackendRun[] runs, int steps, int repetitions)
    {
        List<object> results = [];
        ParticleSnapshot[] referenceFinal = new ParticleSnapshot[repetitions];
        foreach (BackendRun run in runs)
        {
            Series series = new(repetitions, steps);
            List<object> endpoints = [];
            long minimumCandidates = long.MaxValue;
            long maximumCandidates = 0;
            int minimumCells = int.MaxValue;
            int maximumCells = 0;
            long fallbackSteps = 0;
            for (int repetition = 0; repetition < repetitions; repetition++)
            {
                run.Simulation.Reset(experiment.InitialState);
                for (int sample = 0; sample < steps; sample++)
                {
                    run.MeasureStep(series, repetition * steps + sample);
                    minimumCandidates = Math.Min(minimumCandidates, run.Simulation.LastStatistics.CandidatePairs);
                    maximumCandidates = Math.Max(maximumCandidates, run.Simulation.LastStatistics.CandidatePairs);
                    int cells = run.Simulation.LastExecution!.Value.Index.OccupiedCells;
                    minimumCells = Math.Min(minimumCells, cells);
                    maximumCells = Math.Max(maximumCells, cells);
                }

                fallbackSteps += run.Simulation.DirectFallbackSteps;
                ParticleSnapshot final = run.Simulation.CaptureSnapshot();
                if (run.Simulation.Backend == ForceBackend.Direct)
                {
                    referenceFinal[repetition] = final;
                }

                endpoints.Add(new
                {
                    final.Time,
                    final.Step,
                    differenceFromIndependentDirectTrajectory = StateDifference(referenceFinal[repetition].Observe(), final.Observe()),
                    lastExecution = run.Simulation.LastExecution
                });
            }

            results.Add(new
            {
                run.Name,
                steps = series.Summarize(),
                minimumCandidates,
                maximumCandidates,
                minimumOccupiedCells = minimumCells,
                maximumOccupiedCells = maximumCells,
                totalDirectFallbackSteps = fallbackSteps,
                endpoints
            });
        }

        return new { stepsPerRepetition = steps, repetitions, nominalElapsedTime = steps * experiment.TimeStep, results };
    }

    private static object MeasureCapacity(ExperimentDefinition experiment)
    {
        List<object> results = [];
        ParticleSnapshot initial = experiment.InitialState;
        int quarter = initial.Count / 4;
        ParticleSnapshot smaller = new(initial.Ids[..quarter], initial.Positions[..quarter], initial.Velocities[..quarter], initial.Species[..quarter]);
        foreach (int workers in new[] { 1, 2, 4, 8 })
        {
            long before = GC.GetTotalAllocatedBytes(true);
            long start = Stopwatch.GetTimestamp();
            using Simulation simulation = new(smaller, experiment.Parameters, experiment.Matrix, experiment.TimeStep, ForceBackend.UniformGrid, workers);
            long constructionTicks = Stopwatch.GetTimestamp() - start;
            long constructionBytes = GC.GetTotalAllocatedBytes(true) - before;
            simulation.Step();
            ForceExecution? beforeGrowth = simulation.LastExecution;
            before = GC.GetTotalAllocatedBytes(true);
            start = Stopwatch.GetTimestamp();
            simulation.Reset(initial);
            simulation.Step();
            long growthTicks = Stopwatch.GetTimestamp() - start;
            long growthBytes = GC.GetTotalAllocatedBytes(true) - before;
            ForceExecution? afterGrowth = simulation.LastExecution;
            simulation.Reset(smaller);
            simulation.Step();
            results.Add(new
            {
                workers,
                construction = new { milliseconds = Milliseconds(constructionTicks), allocatedBytes = constructionBytes },
                beforeGrowth,
                resetAndFirstStepAfterGrowth = new { milliseconds = Milliseconds(growthTicks), allocatedBytes = growthBytes },
                afterGrowth,
                afterShrink = simulation.LastExecution
            });
        }

        return new { method = "Single observations after shared code warmup, not percentiles; quarter population grows to full then shrinks. Process allocations include persistent worker startup.", results };
    }

    private static object Difference(ReadOnlySpan<Vector2d> reference, ReadOnlySpan<Vector2d> actual)
    {
        double absolute = 0;
        double relative = 0;
        for (int i = 0; i < reference.Length; i++)
        {
            double error = double.Hypot(reference[i].X - actual[i].X, reference[i].Y - actual[i].Y);
            double scale = Math.Max(double.Hypot(reference[i].X, reference[i].Y), double.Hypot(actual[i].X, actual[i].Y));
            absolute = Math.Max(absolute, error);
            relative = Math.Max(relative, scale == 0 ? 0 : error / scale);
        }

        return new { maximumAbsoluteVectorError = absolute, maximumRelativeVectorError = relative };
    }

    private static object StateDifference(ParticleStateView reference, ParticleStateView actual)
    {
        if (reference.Step != actual.Step || reference.Time != actual.Time || !reference.Ids.SequenceEqual(actual.Ids))
        {
            throw new InvalidOperationException("Backend identity/time parity failed.");
        }

        return new { positions = Difference(reference.Positions, actual.Positions), velocities = Difference(reference.Velocities, actual.Velocities) };
    }

    private static double Milliseconds(long ticks) => 1000d * ticks / Stopwatch.Frequency;

    private sealed class BackendRun : IDisposable
    {
        private readonly Series build;
        private readonly Series interaction;
        private readonly Series integration;
        private int warmupCount;
        private int stageWarmupCount;

        internal BackendRun(ExperimentDefinition experiment, ForceBackend backend, int workers, int repetitions, int samples, int stageSamples)
        {
            Name = backend == ForceBackend.Direct ? "direct" : $"grid-{workers}";
            Simulation = new Simulation(experiment.InitialState, experiment.Parameters, experiment.Matrix, experiment.TimeStep, backend, workers);
            Evaluator = backend == ForceBackend.Direct
                ? new DirectForceEvaluator(experiment.Parameters, experiment.Matrix)
                : new UniformGridForceEvaluator(experiment.Parameters, experiment.Matrix, workers);
            Forces = new Vector2d[experiment.InitialState.Count];
            OutputPositions = new Vector2d[experiment.InitialState.Count];
            OutputVelocities = new Vector2d[experiment.InitialState.Count];
            Steps = new(repetitions, samples);
            build = new(repetitions, stageSamples);
            interaction = new(repetitions, stageSamples);
            integration = new(repetitions, stageSamples);
        }

        internal string Name { get; }
        internal Simulation Simulation { get; }
        internal IForceEvaluator Evaluator { get; }
        internal Vector2d[] Forces { get; }
        internal Vector2d[] OutputPositions { get; }
        internal Vector2d[] OutputVelocities { get; }
        internal Series Steps { get; }
        internal ForceEvaluationStatistics Statistics { get; private set; }

        internal void Warmup(ExperimentDefinition experiment)
        {
            long start = Stopwatch.GetTimestamp();
            do
            {
                Simulation.Reset(experiment.InitialState);
                Simulation.Step();
                warmupCount++;
            }
            while (warmupCount < MinimumWarmupSteps || Stopwatch.GetElapsedTime(start).TotalSeconds < MinimumWarmupSeconds);

            start = Stopwatch.GetTimestamp();
            do
            {
                Evaluator.Evaluate(experiment.InitialState.Observe(), Forces);
                SemiImplicitEuler.Integrate(experiment.InitialState.Observe(), Forces, experiment.TimeStep,
                    SemiImplicitEuler.DampingFactor(experiment.TimeStep, experiment.Parameters.DampingHalfLife), OutputPositions, OutputVelocities);
                stageWarmupCount++;
            }
            while (stageWarmupCount < MinimumWarmupSteps || Stopwatch.GetElapsedTime(start).TotalSeconds < MinimumWarmupSeconds);
        }

        internal void MeasureStep(Series series, int sample)
        {
            long before = GC.GetTotalAllocatedBytes(true);
            long start = Stopwatch.GetTimestamp();
            Simulation.Step();
            long ticks = Stopwatch.GetTimestamp() - start;
            series.Record(sample, ticks, GC.GetTotalAllocatedBytes(true) - before);
        }

        internal void MeasureStages(ExperimentDefinition experiment, int sample)
        {
            ParticleStateView input = experiment.InitialState.Observe();
            UniformGridForceEvaluator? grid = Evaluator as UniformGridForceEvaluator;
            if (grid is not null)
            {
                long beforeBuild = GC.GetTotalAllocatedBytes(true);
                long buildStart = Stopwatch.GetTimestamp();
                grid.BuildIndex(input);
                long buildTicks = Stopwatch.GetTimestamp() - buildStart;
                build.Record(sample, buildTicks, GC.GetTotalAllocatedBytes(true) - beforeBuild);
            }

            long before = GC.GetTotalAllocatedBytes(true);
            long callerBefore = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            Statistics = grid is null ? Evaluator.Evaluate(input, Forces) : grid.EvaluatePrepared(input, Forces);
            long ticks = Stopwatch.GetTimestamp() - start;
            long attributedBytes = GC.GetAllocatedBytesForCurrentThread() - callerBefore + (grid?.LastWorkerAllocatedBytes ?? 0);
            interaction.Record(sample, ticks, GC.GetTotalAllocatedBytes(true) - before, attributedBytes);
            double damping = SemiImplicitEuler.DampingFactor(experiment.TimeStep, experiment.Parameters.DampingHalfLife);
            before = GC.GetTotalAllocatedBytes(true);
            start = Stopwatch.GetTimestamp();
            SemiImplicitEuler.Integrate(input, Forces, experiment.TimeStep, damping, OutputPositions, OutputVelocities);
            ticks = Stopwatch.GetTimestamp() - start;
            integration.Record(sample, ticks, GC.GetTotalAllocatedBytes(true) - before);
        }

        internal object Summarize(object forceDifference, object nextStateDifference) => new
        {
            Name,
            requestedBackend = Simulation.Backend,
            algorithmVersion = Simulation.Backend == ForceBackend.Direct ? "direct/1" : UniformGridForceEvaluator.AlgorithmVersion,
            configuredWorkers = Simulation.WorkerCount,
            warmupCount,
            stageWarmupCount,
            lastExecution = Simulation.LastExecution,
            step = Steps.Summarize(),
            indexConstruction = Simulation.Backend == ForceBackend.UniformGrid ? build.Summarize() : null,
            interactionIncludingWalls = interaction.Summarize(),
            integration = integration.Summarize(),
            candidateDirectedPairs = Statistics.CandidatePairs,
            acceptedDirectedPairs = Statistics.AcceptedPairs,
            coincidentUnorderedPairs = Statistics.OverlapPairs,
            forceDifference,
            nextStateDifference,
            splitStageDifference = new
            {
                positions = Difference(Simulation.Observe().Positions, OutputPositions),
                velocities = Difference(Simulation.Observe().Velocities, OutputVelocities)
            }
        };

        public void Dispose()
        {
            Simulation.Dispose();
            (Evaluator as IDisposable)?.Dispose();
        }
    }

    private sealed class Series(int repetitions, int samples)
    {
        private readonly long[] ticks = new long[repetitions * samples];
        private readonly long[] bytes = new long[repetitions * samples];
        private readonly long[] attributedBytes = Enumerable.Repeat(-1L, repetitions * samples).ToArray();

        internal double Median => Percentile(ticks.Select(Milliseconds).ToArray(), 0.5);
        internal long TotalBytes => bytes.Sum();

        internal void Record(int sample, long elapsedTicks, long allocatedBytes, long? attributedAllocatedBytes = null)
        {
            ticks[sample] = elapsedTicks;
            bytes[sample] = allocatedBytes;
            attributedBytes[sample] = attributedAllocatedBytes ?? -1;
        }

        internal object Summarize()
        {
            double[] milliseconds = ticks.Select(Milliseconds).ToArray();
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
                allocatedBytes = bytes.Sum(),
                maximumAllocatedBytesPerSample = bytes.Max(),
                callerPlusWorkerAllocatedBytes = attributedBytes.All(value => value >= 0) ? attributedBytes.Sum() : (long?)null,
                repetitions = Enumerable.Range(0, repetitions).Select(repetition => new
                {
                    p50Milliseconds = Percentile(milliseconds[(repetition * samples)..((repetition + 1) * samples)], 0.5),
                    p95Milliseconds = Percentile(milliseconds[(repetition * samples)..((repetition + 1) * samples)], 0.95),
                    milliseconds = milliseconds[(repetition * samples)..((repetition + 1) * samples)],
                    allocatedBytes = bytes[(repetition * samples)..((repetition + 1) * samples)],
                    callerPlusWorkerAllocatedBytes = attributedBytes.All(value => value >= 0)
                        ? attributedBytes[(repetition * samples)..((repetition + 1) * samples)] : null
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
