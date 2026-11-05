using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using ParticleLife.Core.State;
using ParticleLife.MonoGame.Rendering;
using ParticleLife.Viewer;

/// <summary>Checks native vertex layout and presentation range decisions without loading a graphics device.</summary>
internal static class RenderingChecks
{
    internal static void Run()
    {
        Require(Marshal.SizeOf<ParticleDisplayVertex>() == 12
            && Marshal.OffsetOf<ParticleDisplayVertex>(nameof(ParticleDisplayVertex.Species)).ToInt32() == 8,
            "Native particle vertex layout no longer matches its two attributes.");

        // Normalized display packing must keep ordinary visible geometry in both extreme finite worlds.
        foreach (double extent in new[] { 1.0, 1e-200, 1e200 })
        {
            Vector2d[] positions = [new(0.25 * extent, 0.125 * extent), new(0.75 * extent, 0.375 * extent)];
            Vector2d[] original = (Vector2d[])positions.Clone();
            int[] species = [3, 16_777_217];
            ParticleDisplayVertex[] packed = new ParticleDisplayVertex[positions.Length];
            double magnitude = ParticleDisplayData.Pack(positions, species, extent, packed);
            WorldTransform camera = new(extent, extent / 2, 13, 17, 900, 600);
            ParticleDisplayTransform mapping = ParticleDisplayTransform.Create(extent, extent / 2, camera);
            Require(mapping.SupportsFloatDisplay(magnitude), "A visible normalized finite world unnecessarily lost the native display path.");
            for (int index = 0; index < positions.Length; index++)
            {
                Vector2d reference = camera.ToScreen(positions[index]);
                double x = mapping.OriginX + packed[index].X * mapping.ScaleX;
                double y = mapping.OriginY + packed[index].Y * mapping.ScaleY;
                Require(Math.Abs(x - reference.X) < 0.0002 && Math.Abs(y - reference.Y) < 0.0002,
                    "Packed display coordinates departed from the double camera by more than a subpixel tolerance.");
                Require(positions[index] == original[index] && packed[index].Species == (uint)species[index],
                    "Presentation packing changed source state or quantized an integer species through float.");
            }

            WorldTransform resized = new(extent, extent / 2, 23, 31, 500, 900);
            ParticleDisplayTransform resizedMapping = ParticleDisplayTransform.Create(extent, extent / 2, resized);
            Vector2d resizedReference = resized.ToScreen(positions[1]);
            Require(Math.Abs(resizedMapping.OriginX + packed[1].X * resizedMapping.ScaleX - resizedReference.X) < 0.0002
                && Math.Abs(resizedMapping.OriginY + packed[1].Y * resizedMapping.ScaleY - resizedReference.Y) < 0.0002,
                "A camera-only change required repacking completed particle coordinates.");
        }

        Vector2d[] farPositions = [new(double.MaxValue, 1)];
        ParticleDisplayVertex[] farPacked = new ParticleDisplayVertex[1];
        double farMagnitude = ParticleDisplayData.Pack(farPositions, new[] { 0 }, 1e-200, farPacked);
        WorldTransform tinyCamera = new(1e-200, 1e-200, 0, 0, 800, 600);
        Require(!ParticleDisplayTransform.Create(1e-200, 1e-200, tinyCamera).SupportsFloatDisplay(farMagnitude),
            "An unrepresentable float coordinate was allowed into the native vertex path.");
        Require(farPositions[0].X == double.MaxValue, "Choosing a display fallback clamped a finite simulation coordinate.");
        Require(!new ParticleDisplayTransform(1, 1e9, 0, 800, 800).SupportsFloatDisplay(1),
            "A camera with excessive float cancellation did not request double-mapped rendering.");

        Rectangle clip = new(20, 30, 100, 80);
        Require(ParticleDisplayData.IsVisible(new(18, 60), clip, 3),
            "A particle whose disc crosses the clip edge was incorrectly hidden by centre-only culling.");
        Require(!ParticleDisplayData.IsVisible(new(16, 60), clip, 3)
            && !ParticleDisplayData.IsVisible(new(double.PositiveInfinity, 60), clip, 3),
            "Fully offscreen or unrepresentable raster geometry was not culled.");
        Console.WriteLine("Bulk renderer managed checks: layout, normal/extreme worlds, camera-only mapping, integer species and display clipping passed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
