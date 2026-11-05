using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ParticleLife.MonoGame;

/// <summary>Draws the bundled antialiased IBM Plex Sans atlas with proportional glyph advances.</summary>
/// <remarks>
/// The atlas and its metrics are embedded resources, so rendering has no platform font or content
/// pipeline dependency. The host owns this instance and disposes it before its graphics device.
/// Draw inside a SpriteBatch using LinearClamp. Area-filtered mip levels preserve glyph coverage
/// when the high-resolution source is reduced to inspector text. Glyphs retain their case;
/// unavailable characters use a question mark. Atlas loading allocates once; drawing and measuring
/// allocate no buffers.
/// </remarks>
internal sealed class BitmapFont : IDisposable
{
    private const string ResourcePrefix = "ParticleLife.MonoGame.Assets.Fonts.";
    private static readonly FontMetrics Metrics = ReadMetrics();
    private static readonly Dictionary<char, int> Glyphs = CreateGlyphMap();
    private readonly Texture2D atlas;

    /// <summary>Loads the portable font atlas and filters premultiplied glyph coverage for every mip level.</summary>
    internal BitmapFont(GraphicsDevice device)
    {
        using Stream stream = OpenResource("plex-sans-atlas.png");
        using Texture2D source = Texture2D.FromStream(device, stream);
        Color[] pixels = new Color[source.Width * source.Height];
        source.GetData(pixels);
        for (int index = 0; index < pixels.Length; index++)
        {
            pixels[index] = Color.White * (pixels[index].A / 255f);
        }
        atlas = new Texture2D(device, source.Width, source.Height, true, SurfaceFormat.Color);
        try
        {
            int width = source.Width;
            int height = source.Height;
            for (int level = 0; level < atlas.LevelCount; level++)
            {
                atlas.SetData(level, null, pixels, 0, pixels.Length);
                if (level + 1 < atlas.LevelCount)
                {
                    int nextWidth = Math.Max(1, width / 2);
                    int nextHeight = Math.Max(1, height / 2);
                    pixels = ReduceCoverage(pixels, width, height, nextWidth, nextHeight);
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

    /// <summary>Draws proportional text; scale 1.5 corresponds to a fourteen-pixel em.</summary>
    /// <param name="batch">An already begun host-owned batch with linear sampling.</param>
    /// <param name="text">Text to display, preserving case and line breaks.</param>
    /// <param name="position">Top-left line box position in logical screen pixels.</param>
    /// <param name="color">Premultiplied glyph tint including opacity.</param>
    /// <param name="scale">Positive UI scale; the default is a larger display size.</param>
    /// <param name="maxCharacters">Maximum input characters consumed, including line breaks.</param>
    internal void Draw(SpriteBatch batch, string text, Vector2 position, Color color,
        float scale = 2, int maxCharacters = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);
        ArgumentOutOfRangeException.ThrowIfNegative(maxCharacters);
        float factor = RasterScale(scale);
        float x = position.X;
        float y = position.Y;
        int count = Math.Min(text.Length, maxCharacters);
        for (int index = 0; index < count; index++)
        {
            char character = text[index];
            if (character == '\n')
            {
                x = position.X;
                y += LineHeight(scale);
            }
            else if (character != '\r')
            {
                int glyph = GlyphIndex(character);
                Rectangle source = new(glyph % Metrics.Columns * Metrics.CellSize,
                    glyph / Metrics.Columns * Metrics.CellSize, Metrics.CellSize, Metrics.CellSize);
                batch.Draw(atlas, new Vector2(x - Metrics.Padding * factor, y - Metrics.Padding * factor),
                    source, color, 0, Vector2.Zero, factor, SpriteEffects.None, 0);
                x += Metrics.Advances[glyph] * factor;
            }
        }
    }

    /// <summary>Gets the eighteen-pixel line advance at the normal 1.5 UI size.</summary>
    internal static float LineHeight(float scale = 1.5f)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);
        return 12 * scale;
    }

    /// <summary>Gets the widest line's proportional width without constructing a substring.</summary>
    internal static float Measure(ReadOnlySpan<char> text, float scale = 1.5f)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);
        float current = 0;
        float longest = 0;
        foreach (char character in text)
        {
            if (character == '\n')
            {
                longest = Math.Max(longest, current);
                current = 0;
            }
            else if (character != '\r')
            {
                current += Metrics.Advances[GlyphIndex(character)];
            }
        }
        return Math.Max(longest, current) * RasterScale(scale);
    }

    /// <summary>Gets a rounded-up text width for callers needing integer pixel bounds.</summary>
    internal static int MeasureWidth(string text, int scale = 2) => (int)MathF.Ceiling(Measure(text, scale));

    /// <summary>Releases only the atlas owned by this font.</summary>
    public void Dispose() => atlas.Dispose();

    /// <summary>Area-averages coverage in source-pixel space, including fractional edges for odd dimensions.</summary>
    /// <remarks>
    /// Alpha is coverage, so it is averaged directly without a display-gamma transform. Every output
    /// channel equals alpha: the atlas is white text in premultiplied form. Filtering is performed only
    /// at content load; normal text drawing neither resizes images nor creates textures.
    /// </remarks>
    private static Color[] ReduceCoverage(Color[] source, int width, int height, int nextWidth, int nextHeight)
    {
        Color[] result = new Color[nextWidth * nextHeight];
        double scaleX = width / (double)nextWidth;
        double scaleY = height / (double)nextHeight;
        double area = scaleX * scaleY;
        for (int y = 0; y < nextHeight; y++)
        {
            double top = y * scaleY;
            double bottom = (y + 1) * scaleY;
            for (int x = 0; x < nextWidth; x++)
            {
                double left = x * scaleX;
                double right = (x + 1) * scaleX;
                double coverage = 0;
                for (int sourceY = (int)top; sourceY < Math.Ceiling(bottom); sourceY++)
                {
                    double overlapY = Math.Min(bottom, sourceY + 1) - Math.Max(top, sourceY);
                    for (int sourceX = (int)left; sourceX < Math.Ceiling(right); sourceX++)
                    {
                        double overlapX = Math.Min(right, sourceX + 1) - Math.Max(left, sourceX);
                        coverage += source[sourceY * width + sourceX].A * overlapX * overlapY;
                    }
                }
                byte alpha = (byte)Math.Clamp((int)Math.Round(coverage / area), 0, 255);
                result[y * nextWidth + x] = new Color(alpha, alpha, alpha, alpha);
            }
        }
        return result;
    }

    private static float RasterScale(float scale) => scale * (14f / 1.5f) / Metrics.RasterSize;
    private static int GlyphIndex(char character) => Glyphs.TryGetValue(character, out int index) ? index : Glyphs['?'];
    private static Stream OpenResource(string name) => typeof(BitmapFont).Assembly.GetManifestResourceStream(ResourcePrefix + name)
        ?? throw new InvalidOperationException($"The embedded UI font resource is missing: {name}.");

    private static FontMetrics ReadMetrics()
    {
        using Stream stream = OpenResource("plex-sans-metrics.json");
        return JsonSerializer.Deserialize<FontMetrics>(stream) ?? throw new InvalidOperationException("The UI font metrics are empty.");
    }

    private static Dictionary<char, int> CreateGlyphMap()
    {
        Dictionary<char, int> result = new(Metrics.Characters.Length);
        for (int index = 0; index < Metrics.Characters.Length; index++)
        {
            result.Add((char)Metrics.Characters[index], index);
        }
        return result;
    }

    private sealed record FontMetrics(int Columns, int CellSize, int Padding, int RasterSize,
        int[] Characters, float[] Advances);
}
