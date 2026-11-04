using BenchmarkDotNet.Attributes;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Numerics;

namespace ParticleLife.Benchmarks;

/// <summary>Measures one direct step from an identical saved starting state on every iteration.</summary>
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

        simulation = new Simulation(experiment.InitialState, experiment.Parameters, experiment.Matrix, experiment.TimeStep);
    }

    /// <summary>Restores the identical initial state without timing its copying cost.</summary>
    [IterationSetup]
    public void Reset() => simulation.Reset(experiment.InitialState);

    /// <summary>Evaluates all directed pairs and completes one consistent integration step.</summary>
    [Benchmark]
    public void Step() => simulation.Step();
}
