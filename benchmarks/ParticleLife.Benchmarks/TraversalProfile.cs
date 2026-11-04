using System.Diagnostics;
using System.Text.Json;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Benchmarks;

/// <summary>Profiles enumeration and exact ordinary-scale geometry independently of force mathematics.</summary>
/// <remarks>This standalone source also builds against the accepted M4 snapshot for like-for-like profiling.</remarks>
internal static class TraversalProfile
{
    internal static void Run(string inputPath, string outputPath, int samples = 15, int repetitions = 3)
    {
        if (samples < 5 || repetitions < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(samples));
        }

        ExperimentDefinition experiment = ExperimentFile.Load(inputPath);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        Directory.CreateDirectory(directory!);
        object result = new
        {
            inputFile = Path.GetFullPath(inputPath),
            inputSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(inputPath))),
            environment = LatencyRunner.CaptureEnvironment("prepared candidate/geometry profiling, one thread"),
            profile = Measure(experiment, samples, repetitions)
        };
        File.WriteAllText(outputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        }) + "\n");
        Console.WriteLine($"Profile: {Path.GetFullPath(outputPath)}");
    }

    /// <summary>Times index enumeration and ordinary-scale geometry filtering independently of force mathematics.</summary>
    /// <remarks>
    /// The component/square/square-root filter has the same ordinary-scale strict cutoff as the kernel.
    /// It is a benchmark probe, not a production replacement. Tiny/subnormal model ranges are excluded.
    /// Source-order iteration retains a checksum so a compiler cannot discard traversal as dead work.
    /// </remarks>
    internal static object Measure(ExperimentDefinition experiment, int samples, int repetitions)
    {
        UniformGridIndex index = new(experiment.Parameters.Cutoff);
        ParticleStateView state = experiment.InitialState.Observe();
        double cutoff = experiment.Parameters.Cutoff;
        double square = cutoff * cutoff;
        if (square < PairForceKernel.SmallestNormal || !index.Build(state.Positions))
        {
            return new { measured = false, reason = "Profile limited to indexable ordinary-scale geometry." };
        }

        Series traversal = new(repetitions, samples);
        Series filtered = new(repetitions, samples);
        (long Candidates, long Accepted, long Checksum) counts = default;
        (long Candidates, long Accepted, long Checksum) traversalCounts = default;
        int traversalWarmupCount = 0;
        long warmupStart = Stopwatch.GetTimestamp();
        do
        {
            traversalCounts = Traverse(index, state, cutoff, false);
            traversalWarmupCount++;
        }
        while (traversalWarmupCount < 32 || Stopwatch.GetElapsedTime(warmupStart).TotalSeconds < 0.5);

        int geometryWarmupCount = 0;
        warmupStart = Stopwatch.GetTimestamp();
        do
        {
            _ = Traverse(index, state, cutoff, true);
            geometryWarmupCount++;
        }
        while (geometryWarmupCount < 32 || Stopwatch.GetElapsedTime(warmupStart).TotalSeconds < 0.5);

        for (int i = 0; i < repetitions * samples; i++)
        {
            long before = GC.GetTotalAllocatedBytes(true);
            long start = Stopwatch.GetTimestamp();
            traversalCounts = Traverse(index, state, cutoff, false);
            long ticks = Stopwatch.GetTimestamp() - start;
            traversal.Record(i, ticks, GC.GetTotalAllocatedBytes(true) - before);
            before = GC.GetTotalAllocatedBytes(true);
            start = Stopwatch.GetTimestamp();
            counts = Traverse(index, state, cutoff, true);
            ticks = Stopwatch.GetTimestamp() - start;
            filtered.Record(i, ticks, GC.GetTotalAllocatedBytes(true) - before);
        }

        DirectForceEvaluator reference = new(experiment.Parameters, experiment.Matrix);
        ForceEvaluationStatistics expected = reference.Evaluate(state, new Vector2d[state.Count]);
        if (counts.Accepted != expected.AcceptedPairs)
        {
            throw new InvalidOperationException("Ordinary-scale profile acceptance differs from the force reference; do not use this profile for this input.");
        }

        if (traversalCounts.Candidates != counts.Candidates || traversalCounts.Checksum != counts.Checksum)
        {
            throw new InvalidOperationException("Traversal probes disagree on candidate enumeration.");
        }

        return new
        {
            measured = true,
            method = "Prepared cell candidate enumeration in original-index space; traversal checksum and ordinary-scale geometric filter without force coefficients/normalization/accumulation. Neither probe is a substitute for complete interaction timing.",
            candidatePairs = counts.Candidates,
            acceptedPairs = counts.Accepted,
            traversalWarmupCount,
            geometryWarmupCount,
            checksum = counts.Checksum,
            traversalChecksum = traversalCounts.Checksum,
            traversalOnly = traversal.Summarize(),
            traversalAndGeometry = filtered.Summarize()
        };
    }

    private static (long Candidates, long Accepted, long Checksum) Traverse(UniformGridIndex index,
        ParticleStateView state, double cutoff, bool filter)
    {
        long candidates = 0;
        long accepted = 0;
        long checksum = 0;
        double square = cutoff * cutoff;
        for (int receiver = 0; receiver < state.Count; receiver++)
        {
            UniformGridIndex.CandidateEnumerator iterator = index.Candidates(receiver);
            while (iterator.MoveNext())
            {
                int source = iterator.Current;
                if (source == receiver)
                {
                    continue;
                }

                candidates++;
                checksum += source;
                if (filter)
                {
                    Vector2d delta = state.Positions[source] - state.Positions[receiver];
                    double distanceSquared = delta.X * delta.X + delta.Y * delta.Y;
                    if ((delta.X != 0 || delta.Y != 0) && Math.Abs(delta.X) < cutoff && Math.Abs(delta.Y) < cutoff
                        && distanceSquared < square && Math.Sqrt(distanceSquared) < cutoff)
                    {
                        accepted++;
                    }
                }
            }
        }

        return (candidates, accepted, checksum);
    }

    private sealed class Series(int repetitions, int samples)
    {
        private readonly long[] ticks = new long[repetitions * samples];
        private readonly long[] bytes = new long[repetitions * samples];

        internal void Record(int sample, long elapsedTicks, long allocatedBytes)
        {
            ticks[sample] = elapsedTicks;
            bytes[sample] = allocatedBytes;
        }

        internal object Summarize()
        {
            double[] milliseconds = ticks.Select(tick => 1000d * tick / Stopwatch.Frequency).ToArray();
            double[] sorted = (double[])milliseconds.Clone();
            Array.Sort(sorted);
            return new
            {
                samples = milliseconds.Length,
                p50Milliseconds = sorted[(int)Math.Ceiling(0.5 * sorted.Length) - 1],
                p95Milliseconds = sorted[(int)Math.Ceiling(0.95 * sorted.Length) - 1],
                allocatedBytes = bytes.Sum(),
                maximumAllocatedBytesPerSample = bytes.Max(),
                milliseconds,
                allocatedBytesPerSample = bytes
            };
        }
    }
}
