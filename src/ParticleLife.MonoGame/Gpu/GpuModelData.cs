using System.Runtime.InteropServices;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.MonoGame.Gpu;

/// <summary>Defines the exact resident 32-byte std430 particle record in stable logical-slot order.</summary>
/// <remarks>Spatial scattering moves indices only. Identity low/high words preserve all signed Int64
/// bits without floating conversion. Position/velocity remain physical 2D quantities, not camera values.</remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct GpuParticle
{
    internal float X, Y, VelocityX, VelocityY;
    internal uint IdentityLow, IdentityHigh, Species, Padding;

    internal static GpuParticle[] Encode(ParticleSnapshot state)
    {
        GpuParticle[] result = new GpuParticle[state.Count];
        for (int index = 0; index < result.Length; index++)
        {
            ulong identity = unchecked((ulong)state.Ids[index]);
            result[index] = new()
            {
                X = StateComponent(state.Positions[index].X),
                Y = StateComponent(state.Positions[index].Y),
                VelocityX = StateComponent(state.Velocities[index].X),
                VelocityY = StateComponent(state.Velocities[index].Y),
                IdentityLow = (uint)identity,
                IdentityHigh = (uint)(identity >> 32),
                Species = (uint)state.Species[index]
            };
        }
        return result;
    }

    /// <summary>Preserves finite binary32 storage bits, including signed zero and subnormal captured values.</summary>
    /// <remarks>Model scalars have stricter range checks. Device arithmetic may flush subnormal intermediates,
    /// but explicit upload/capture/restore must not rewrite an already stored float32 particle component.</remarks>
    private static float StateComponent(double value)
    {
        float converted = (float)value;
        if (!float.IsFinite(converted)) { throw new NotSupportedException("GPU particle storage requires finite float32 components."); }
        return converted;
    }

    internal static ParticleSnapshot Decode(ReadOnlySpan<GpuParticle> state, double time, long step)
    {
        long[] ids = new long[state.Length];
        Vector2d[] positions = new Vector2d[state.Length], velocities = new Vector2d[state.Length];
        int[] species = new int[state.Length];
        for (int index = 0; index < state.Length; index++)
        {
            GpuParticle particle = state[index];
            ids[index] = unchecked((long)(((ulong)particle.IdentityHigh << 32) | particle.IdentityLow));
            positions[index] = new(particle.X, particle.Y);
            velocities[index] = new(particle.VelocityX, particle.VelocityY);
            species[index] = checked((int)particle.Species);
        }
        return new(ids, positions, velocities, species, time, step);
    }
}

/// <summary>Stores one directed coefficient/core/cutoff rule without coefficient quantization.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct GpuPairRule
{
    internal float Coefficient, Core, Cutoff, Padding;
}

/// <summary>Validates the practical float32 model domain before allocating or changing device resources.</summary>
/// <remarks>Nonzero scalar values may round normally but may not disappear, overflow or collapse a
/// directed core/cutoff interval. Cutoff squares must be normal finite floats. Particle storage retains
/// all finite float bits; device arithmetic may flush subnormal intermediates. Extreme CPU-only cases remain supported by the CPU.
/// The host clock accumulates the configured double timestep; GPU arithmetic uses its recorded float32
/// conversion. No timestep, radius, coefficient, particle count or density is adapted to meet performance.</remarks>
internal sealed class GpuModelData
{
    internal const int MaximumCellCoordinate = 1 << 20;
    internal const string Implementation = "opengl-hashed-grid-float32/1";
    internal readonly SimulationConfiguration Configuration;
    internal readonly GpuPairRule[] Rules;
    internal readonly float Width, Height, Cutoff, CellWidth, CellHeight, TimeStep, Damping, ForceScale, WallWidth, WallStrength;
    internal readonly int CellsX, CellsY;

    internal GpuModelData(SimulationConfiguration configuration)
    {
        Configuration = configuration;
        ModelParameters parameters = configuration.Parameters;
        Width = Positive(parameters.WorldWidth, "world width");
        Height = Positive(parameters.WorldHeight, "world height");
        TimeStep = Positive(configuration.TimeStep, "timestep");
        ForceScale = Component(parameters.ForceScale, "force scale");
        WallWidth = Positive(parameters.WallWidth, "wall width");
        WallStrength = Component(parameters.WallStrength, "wall strength");
        if (WallWidth > Math.Min(Width, Height) / 2)
        {
            throw new NotSupportedException("Float32 wall layers must remain at most half the shortest float32 extent.");
        }
        Damping = (float)SemiImplicitEuler.DampingFactor(configuration.TimeStep, parameters.DampingHalfLife);
        if (parameters.Boundary == BoundaryKind.Reflecting && (!float.IsFinite(2 * Width) || !float.IsFinite(2 * Height)))
        {
            throw new NotSupportedException("Reflecting GPU extents require finite doubled float32 lengths.");
        }
        Rules = new GpuPairRule[configuration.Matrix.Values.Length];
        int species = configuration.Matrix.SpeciesCount;
        float maximum = 0;
        for (int index = 0; index < Rules.Length; index++)
        {
            float cutoff = Positive(configuration.Matrix.CutoffRadius(index / species, index % species, parameters), "pair cutoff");
            float core = Component(configuration.Matrix.CoreRadius(index / species, index % species, parameters), "pair core");
            if (!float.IsNormal(cutoff * cutoff) || core >= cutoff)
            {
                throw new NotSupportedException("GPU pair cutoffs require finite normal squares and distinct float32 core/cutoff values.");
            }
            Rules[index] = new()
            {
                Coefficient = Component(configuration.Matrix.Values[index], "coefficient"),
                Core = core,
                Cutoff = cutoff
            };
            maximum = Math.Max(maximum, cutoff);
        }
        Cutoff = maximum;
        if (parameters.Boundary == BoundaryKind.Periodic)
        {
            if (maximum > Math.Min(Width, Height) / 2)
            {
                throw new NotSupportedException("Float32 periodic cutoffs must remain at most half the shortest float32 extent.");
            }
            // Extra index width protects the 3x3 stencil against rounded division at large cell indices.
            double countX = Math.Floor(Width / (1.25 * maximum));
            double countY = Math.Floor(Height / (1.25 * maximum));
            if (countX > MaximumCellCoordinate || countY > MaximumCellCoordinate)
            {
                throw new NotSupportedException("The GPU periodic grid supports at most 2^20 cells per axis; use CPU for larger extent/range ratios.");
            }
            CellsX = Math.Max(1, (int)countX);
            CellsY = Math.Max(1, (int)countY);
            CellWidth = Width / CellsX;
            CellHeight = Height / CellsY;
        }
        else
        {
            float cell = MathF.ScaleB(1, MathF.ILogB(maximum));
            if (cell < maximum)
            {
                cell *= 2;
            }
            CellWidth = CellHeight = cell;
        }
    }

    internal void ValidateState(ReadOnlySpan<GpuParticle> state)
    {
        foreach (GpuParticle particle in state)
        {
            if (particle.Species >= Configuration.Matrix.SpeciesCount)
            {
                throw new ArgumentException("Every GPU particle requires a rule-matrix species.", nameof(state));
            }
            if (Configuration.Parameters.Boundary != BoundaryKind.Periodic
                && (Math.Abs(particle.X / CellWidth) > MaximumCellCoordinate
                    || Math.Abs(particle.Y / CellHeight) > MaximumCellCoordinate))
            {
                throw new NotSupportedException("The GPU grid supports absolute cell coordinates through 2^20; CPU retains larger finite ranges.");
            }
        }
    }

    internal static float Component(double value, string name)
    {
        float converted = (float)value;
        if (!float.IsFinite(converted) || (value != 0 && !float.IsNormal(converted)))
        {
            throw new NotSupportedException($"GPU {name} must convert to zero or a normal finite float32 value without losing a nonzero value.");
        }
        return converted;
    }

    private static float Positive(double value, string name)
    {
        float converted = Component(value, name);
        if (converted <= 0)
        {
            throw new NotSupportedException($"GPU {name} must remain positive in float32.");
        }
        return converted;
    }
}
