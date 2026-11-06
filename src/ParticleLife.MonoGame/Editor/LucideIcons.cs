using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ParticleLife.MonoGame.Editor;

/// <summary>Owns the native, offline atlas of the thirteen pinned Lucide brush and history icons.</summary>
/// <remarks>
/// Source SVGs retain Lucide's canonical 24-unit geometry and two-unit round strokes. The checked-in
/// atlas rasterizes those sources at four times that resolution. Loading premultiplies coverage and
/// creates area-filtered mip levels once; drawing only submits an existing texture. The graphics-thread
/// host owns this resource and disposes it before its device. See Assets/Lucide/README.md for provenance.
/// </remarks>
internal sealed class LucideIcons : IDisposable
{
    private const int CellSize = 96;
    private static readonly string[] Names = ["mouse-pointer-2", "hand", "circle-plus", "paintbrush", "circle-minus",
        "magnet", "expand", "wind", "undo-2", "redo-2", "circle-dashed", "square-dashed", "circle-check"];
    private static readonly Dictionary<string, int> Indices = Names.Select((name, index) => (name, index))
        .ToDictionary(pair => pair.name, pair => pair.index, StringComparer.Ordinal);
    private readonly Texture2D atlas;

    /// <summary>Loads embedded icon coverage and creates reusable premultiplied native drawing resources.</summary>
    internal LucideIcons(GraphicsDevice device)
    {
        using Stream stream = typeof(LucideIcons).Assembly.GetManifestResourceStream(
            "ParticleLife.MonoGame.Assets.Lucide.lucide-atlas.png")
            ?? throw new InvalidOperationException("The bundled Lucide atlas is missing.");
        using Texture2D source = Texture2D.FromStream(device, stream);
        if (source.Width != Names.Length * CellSize || source.Height != CellSize)
        {
            throw new InvalidOperationException("The bundled Lucide atlas does not match its icon manifest.");
        }
        Color[] coverage = new Color[source.Width * source.Height];
        source.GetData(coverage);
        for (int index = 0; index < coverage.Length; index++)
        {
            coverage[index] = Color.White * (coverage[index].A / 255f);
        }
        atlas = new Texture2D(device, source.Width, source.Height, true, SurfaceFormat.Color);
        try
        {
            int width = source.Width, height = source.Height;
            for (int level = 0; level < atlas.LevelCount; level++)
            {
                atlas.SetData(level, null, coverage, 0, coverage.Length);
                if (level + 1 < atlas.LevelCount)
                {
                    int nextWidth = Math.Max(1, width / 2), nextHeight = Math.Max(1, height / 2);
                    coverage = ReduceCoverage(coverage, width, height, nextWidth, nextHeight);
                    width = nextWidth;
                    height = nextHeight;
                }
            }
        }
        catch
        {
            atlas.Dispose();
            throw;
        }
    }

    /// <summary>Returns the fixed clockwise tool icon, starting with Navigate only at the top.</summary>
    internal static string ForTool(int tool)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tool);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tool, 7);
        return Names[tool];
    }

    /// <summary>Returns a canonical icon's atlas rectangle; unknown names fail visibly during development.</summary>
    internal static Rectangle SourceBounds(string name) => new(Indices[name] * CellSize, 0, CellSize, CellSize);

    /// <summary>Draws one icon inside an already-begun linear-sampled batch without loading or allocating assets.</summary>
    /// <param name="batch">The host's current batch and coordinate transform.</param>
    /// <param name="name">One of the exact bundled Lucide names.</param>
    /// <param name="bounds">Destination square in the batch's coordinate space.</param>
    /// <param name="tint">Premultiplied colour, including any presentation fade.</param>
    internal void Draw(SpriteBatch batch, string name, Rectangle bounds, Color tint) =>
        batch.Draw(atlas, bounds, SourceBounds(name), tint);

    /// <summary>Releases the graphics-thread-owned atlas.</summary>
    public void Dispose() => atlas.Dispose();

    /// <summary>Area-averages premultiplied coverage, including odd dimensions, for smooth scaled icon strokes.</summary>
    private static Color[] ReduceCoverage(Color[] source, int width, int height, int nextWidth, int nextHeight)
    {
        Color[] result = new Color[nextWidth * nextHeight];
        for (int y = 0; y < nextHeight; y++)
        {
            int top = y * height / nextHeight, bottom = (y + 1) * height / nextHeight;
            for (int x = 0; x < nextWidth; x++)
            {
                int left = x * width / nextWidth, right = (x + 1) * width / nextWidth;
                int alpha = 0;
                for (int sy = top; sy < bottom; sy++)
                {
                    for (int sx = left; sx < right; sx++)
                    {
                        alpha += source[sy * width + sx].A;
                    }
                }
                byte average = (byte)((alpha + (right - left) * (bottom - top) / 2) / ((right - left) * (bottom - top)));
                result[y * nextWidth + x] = new Color(average, average, average, average);
            }
        }
        return result;
    }
}
