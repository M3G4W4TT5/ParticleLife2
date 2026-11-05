using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ParticleLife.Viewer;

namespace ParticleLife.MonoGame.Editor;

/// <summary>Draws already sampled local thumbnails; Draw never reads files or captures state.</summary>
internal sealed class DiscoveryItemWidget(DiscoveryEntry entry, Action selected, Func<bool> current) : UiWidget(entry.Metadata.PreviewLabel + ". " + entry.Path + (entry.Notice is null ? "" : ". " + entry.Notice))
{
    private readonly string title = (entry.Metadata.Favourite ? "* " : "") + entry.Metadata.Name;
    private readonly string purpose = (entry.Metadata.Kind switch { DiscoveryKind.Pattern => "Captured pattern", DiscoveryKind.Initial => "Initial baseline", DiscoveryKind.Preset => "Reusable preset", _ => "Recorded session" }) + $" / {entry.Metadata.Population:N0} particles";
    private readonly string description = entry.Metadata.Description.Length == 0 ? string.Join(", ", entry.Metadata.Tags) : entry.Metadata.Description;
    internal override int Height => 104;
    internal override void Press(Point point) => selected();
    internal override void Key(Keys key, bool control, bool shift) { if (key is Keys.Enter or Keys.Space) { selected(); } }
    internal override void Draw(UiCanvas canvas)
    {
        canvas.Rounded(Bounds, current() ? UiCanvas.Raised : UiCanvas.Surface, 5);
        Rectangle preview = new(Bounds.X + 7, Bounds.Y + 8, Math.Min(104, Bounds.Width / 3), Bounds.Height - 16);
        canvas.Fill(preview, UiCanvas.Background);
        double aspect = Math.Clamp(entry.Metadata.WorldAspect, 0.1, 10);
        int width = Math.Min(preview.Width, (int)(preview.Height * aspect)), height = Math.Min(preview.Height, (int)(preview.Width / aspect));
        Rectangle world = new(preview.Center.X - width / 2, preview.Center.Y - height / 2, width, height);
        foreach (DiscoveryPoint point in entry.Metadata.Points)
        {
            uint rgb = point.Rgb;
            canvas.Fill(new Rectangle(world.X + (int)(point.X * Math.Max(0, world.Width - 2)), world.Y + (int)(point.Y * Math.Max(0, world.Height - 2)), 2, 2),
                new Color((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        }
        if (entry.Metadata.Points.Length == 0) { canvas.TextFit("No preview", preview, UiCanvas.Muted); }
        int x = preview.Right + 9, available = Math.Max(0, Bounds.Right - x - 7);
        canvas.TextFit(title, new Rectangle(x, Bounds.Y + 6, available, 25));
        canvas.TextFit(purpose, new Rectangle(x, Bounds.Y + 31, available, 20), UiCanvas.Accent, 1.1f);
        canvas.TextFit(entry.Metadata.PreviewLabel, new Rectangle(x, Bounds.Y + 52, available, 20), UiCanvas.Muted, 1.0f);
        canvas.TextFit(description, new Rectangle(x, Bounds.Y + 75, available, 20), UiCanvas.Muted, 1.0f);
        DrawFocus(canvas);
    }
}
