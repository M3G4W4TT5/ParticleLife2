using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using ParticleLife.Core.State;
using ParticleLife.Viewer;

namespace ParticleLife.MonoGame.Rendering;

/// <summary>Stores presentation-only normalized coordinates and exact species for one native point.</summary>
/// <remarks>The 12-byte layout is independent of the CPU solver's double precision storage.
/// No converted value is written back to the simulation or persisted as scientific state.</remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct ParticleDisplayVertex
{
    internal float X, Y;
    internal uint Species;
}

/// <summary>Maps normalized world coordinates to pixels using the supplied 2D camera.</summary>
/// <remarks>Normalization by the larger world extent permits tiny/large finite CPU worlds. The
/// current and planned pan/zoom cameras are axis-aligned affine mappings. Draw methods derive the
/// camera from a completed frame's configuration, never a newer independently published setting.</remarks>
internal readonly record struct ParticleDisplayTransform(double Extent, double OriginX, double OriginY,
    double ScaleX, double ScaleY)
{
    internal static ParticleDisplayTransform Create(RenderFrameLease frame, WorldTransform transform) =>
        Create(frame.Configuration.Parameters.WorldWidth, frame.Configuration.Parameters.WorldHeight, transform);

    internal static ParticleDisplayTransform Create(double worldWidth, double worldHeight, WorldTransform transform)
    {
        double extent = Math.Max(worldWidth, worldHeight);
        Vector2d origin = transform.ToScreen(default);
        Vector2d horizontal = transform.ToScreen(new Vector2d(extent, 0));
        Vector2d vertical = transform.ToScreen(new Vector2d(0, extent));
        return new(extent, origin.X, origin.Y, horizontal.X - origin.X, vertical.Y - origin.Y);
    }

    /// <summary>Conservatively limits float conversion/cancellation to a fraction of a screen pixel.</summary>
    /// <remarks>A distant finite outlier can choose the slower double-mapped path for the entire frame.
    /// This is deliberate: no simulation membership or finite state is discarded to satisfy display ranges.</remarks>
    internal bool SupportsFloatDisplay(double normalizedMagnitude) =>
        double.IsFinite(ScaleX) && double.IsFinite(ScaleY) && ScaleX > 0 && ScaleY > 0
        && double.IsFinite(OriginX) && double.IsFinite(OriginY)
        && Math.Max(Math.Abs(OriginX), Math.Abs(OriginY)) < 500_000
        && normalizedMagnitude * Math.Max(ScaleX, ScaleY) < 500_000;
}

/// <summary>Provides graphics-free packing and visibility rules shared by the render adapters.</summary>
internal static class ParticleDisplayData
{
    /// <summary>Packs a completed CPU observation without retaining any borrowed span.</summary>
    /// <returns>The largest absolute normalized coordinate, or infinity if float packing is unsupported.</returns>
    internal static double Pack(ReadOnlySpan<Vector2d> positions, ReadOnlySpan<int> species,
        double extent, Span<ParticleDisplayVertex> destination)
    {
        double magnitude = 0;
        for (int index = 0; index < positions.Length; index++)
        {
            double x = positions[index].X / extent;
            double y = positions[index].Y / extent;
            magnitude = Math.Max(magnitude, Math.Max(Math.Abs(x), Math.Abs(y)));
            destination[index] = new ParticleDisplayVertex { X = (float)x, Y = (float)y, Species = (uint)species[index] };
        }
        return magnitude;
    }

    /// <summary>Rejects only non-finite or fully offscreen raster bounds, never simulation particles.</summary>
    internal static bool IsVisible(Vector2d screen, Rectangle clip, float radius) =>
        screen.IsFinite && screen.X >= clip.Left - (double)radius && screen.X <= clip.Right + (double)radius
        && screen.Y >= clip.Top - (double)radius && screen.Y <= clip.Bottom + (double)radius;
}
