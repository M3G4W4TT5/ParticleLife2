using BenchmarkDotNet.Attributes;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;

namespace ParticleLife.Benchmarks;

/// <summary>Measures either serial backend from the same saved starting state on every iteration.</summary>
/// <remarks>
/// Iteration setup deliberately forces one invocation per iteration. Setup and file serialization
/// are outside the timed method. Very short cases can have wide timing intervals; the separate
/// latency runner supplies repeated phase timings and exact thread allocation deltas.
/// </remarks>
[MemoryDiagnoser]
public class DirectStepBenchmarks
{
    private ExperimentDefinition experiment = null!;
    private Simulation simulation = null!;

    /// <summary>Gets the controlled benchmark names offered to BenchmarkDotNet.</summary>
    public IEnumerable<string> Cases => BenchmarkWorkload.AllCases;

    /// <summary>Gets or sets the geometry and population under measurement.</summary>
    [ParamsSource(nameof(Cases))]
    public string Case { get; set; } = "uniform-fixed-500";

    /// <summary>Gets or sets the requested exact neighbour-search implementation.</summary>
    [Params(ForceBackend.Direct, ForceBackend.UniformGrid)]
    public ForceBackend Backend { get; set; }

    /// <summary>Creates buffers and exports the exact initial state outside measurements.</summary>
    [GlobalSetup]
    public void Setup()
    {
        string directory = Environment.GetEnvironmentVariable("PARTICLELIFE_BENCHMARK_INPUTS")
            ?? throw new InvalidOperationException("Start benchmarks through the bdn command so saved input has a persistent destination.");
        Directory.CreateDirectory(directory);
        string inputPath = Path.Combine(directory, Case + ".experiment.json");
        if (File.Exists(inputPath))
        {
            experiment = ExperimentFile.Load(inputPath);
        }
        else
        {
            experiment = BenchmarkWorkload.Create(Case);
            ExperimentFile.Save(inputPath, experiment);
        }

        simulation = new Simulation(experiment.InitialState, experiment.Parameters, experiment.Matrix, experiment.TimeStep, Backend);
        // Establish all buffers and JIT paths before a measured iteration. Resets do not discard capacity.
        int warmupCount = 0;
        long warmupStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        do
        {
            simulation.Reset(experiment.InitialState);
            simulation.Step();
            warmupCount++;
        }
        while (warmupCount < 64 || System.Diagnostics.Stopwatch.GetElapsedTime(warmupStarted).TotalSeconds < 1);
    }

    /// <summary>Restores the identical initial state without timing its copying cost.</summary>
    [IterationSetup]
    public void Reset() => simulation.Reset(experiment.InitialState);

    /// <summary>Rebuilds any index, evaluates forces and completes one consistent integration step.</summary>
    [Benchmark]
    public void Step() => simulation.Step();
}
