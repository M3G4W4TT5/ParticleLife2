using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;
using ParticleLife.Viewer;

namespace ParticleLife.Benchmarks;

/// <summary>Separates graphics-free steady playback, actual live-edit costs and structural allocation.</summary>
/// <remarks>Run alone after building, with the native viewer closed. Exact saved inputs and assembly
/// hashes identify each workload. UI layout/drawing/present are deliberately measured in the native host.
/// Process-wide allocation counters include workers and are outside timed regions.</remarks>
internal static class EditorRunner
{
    internal static void Run(string outputDirectory, string inputPath, int samples)
    {
        if (samples is < 20 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(samples), "Use 20..10000 samples.");
        }

        Directory.CreateDirectory(outputDirectory);
        ExperimentDefinition input = ExperimentFile.Load(inputPath);
        List<object> results = [];
        foreach (int workers in new[] { 1, 4 })
        {
            ExperimentDefinition experiment = input.WithBackend(ForceBackend.UniformGrid, workers);
            using PlaybackController playback = new(experiment, diagnosticEverySteps: 0);
            ExperimentEditor editor = new(playback);
            SimulationConfiguration original = editor.Configuration;
            SimulationConfiguration altered = new(original.Parameters with { ForceScale = original.Parameters.ForceScale + 0.001 },
                original.Matrix, original.TimeStep, original.Backend, original.WorkerCount);

            // Reset deliberately reconstructs the baseline simulation. Prime its new evaluator outside
            // measurement, then measure the second step from the same once-advanced input every time.
            // Without this prime, first-use index/worker buffers are incorrectly counted as steady steps.
            int steadyWarmupCalls = Warm(() =>
            {
                playback.Reset();
                playback.AdvanceFrame(original.TimeStep);
                playback.AdvanceFrame(original.TimeStep);
            });
            Series steady = new(samples);
            Action step = () => playback.AdvanceFrame(original.TimeStep);
            for (int i = 0; i < samples; i++)
            {
                playback.Reset();
                playback.AdvanceFrame(original.TimeStep);
                steady.Measure(i, step);
            }

            editor.Reset();
            editor.BeginGesture("Measured force-scale drag");
            int value = 0;
            Action edit = () => editor.ApplyConfiguration((++value & 1) == 0 ? original : altered, "Measured force-scale drag");
            int editWarmupCalls = Warm(edit, () =>
            {
                // Tiny edits can execute hundreds of thousands of times in a timed warmup. Bound
                // discarded warmup history without omitting any event from the measured gesture.
                editor.Reset();
                editor.BeginGesture("Measured force-scale drag");
                value = 0;
            });
            Series editing = new(samples);
            for (int i = 0; i < samples; i++)
            {
                editing.Measure(i, edit);
            }

            editor.EndGesture();
            int appliedEvents = editor.SessionEventCount;

            // A separate continuous drag interleaves applied values with completed dynamics.
            editor.Reset();
            editor.BeginGesture("Measured evolving drag");
            value = 0;
            Action editAndStep = () => { edit(); playback.AdvanceFrame(original.TimeStep); };
            int draggingWarmupCalls = Warm(editAndStep);
            Series dragging = new(samples);
            for (int i = 0; i < samples; i++)
            {
                dragging.Measure(i, editAndStep);
            }

            editor.EndGesture();

            // Post-edit steady stepping includes established grid/worker capacity, with no history writes.
            int settledWarmupCalls = Warm(step);
            Series settled = new(samples);
            for (int i = 0; i < samples; i++)
            {
                settled.Measure(i, step);
            }

            editor.Reset();
            int count = playback.Observe().Count;
            int grownCount = checked(count + Math.Max(1, count / 4));
            int species = editor.Configuration.Matrix.SpeciesCount;
            Series structural = new(2);
            structural.Measure(0, () => editor.ApplyPopulation(species, ExperimentEditor.DistributePopulation(grownCount, species)));
            structural.Measure(1, () => editor.ApplyPopulation(species, ExperimentEditor.DistributePopulation(count, species)));

            results.Add(new
            {
                workers,
                count,
                species,
                warmupCalls = new
                {
                    steady = steadyWarmupCalls,
                    editOnly = editWarmupCalls,
                    evolvingEditAndStep = draggingWarmupCalls,
                    evolvingSettled = settledWarmupCalls
                },
                steadyController = steady.Report(),
                editOnly = editing.Report(),
                editOnlyAppliedEventsIncludingWarmup = appliedEvents,
                evolvingEditAndStep = dragging.Report(),
                evolvingSettledController = settled.Report(),
                structuralSingleObservations = new { grownCount, milliseconds = structural.Milliseconds, allocatedBytes = structural.Bytes },
                processWideZeroAllocationForSteadySamples = steady.Bytes.All(bytes => bytes == 0),
                processWideZeroAllocationForSettledSamples = settled.Bytes.All(bytes => bytes == 0)
            });
            Console.WriteLine(FormattableString.Invariant($"Editor {count}, grid/{workers}: steady p50 {steady.Percentile(0.5):F4} ms, edit p50 {editing.Percentile(0.5):F4} ms, drag+step p50 {dragging.Percentile(0.5):F4} ms; settled bytes {settled.Bytes.Sum()}."));
        }

        object report = new
        {
            schemaVersion = 2,
            capturedAtUtc = DateTimeOffset.UtcNow,
            processId = Environment.ProcessId,
            inputPath = Path.GetFullPath(inputPath),
            inputSha256 = Hash(inputPath),
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            logicalProcessors = Environment.ProcessorCount,
            coreVersion = typeof(Simulation).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            coreAssemblySha256 = Hash(typeof(Simulation).Assembly.Location),
            viewerAssemblySha256 = Hash(typeof(ExperimentEditor).Assembly.Location),
            experiment = JsonSerializer.Deserialize<JsonElement>(ExperimentFile.Serialize(input)),
            method = "At least 64 calls and 0.5 seconds warmup per path. Nearest-rank p50/p95, no trimming. Static steady resets and one evaluator-priming step occur outside timing; each measured second step begins from identical once-advanced saved values. Edit-only warmup discards history every 1024 calls outside measured samples; actual measured edits retain every event and one undo gesture. Force-scale candidate configurations are constructed outside measurement. Drag/settled paths evolve independently and are not static-workload comparisons. Structural growth/shrink are individual observations, not percentiles. Diagnostics, rendering and I/O excluded. Run without competing builds, tests or viewers.",
            allocationMethod = "GC.GetTotalAllocatedBytes(true) outside each timed boundary covers all workers and unrelated process allocations. Actual edits allocate history/configuration; stable steps do not.",
            results
        };
        string output = Path.Combine(outputDirectory, "editor.json");
        using FileStream stream = new(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
        Console.WriteLine($"Report: {Path.GetFullPath(output)}");
    }

    private static int Warm(Action action, Action? clearWarmupBatch = null)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        int count = 0;
        while (count < 64 || elapsed.Elapsed.TotalSeconds < 0.5)
        {
            action();
            count++;
            if (clearWarmupBatch is not null && count % 1024 == 0)
            {
                clearWarmupBatch();
            }
        }

        return count;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class Series(int count)
    {
        internal double[] Milliseconds { get; } = new double[count];
        internal long[] Bytes { get; } = new long[count];

        internal void Measure(int index, Action action)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            long started = Stopwatch.GetTimestamp();
            action();
            long ended = Stopwatch.GetTimestamp();
            Bytes[index] = GC.GetTotalAllocatedBytes(precise: true) - before;
            Milliseconds[index] = Stopwatch.GetElapsedTime(started, ended).TotalMilliseconds;
        }

        internal double Percentile(double percentile)
        {
            double[] ordered = (double[])Milliseconds.Clone();
            Array.Sort(ordered);
            return ordered[Math.Max(0, (int)Math.Ceiling(percentile * ordered.Length) - 1)];
        }

        internal object Report() => new
        {
            samples = Milliseconds.Length,
            p50Milliseconds = Percentile(0.5),
            p95Milliseconds = Percentile(0.95),
            maximumMilliseconds = Milliseconds.Max(),
            allocatedBytes = Bytes,
            rawMilliseconds = Milliseconds
        };
    }
}
