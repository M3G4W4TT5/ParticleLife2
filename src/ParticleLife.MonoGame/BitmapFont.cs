using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ParticleLife.MonoGame;

/// <summary>Draws a small original 5-by-7 pixel alphabet from one reusable texture atlas.</summary>
/// <remarks>
/// The host owns this font and disposes it before disposing its graphics device. Draw within an
/// active SpriteBatch using PointClamp sampling. Text uses fixed six-pixel cells; lowercase is
/// displayed as uppercase, and unsupported characters use a question mark. No content pipeline,
/// operating-system font, per-frame texture creation or per-character allocation is required.
/// </remarks>
internal sealed class BitmapFont : IDisposable
{
    private const int Columns = 16;
    private const int CellWidth = 6;
    private const int CellHeight = 8;
    private readonly Texture2D atlas;

    /// <summary>Creates and uploads the glyph atlas once on the graphics thread.</summary>
    internal BitmapFont(GraphicsDevice device)
    {
        const int width = Columns * CellWidth;
        const int height = 6 * CellHeight;
        Color[] pixels = new Color[width * height];
        for (int code = 32; code <= 126; code++)
        {
            int cell = code - 32;
            int originX = cell % Columns * CellWidth;
            int originY = cell / Columns * CellHeight;
            ReadOnlySpan<byte> rows = GlyphRows(char.ToUpperInvariant((char)code));
            for (int y = 0; y < rows.Length; y++)
            {
                for (int x = 0; x < 5; x++)
                {
                    if ((rows[y] & (1 << (4 - x))) != 0)
                    {
                        pixels[(originY + y) * width + originX + x] = Color.White;
                    }
                }
            }
        }

        atlas = new Texture2D(device, width, height);
        atlas.SetData(pixels);
    }

    /// <summary>Draws text at a positive pixel scale; newlines begin another nine-unit row.</summary>
    /// <param name="batch">An already begun batch owned by the host.</param>
    /// <param name="text">Text to display; unsupported glyphs become question marks.</param>
    /// <param name="position">Top-left screen location in pixels.</param>
    /// <param name="color">Glyph tint, including alpha.</param>
    /// <param name="scale">Positive pixel magnification, including fractional interface scales.</param>
    /// <param name="maxCharacters">Maximum input characters consumed, including newlines.</param>
    internal void Draw(SpriteBatch batch, string text, Vector2 position, Color color,
        float scale = 2, int maxCharacters = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);
        ArgumentOutOfRangeException.ThrowIfNegative(maxCharacters);
        float x = position.X;
        float y = position.Y;
        int count = Math.Min(text.Length, maxCharacters);
        for (int i = 0; i < count; i++)
        {
            char character = char.ToUpperInvariant(text[i]);
            if (character == '\n')
            {
                x = position.X;
                y += 9 * scale;
                continue;
            }

            if (character != '\r')
            {
                int cell = (character is >= ' ' and <= '~' ? character : '?') - 32;
                Rectangle source = new(cell % Columns * CellWidth, cell / Columns * CellHeight, 5, 7);
                batch.Draw(atlas, new Vector2(x, y), source, color, 0, Vector2.Zero,
                    scale, SpriteEffects.None, 0);
                x += CellWidth * scale;
            }
        }
    }

    /// <summary>Gets the line advance in pixels at the supplied positive integer magnification.</summary>
    internal static int LineHeight(int scale)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);
        return checked(9 * scale);
    }

    /// <summary>Gets the widest line's cell width in pixels, including its final cell spacing.</summary>
    internal static int MeasureWidth(string text, int scale = 2)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);
        int current = 0;
        int longest = 0;
        foreach (char character in text)
        {
            if (character == '\n')
            {
                longest = Math.Max(longest, current);
                current = 0;
            }
            else if (character != '\r')
            {
                current++;
            }
        }

        return checked(Math.Max(longest, current) * CellWidth * scale);
    }

    /// <summary>Releases the atlas; no graphics device or SpriteBatch owned by the host is disposed.</summary>
    public void Dispose() => atlas.Dispose();

    /// <summary>Defines seven rows of five pixels, with the leftmost pixel in bit four.</summary>
    private static ReadOnlySpan<byte> GlyphRows(char character) => character switch
    {
        ' ' => [0, 0, 0, 0, 0, 0, 0],
        'A' => [14, 17, 17, 31, 17, 17, 17],
        'B' => [30, 17, 17, 30, 17, 17, 30],
        'C' => [14, 17, 16, 16, 16, 17, 14],
        'D' => [30, 17, 17, 17, 17, 17, 30],
        'E' => [31, 16, 16, 30, 16, 16, 31],
        'F' => [31, 16, 16, 30, 16, 16, 16],
        'G' => [14, 17, 16, 23, 17, 17, 15],
        'H' => [17, 17, 17, 31, 17, 17, 17],
        'I' => [14, 4, 4, 4, 4, 4, 14],
        'J' => [7, 2, 2, 2, 2, 18, 12],
        'K' => [17, 18, 20, 24, 20, 18, 17],
        'L' => [16, 16, 16, 16, 16, 16, 31],
        'M' => [17, 27, 21, 21, 17, 17, 17],
        'N' => [17, 25, 25, 21, 19, 19, 17],
        'O' => [14, 17, 17, 17, 17, 17, 14],
        'P' => [30, 17, 17, 30, 16, 16, 16],
        'Q' => [14, 17, 17, 17, 21, 18, 13],
        'R' => [30, 17, 17, 30, 20, 18, 17],
        'S' => [15, 16, 16, 14, 1, 1, 30],
        'T' => [31, 4, 4, 4, 4, 4, 4],
        'U' => [17, 17, 17, 17, 17, 17, 14],
        'V' => [17, 17, 17, 17, 17, 10, 4],
        'W' => [17, 17, 17, 21, 21, 27, 17],
        'X' => [17, 17, 10, 4, 10, 17, 17],
        'Y' => [17, 17, 10, 4, 4, 4, 4],
        'Z' => [31, 1, 2, 4, 8, 16, 31],
        '0' => [14, 17, 19, 21, 25, 17, 14],
        '1' => [4, 12, 4, 4, 4, 4, 14],
        '2' => [14, 17, 1, 2, 4, 8, 31],
        '3' => [30, 1, 1, 14, 1, 1, 30],
        '4' => [2, 6, 10, 18, 31, 2, 2],
        '5' => [31, 16, 16, 30, 1, 1, 30],
        '6' => [14, 16, 16, 30, 17, 17, 14],
        '7' => [31, 1, 2, 4, 8, 8, 8],
        '8' => [14, 17, 17, 14, 17, 17, 14],
        '9' => [14, 17, 17, 15, 1, 1, 14],
        '.' => [0, 0, 0, 0, 0, 6, 6],
        ',' => [0, 0, 0, 0, 6, 6, 4],
        ':' => [0, 6, 6, 0, 6, 6, 0],
        ';' => [0, 6, 6, 0, 6, 6, 4],
        '!' => [4, 4, 4, 4, 4, 0, 4],
        '?' => [14, 17, 1, 2, 4, 0, 4],
        '-' => [0, 0, 0, 31, 0, 0, 0],
        '_' => [0, 0, 0, 0, 0, 0, 31],
        '+' => [0, 4, 4, 31, 4, 4, 0],
        '=' => [0, 0, 31, 0, 31, 0, 0],
        '/' => [1, 1, 2, 4, 8, 16, 16],
        '\\' => [16, 16, 8, 4, 2, 1, 1],
        '(' => [2, 4, 8, 8, 8, 4, 2],
        ')' => [8, 4, 2, 2, 2, 4, 8],
        '[' => [14, 8, 8, 8, 8, 8, 14],
        ']' => [14, 2, 2, 2, 2, 2, 14],
        '{' => [3, 4, 4, 8, 4, 4, 3],
        '}' => [24, 4, 4, 2, 4, 4, 24],
        '<' => [1, 2, 4, 8, 4, 2, 1],
        '>' => [16, 8, 4, 2, 4, 8, 16],
        '%' => [25, 25, 2, 4, 8, 19, 19],
        '*' => [0, 17, 10, 31, 10, 17, 0],
        '"' => [10, 10, 10, 0, 0, 0, 0],
        '\'' => [4, 4, 8, 0, 0, 0, 0],
        '|' => [4, 4, 4, 4, 4, 4, 4],
        '#' => [10, 10, 31, 10, 31, 10, 10],
        '@' => [14, 17, 23, 21, 23, 16, 14],
        '&' => [12, 18, 20, 8, 21, 18, 13],
        '$' => [4, 15, 20, 14, 5, 30, 4],
        '^' => [4, 10, 17, 0, 0, 0, 0],
        '~' => [0, 0, 9, 22, 0, 0, 0],
        '`' => [8, 4, 0, 0, 0, 0, 0],
        _ => [14, 17, 1, 2, 4, 0, 4]
    };
}
