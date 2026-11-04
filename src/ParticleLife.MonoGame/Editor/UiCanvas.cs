using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ParticleLife.MonoGame.Editor;

/// <summary>Reuses the host font, white pixel and batch for resolution-independent editor drawing.</summary>
internal sealed class UiCanvas(SpriteBatch batch, Texture2D pixel, BitmapFont font)
{
    internal static readonly Color Background = new(16, 22, 32);
    internal static readonly Color Surface = new(25, 35, 47);
    internal static readonly Color Raised = new(35, 48, 62);
    internal static readonly Color Ink = new(226, 234, 242);
    internal static readonly Color Muted = new(150, 170, 188);
    internal static readonly Color Accent = new(100, 217, 198);
    internal static readonly Color Error = new(255, 145, 133);
    internal const float FontSize = 1.5f;
    internal const float CharacterWidth = 9;

    internal void Fill(Rectangle rectangle, Color color) => batch.Draw(pixel, rectangle, color);

    internal void Border(Rectangle rectangle, Color color, int thickness = 1)
    {
        Fill(new Rectangle(rectangle.X, rectangle.Y, rectangle.Width, thickness), color);
        Fill(new Rectangle(rectangle.X, rectangle.Bottom - thickness, rectangle.Width, thickness), color);
        Fill(new Rectangle(rectangle.X, rectangle.Y, thickness, rectangle.Height), color);
        Fill(new Rectangle(rectangle.Right - thickness, rectangle.Y, thickness, rectangle.Height), color);
    }

    internal void Text(string text, int x, int y, Color? color = null, int maxCharacters = int.MaxValue, float size = FontSize) =>
        font.Draw(batch, text, new Vector2(x, y), color ?? Ink, size, maxCharacters);

    internal void Wrapped(string text, Rectangle bounds, Color? color = null)
    {
        int capacity = Math.Max(1, (int)(bounds.Width / CharacterWidth));
        int offset = 0;
        int y = bounds.Y;
        while (offset < text.Length && y + 11 < bounds.Bottom)
        {
            int length = Math.Min(capacity, text.Length - offset);
            if (length < text.Length - offset)
            {
                int space = text.LastIndexOf(' ', offset + length - 1, length);
                if (space > offset)
                {
                    length = space - offset;
                }
            }
            Text(text.Substring(offset, length), bounds.X, y, color);
            offset += length;
            while (offset < text.Length && text[offset] == ' ')
            {
                offset++;
            }
            y += 17;
        }
    }

    internal void Line(Vector2 start, Vector2 end, Color color, float thickness = 1)
    {
        Vector2 difference = end - start;
        batch.Draw(pixel, start, null, color, MathF.Atan2(difference.Y, difference.X), Vector2.Zero,
            new Vector2(difference.Length(), thickness), SpriteEffects.None, 0);
    }
}
