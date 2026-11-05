using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ParticleLife.MonoGame.Editor;

/// <summary>Reuses the host's font, white pixel and batch for compact, resolution-independent UI drawing.</summary>
/// <remarks>Colours use premultiplied alpha. Rounded surfaces use a bounded number of scan lines and
/// no per-frame textures. Glass is a tinted control surface; any backdrop filtering belongs to the host.</remarks>
internal sealed class UiCanvas(SpriteBatch batch, Texture2D pixel, BitmapFont font)
{
    internal static readonly Color Background = new(11, 18, 21);
    internal static readonly Color Surface = new(22, 35, 41);
    internal static readonly Color Raised = new(39, 53, 60);
    internal static readonly Color Ink = new(219, 234, 229);
    internal static readonly Color Muted = new(141, 169, 173);
    internal static readonly Color Accent = new(168, 210, 207);
    internal static readonly Color Error = new(241, 154, 143);
    internal const float FontSize = 1.5f;
    internal const float CharacterWidth = 7;

    internal void Fill(Rectangle rectangle, Color color)
    {
        if (rectangle.Width > 0 && rectangle.Height > 0)
        {
            batch.Draw(pixel, rectangle, color);
        }
    }

    internal void Border(Rectangle rectangle, Color color, int thickness = 1)
    {
        Fill(new Rectangle(rectangle.X, rectangle.Y, rectangle.Width, thickness), color);
        Fill(new Rectangle(rectangle.X, rectangle.Bottom - thickness, rectangle.Width, thickness), color);
        Fill(new Rectangle(rectangle.X, rectangle.Y, thickness, rectangle.Height), color);
        Fill(new Rectangle(rectangle.Right - thickness, rectangle.Y, thickness, rectangle.Height), color);
    }

    /// <summary>Fills a subtly rounded rectangle with fractional edge coverage and no retained texture.</summary>
    internal void Rounded(Rectangle rectangle, Color color, int radius = 5)
    {
        if (rectangle.Width <= 0 || rectangle.Height <= 0)
        {
            return;
        }
        radius = Math.Clamp(radius, 0, Math.Min(rectangle.Width, rectangle.Height) / 2);
        if (radius == 0)
        {
            Fill(rectangle, color);
            return;
        }
        Fill(new Rectangle(rectangle.X, rectangle.Y + radius, rectangle.Width, rectangle.Height - radius * 2), color);
        for (int row = 0; row < radius; row++)
        {
            float distance = radius - row - 0.5f;
            float inset = radius - MathF.Sqrt(radius * radius - distance * distance);
            int solidInset = (int)MathF.Ceiling(inset);
            Color edge = color * (solidInset - inset);
            DrawRow(rectangle.Y + row);
            DrawRow(rectangle.Bottom - row - 1);

            void DrawRow(int y)
            {
                Fill(new Rectangle(rectangle.X + solidInset, y, rectangle.Width - solidInset * 2, 1), color);
                Fill(new Rectangle(rectangle.X + solidInset - 1, y, 1, 1), edge);
                Fill(new Rectangle(rectangle.Right - solidInset, y, 1, 1), edge);
            }
        }
    }

    /// <summary>Draws a one-pixel rounded rim without filling the rectangle's transparent interior.</summary>
    internal void RoundedBorder(Rectangle rectangle, Color color, int radius = 5)
    {
        if (rectangle.Width <= 0 || rectangle.Height <= 0)
        {
            return;
        }
        radius = Math.Clamp(radius, 0, Math.Min(rectangle.Width, rectangle.Height) / 2);
        if (radius == 0)
        {
            Border(rectangle, color);
            return;
        }
        Fill(new Rectangle(rectangle.X + radius, rectangle.Y, rectangle.Width - radius * 2, 1), color);
        Fill(new Rectangle(rectangle.X + radius, rectangle.Bottom - 1, rectangle.Width - radius * 2, 1), color);
        Fill(new Rectangle(rectangle.X, rectangle.Y + radius, 1, rectangle.Height - radius * 2), color);
        Fill(new Rectangle(rectangle.Right - 1, rectangle.Y + radius, 1, rectangle.Height - radius * 2), color);
        for (int row = 0; row < radius; row++)
        {
            float distance = radius - row - 0.5f;
            float inset = radius - MathF.Sqrt(radius * radius - distance * distance);
            int x = (int)MathF.Floor(inset);
            float fraction = inset - x;
            DrawCorners(x, row, color * (1 - fraction));
            DrawCorners(x + 1, row, color * fraction);
        }

        void DrawCorners(int x, int y, Color tint)
        {
            Fill(new Rectangle(rectangle.X + x, rectangle.Y + y, 1, 1), tint);
            Fill(new Rectangle(rectangle.Right - 1 - x, rectangle.Y + y, 1, 1), tint);
            Fill(new Rectangle(rectangle.X + x, rectangle.Bottom - 1 - y, 1, 1), tint);
            Fill(new Rectangle(rectangle.Right - 1 - x, rectangle.Bottom - 1 - y, 1, 1), tint);
        }
    }

    /// <summary>Draws the dark, legible material for floating controls over the host's backdrop.</summary>
    internal void Glass(Rectangle rectangle, int radius = 8)
    {
        Rounded(rectangle, Background * 0.9f, radius);
        RoundedBorder(rectangle, Muted * 0.25f, radius);
        Fill(new Rectangle(rectangle.X + radius, rectangle.Y + 1, rectangle.Width - radius * 2, 1), Ink * 0.035f);
    }

    internal float Measure(string text, float size = FontSize) => BitmapFont.Measure(text, size);

    internal void Text(string text, int x, int y, Color? color = null, int maxCharacters = int.MaxValue, float size = FontSize) =>
        font.Draw(batch, text, new Vector2(x, y), color ?? Ink, size, maxCharacters);

    internal void TextRight(string text, int right, int y, Color? color = null, float size = FontSize) =>
        Text(text, right - (int)MathF.Ceiling(Measure(text, size)), y, color, size: size);

    internal void TextCentered(string text, Rectangle bounds, Color? color = null, float size = FontSize)
    {
        if (Measure(text, size) > bounds.Width)
        {
            TextFit(text, bounds, color, size);
            return;
        }
        Text(text, bounds.X + (int)((bounds.Width - Measure(text, size)) / 2),
            bounds.Y + (int)((bounds.Height - BitmapFont.LineHeight(size)) / 2), color, size: size);
    }

    /// <summary>Fits one vertically centered line by measured advances, adding an ellipsis on truncation.</summary>
    internal void TextFit(string text, Rectangle bounds, Color? color = null, float size = FontSize)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }
        int y = bounds.Y + (int)((bounds.Height - BitmapFont.LineHeight(size)) / 2);
        if (Measure(text, size) <= bounds.Width)
        {
            Text(text, bounds.X, y, color, size: size);
            return;
        }
        float available = bounds.Width - Measure("…", size);
        int count = 0;
        while (count < text.Length && BitmapFont.Measure(text.AsSpan(0, count + 1), size) <= available)
        {
            count++;
        }
        Text(text, bounds.X, y, color, count, size);
        Text("…", bounds.X + (int)MathF.Ceiling(BitmapFont.Measure(text.AsSpan(0, count), size)), y, color, size: size);
    }

    /// <summary>Measures all wrapped lines using exactly the line breaks used by <see cref="Wrapped"/>.</summary>
    /// <remarks>A nonpositive width or empty string has zero height. Explicit blank lines count;
    /// an overlong word is split at glyph boundaries so layout always makes progress.</remarks>
    internal static int MeasureWrappedHeight(string text, int width)
    {
        if (width <= 0)
        {
            return 0;
        }
        int offset = 0;
        int lines = 0;
        while (offset < text.Length)
        {
            WrappedLineLength(text, offset, width, out offset);
            lines++;
        }
        return lines * (int)BitmapFont.LineHeight(FontSize);
    }

    /// <summary>Wraps prose using proportional advances; explicit newlines retain their paragraph break.</summary>
    internal void Wrapped(string text, Rectangle bounds, Color? color = null)
    {
        if (bounds.Width <= 0)
        {
            return;
        }
        int offset = 0;
        int y = bounds.Y;
        int lineHeight = (int)BitmapFont.LineHeight(FontSize);
        while (offset < text.Length && y + lineHeight <= bounds.Bottom)
        {
            int length = WrappedLineLength(text, offset, bounds.Width, out int next);
            Text(text.Substring(offset, length), bounds.X, y, color);
            offset = next;
            y += lineHeight;
        }
    }

    /// <summary>Returns the next drawable line and the first unconsumed input offset for layout and drawing.</summary>
    private static int WrappedLineLength(string text, int offset, int width, out int next)
    {
        int length = 0;
        int lastSpace = -1;
        while (offset + length < text.Length && text[offset + length] != '\n')
        {
            if (BitmapFont.Measure(text.AsSpan(offset, length + 1), FontSize) > width)
            {
                break;
            }
            if (text[offset + length] == ' ')
            {
                lastSpace = length;
            }
            length++;
        }
        bool wrapped = offset + length < text.Length && text[offset + length] != '\n';
        if (wrapped && lastSpace > 0)
        {
            length = lastSpace;
        }
        if (length == 0 && text[offset] != '\n')
        {
            length = 1;
        }
        next = offset + length;
        while (next < text.Length && text[next] == ' ')
        {
            next++;
        }
        if (next < text.Length && text[next] == '\n')
        {
            next++;
        }
        return length;
    }

    internal void Line(Vector2 start, Vector2 end, Color color, float thickness = 1)
    {
        Vector2 difference = end - start;
        batch.Draw(pixel, start, null, color, MathF.Atan2(difference.Y, difference.X), Vector2.Zero,
            new Vector2(difference.Length(), thickness), SpriteEffects.None, 0);
    }
}
