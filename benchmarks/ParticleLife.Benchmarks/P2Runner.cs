using System.Diagnostics;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Benchmarks;

/// <summary>Measures explicit CPU worker selections on immutable saved inputs after bounded-duration warmup.</summary>
internal static class P2Runner
{
    internal static void Run(string outputPath, string inputPath, int workers, string mode, int samples)
    {
        if (samples is < 5 or > 1000 || mode is not "static" and not "evolving")
        {
            throw new ArgumentException("Use 5..1000 samples and static or evolving mode.");
        }

        ForceWorkerConfiguration.Validate(ForceBackend.UniformGrid, workers);
        if (File.Exists(outputPath))
        {
            throw new IOException("Use a fresh P2 output file.");
        }

        ExperimentDefinition experiment = ExperimentFile.Load(inputPath);
        using Simulation simulation = experiment.CreateSimulation(ForceBackend.UniformGrid, workers);
        int warmup = 0;
        long began = Stopwatch.GetTimestamp();
        do
        {
            simulation.Reset(experiment.InitialState);
            simulation.Step();
            warmup++;
        }
        while (warmup < 8 || Stopwatch.GetElapsedTime(began).TotalSeconds < 0.5);

        simulation.Reset(experiment.InitialState);
        Series steps = new(samples);
        long candidates = 0, accepted = 0;
        began = Stopwatch.GetTimestamp();
        for (int i = 0; i < samples; i++)
        {
            if (mode == "static")
            {
                simulation.Reset(experiment.InitialState);
            }

            steps.Measure(i, simulation.Step);
            candidates += simulation.LastStatistics.CandidatePairs;
            accepted += simulation.LastStatistics.AcceptedPairs;
        }

        double measuredSeconds = Stopwatch.GetElapsedTime(began).TotalSeconds;
        ParticleSnapshot completed = simulation.CaptureSnapshot();
        UniformGridForceEvaluator evaluator = new(experiment.Parameters, experiment.Matrix, workers);
        using (evaluator)
        {
            Vector2d[] forces = new Vector2d[experiment.InitialState.Count];
            // Stage probes use the saved input, not an index from another evolving boundary.
            int stageWarmup = 0;
            long stageBegan = Stopwatch.GetTimestamp();
            do
            {
                evaluator.Evaluate(experiment.InitialState.Observe(), forces);
                stageWarmup++;
            }
            while (stageWarmup < 8 || Stopwatch.GetElapsedTime(stageBegan).TotalSeconds < 0.5);
            Series build = new(samples), interaction = new(samples);
            for (int i = 0; i < samples; i++)
            {
                build.Measure(i, () => evaluator.BuildIndex(experiment.InitialState.Observe()));
                interaction.Measure(i, () => evaluator.EvaluatePrepared(experiment.InitialState.Observe(), forces));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            P1Inputs.WriteJson(outputPath, new
            {
                schemaVersion = 1,
                environment = LatencyRunner.CaptureEnvironment("P2 CPU binary64 uniform grid", workers),
                inputFile = Path.GetFullPath(inputPath),
                inputSha256 = P1Inputs.HashFile(inputPath),
                experiment.Name,
                population = experiment.InitialState.Count,
                experiment.Parameters,
                experiment.TimeStep,
                mode,
                workers,
                warmup,
                stageWarmup,
                minimumWarmupCalls = 8,
                minimumWarmupSeconds = 0.5,
                method = "One worker selection per process. Static resets outside timing; evolving samples begin again at saved input after static warmup. Stage probes are static and separate. Process-wide precise allocation counters include background workers. External supervisor must enforce wall deadline for dense cases.",
                steps = steps.Summarize(),
                build = build.Summarize(),
                interaction = interaction.Summarize(),
                candidates,
                accepted,
                measuredSeconds,
                stepsPerSecond = samples / measuredSeconds,
                achievedSimulationSpeed = mode == "evolving" ? completed.Time / measuredSeconds : (double?)null,
                execution = simulation.LastExecution,
                grid = evaluator.IndexStatistics,
                finalStep = completed.Step,
                finalTime = completed.Time
            });
        }

        Console.WriteLine($"P2 {workers} workers {mode}: {steps.Median:F4} ms; {outputPath}");
    }

    internal sealed class Series(int samples)
    {
        private readonly double[] milliseconds = new double[samples];
        private readonly long[] allocations = new long[samples];

        internal double Median => Percentile(0.5);

        internal void Measure(int index, Action operation)
        {
            long before = GC.GetTotalAllocatedBytes(true);
            long began = Stopwatch.GetTimestamp();
            operation();
            milliseconds[index] = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            allocations[index] = GC.GetTotalAllocatedBytes(true) - before;
        }

        internal object Summarize() => new
        {
            samples = milliseconds.Length,
            p50Milliseconds = Median,
            p95Milliseconds = Percentile(0.95),
            allocatedBytes = allocations.Sum(),
            maximumAllocatedBytes = allocations.Max(),
            milliseconds,
            allocations
        };

        private double Percentile(double percentile)
        {
            double[] sorted = (double[])milliseconds.Clone();
            Array.Sort(sorted);
            return sorted[(int)Math.Ceiling(percentile * sorted.Length) - 1];
        }
    }
}
