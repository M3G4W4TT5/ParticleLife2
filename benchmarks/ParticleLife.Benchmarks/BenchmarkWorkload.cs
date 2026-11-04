using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.State;

namespace ParticleLife.Benchmarks;

/// <summary>Builds explicit initial states for comparing the serial reference on controlled geometries.</summary>
internal static class BenchmarkWorkload
{
    internal const int Seed = 1729;

    internal static IEnumerable<string> AllCases
    {
        get
        {
            foreach (int population in new[] { 500, 2_000, 10_000 })
            {
                yield return $"uniform-fixed-{population}";
                yield return $"uniform-density-{population}";
                yield return $"cluster-{population}";
                yield return $"band-{population}";
            }

            yield return "boundary-overlap-500";
        }
    }

    internal static IEnumerable<string> Select(string selection)
    {
        if (selection == "all")
        {
            return AllCases;
        }

        if (selection == "quick")
        {
            return ["uniform-fixed-500", "cluster-500", "band-500", "boundary-overlap-500"];
        }

        if (AllCases.Contains(selection, StringComparer.Ordinal))
        {
            return [selection];
        }

        throw new ArgumentException($"Unknown benchmark selection '{selection}'.", nameof(selection));
    }

    /// <remarks>
    /// Fixed-density domains have side sqrt(N/500), retaining density 500 per reduced area.
    /// Cluster positions lie in [0.475,0.525]^2, so every distinct pair is inside R=0.1.
    /// Band positions lie in [0,1] x [0.49,0.51]. The edge case spans [-0.1,1.1]^2
    /// and explicitly duplicates one position. Positions and matrix values are saved, not just this seed.
    /// </remarks>
    internal static ExperimentDefinition Create(string caseName)
    {
        if (!AllCases.Contains(caseName, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unknown benchmark case '{caseName}'.", nameof(caseName));
        }

        int population = int.Parse(caseName[(caseName.LastIndexOf('-') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        double side = caseName.StartsWith("uniform-density-", StringComparison.Ordinal) ? Math.Sqrt(population / 500d) : 1;
        ModelParameters parameters = new() { WorldWidth = side, WorldHeight = side };
        ExperimentDefinition source = PresetGenerator.Create(PresetKind.Random, population, 7, Seed, parameters);
        Vector2d[] positions = source.InitialState.Positions.ToArray();
        for (int i = 0; i < positions.Length; i++)
        {
            Vector2d position = positions[i];
            if (caseName.StartsWith("cluster-", StringComparison.Ordinal))
            {
                positions[i] = new Vector2d(0.475 + 0.05 * position.X, 0.475 + 0.05 * position.Y);
            }
            else if (caseName.StartsWith("band-", StringComparison.Ordinal))
            {
                positions[i] = new Vector2d(position.X, 0.49 + 0.02 * position.Y);
            }
            else if (caseName.StartsWith("boundary-overlap-", StringComparison.Ordinal))
            {
                positions[i] = new Vector2d(1.2 * position.X - 0.1, 1.2 * position.Y - 0.1);
            }
        }

        if (caseName.StartsWith("boundary-overlap-", StringComparison.Ordinal))
        {
            positions[1] = positions[0];
        }

        ParticleSnapshot snapshot = new(source.InitialState.Ids, positions, source.InitialState.Velocities, source.InitialState.Species);
        GeneratorMetadata generator = new($"benchmark/{caseName}", "1", Seed, System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        return new ExperimentDefinition(caseName, parameters, source.Matrix, snapshot, source.TimeStep, generator);
    }
}
