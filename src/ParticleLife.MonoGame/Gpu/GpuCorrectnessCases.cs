using ParticleLife.Core.Execution;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.MonoGame.Gpu;

/// <summary>Defines small deterministic hardware-oracle scenes and one linear-work large scan fixture.</summary>
/// <remarks>These are correctness fixtures, not performance workloads. The large zero-force case has
/// an analytic stationary reference so its three-level scan does not require a quadratic CPU run.</remarks>
internal sealed record GpuCorrectnessCase(string Name, SimulationConfiguration Configuration, ParticleSnapshot Initial,
    int Steps, double AbsoluteTolerance = 0.00002, double RelativeTolerance = 0.0002,
    bool StationaryReference = false, bool ScalarEdit = false, bool RequireMotion = false)
{
    internal static IReadOnlyList<GpuCorrectnessCase> Create()
    {
        List<GpuCorrectnessCase> cases = [];
        ModelParameters ordinary = new() { Cutoff = 0.25, WallWidth = 0.125, ForceScale = 0.5, DampingHalfLife = 0.5 };
        InteractionMatrix directed = new(2, [0, 0.75, -0.5, 0.25]);
        cases.Add(new("soft-global-evolving", new(ordinary, directed, 1.0 / 512), Lattice(96, 2, 0.1, 0.8), 24, ScalarEdit: true));
        cases.Add(new("open-negative-hash-collisions", new(ordinary with { Boundary = BoundaryKind.Open }, directed, 1.0 / 1024),
            Lattice(513, 2, -3, 7), 12));
        cases.Add(new("dense-unbounded-bucket", new(ordinary with { Boundary = BoundaryKind.Open }, directed, 1.0 / 4096),
            Lattice(769, 2, 0.4, 0.006), 12, 0.0002, 0.002, RequireMotion: true));
        PairInteractionRanges ranges = new(2, [0, 0.015625, 0.0625, 0.03125], [0.125, 0.375, 0.25, 0.1875]);
        cases.Add(new("directed-ranges-open", new(ordinary with { Boundary = BoundaryKind.Open }, new(2, [0, 0.75, -0.5, 0], ranges), 1.0 / 1024),
            Lattice(145, 2, 0.15, 0.5), 20, RequireMotion: true));
        cases.Add(new("periodic-negative-cell-single-step", new(ordinary with
        {
            Boundary = BoundaryKind.Periodic,
            Cutoff = 0.125,
            DampingHalfLife = null
        }, new(1, [1], new(1, [0], [0.125])), 1.0 / 1024),
            State([new(0.0078125, 0.25), new(0.9921875, 0.25), new(0.25, 0.0078125), new(0.25, 0.9921875)],
                [default, default, default, default], 1), 1, 0, 0, RequireMotion: true));
        foreach (double cutoff in new[] { 0.5, 0.375, 0.125 })
        {
            PairInteractionRanges periodicRanges = new(2, [0, cutoff / 8, cutoff / 4, cutoff / 16],
                [cutoff, cutoff / 2, cutoff * 0.75, cutoff]);
            cases.Add(new("periodic-seams-" + cutoff.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new(ordinary with { Boundary = BoundaryKind.Periodic, Cutoff = cutoff }, new(2, [0.25, 0.75, -0.5, 0], periodicRanges), 1.0 / 1024),
                Lattice(81, 2, -0.03125, 1.0625), 24));
        }
        ModelParameters free = ordinary with { ForceScale = 0, WallStrength = 0, DampingHalfLife = null };
        cases.Add(new("reflecting-multiple-crossings", new(free with { Boundary = BoundaryKind.Reflecting }, directed, 0.125),
            State([new(0, 1), new(1, 0), new(-2.25, 4.5), new(0.5, 0.5)], [new(-8, 16), new(16, -8), new(32, -24), new(-4, 4)], 2), 12, 0, 0));
        cases.Add(new("periodic-multiple-crossings", new(free with { Boundary = BoundaryKind.Periodic }, directed, 0.125),
            State([new(0, 1), new(1, 0), new(-2.25, 4.5), new(0.5, 0.5)], [new(-8, 16), new(16, -8), new(32, -24), new(-4, 4)], 2), 12, 0, 0));
        cases.Add(new("exact-zero-with-zero-core", new(ordinary with { Boundary = BoundaryKind.Open, DampingHalfLife = null },
            new(1, [0], new(1, [0], [0.25])), 1.0 / 1024),
            State([new(0.25, 0.25), new(0.375, 0.25)], [default, default], 1), 4, 0, 0, StationaryReference: true));
        cases.Add(new("zero-coefficient-retains-core", new(ordinary with { Boundary = BoundaryKind.Open, DampingHalfLife = null },
            new(1, [0], new(1, [0.125], [0.25])), 1.0 / 1024),
            State([new(0.25, 0.25), new(0.28125, 0.25)], [default, default], 1), 1, RequireMotion: true));
        cases.Add(new("tiny-normal-coefficient", new(ordinary with { Boundary = BoundaryKind.Open, DampingHalfLife = null },
            new(1, [1e-30], new(1, [0], [0.25])), 1.0 / 1024),
            State([new(0.25, 0.25), new(0.375, 0.25)], [default, default], 1), 1, 0, 0.00001, RequireMotion: true));
        cases.Add(new("tiny-core-scaled-distance", new(ordinary with { Boundary = BoundaryKind.Open, DampingHalfLife = null },
            new(1, [0], new(1, [Math.ScaleB(1, -98)], [0.25])), 1.0 / 1024),
            State([new(0, 0), new(Math.ScaleB(1, -100), 0)], [default, default], 1), 1, RequireMotion: true));
        cases.Add(new("exact-overlap", new(ordinary with { Boundary = BoundaryKind.Open }, directed, 1.0 / 1024),
            State([new(0.5, 0.5), new(0.5, 0.5)], [default, default], 2), 3, 0, 0, StationaryReference: true));
        cases.Add(new("empty-membership", new(free with { Boundary = BoundaryKind.Open }, directed, 1.0 / 1024),
            new([], [], [], []), 3, 0, 0, StationaryReference: true));
        cases.Add(new("three-level-scan-131073", new(free with { Boundary = BoundaryKind.Open }, directed, 1.0 / 1024),
            Lattice(131073, 2, 0, 512, stationary: true), 1, 0, 0, StationaryReference: true));
        cases.Add(new("signed-zero-subnormal-storage", new(free with { Boundary = BoundaryKind.Open }, directed, 1.0 / 1024),
            State([new(float.Epsilon, -0.0), new(-float.Epsilon, 0.5)], [new(-float.Epsilon, float.Epsilon), new(-0.0, 0)], 2), 0));
        return cases;
    }

    private static ParticleSnapshot Lattice(int count, int species, double origin, double extent, bool stationary = false)
    {
        int side = (int)Math.Ceiling(Math.Sqrt(count));
        Vector2d[] positions = new Vector2d[count], velocities = new Vector2d[count];
        for (int index = 0; index < count; index++)
        {
            positions[index] = new(origin + extent * ((index % side + 0.31) / side), origin + extent * ((index / side + 0.57) / side));
            velocities[index] = stationary ? default : new(((index % 7) - 3) / 1024.0, ((index % 5) - 2) / 1024.0);
        }
        return State(positions, velocities, species);
    }

    internal static ParticleSnapshot State(Vector2d[] positions, Vector2d[] velocities, int speciesCount, double time = 0, long step = 0)
    {
        long[] identities = new long[positions.Length];
        int[] species = new int[positions.Length];
        for (int index = 0; index < identities.Length; index++)
        {
            identities[index] = index switch { 0 => long.MinValue, 1 => long.MaxValue, _ => index + 100 };
            species[index] = index % speciesCount;
        }
        return CaptureTransfer.Convert(new(identities, positions, velocities, species, time, step), StatePrecision.Float32, true);
    }
}
