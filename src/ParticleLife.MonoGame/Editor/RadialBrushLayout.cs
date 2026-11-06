using Microsoft.Xna.Framework;

namespace ParticleLife.MonoGame.Editor;

/// <summary>Immutable menu geometry in presentation pixels; world anchor is independent of edge placement.</summary>
/// <remarks>The full outer extent is reserved before positioning, so contexts never move the menu.
/// Tool order and orientation remain fixed. Compact detail controls reuse the main ring footprint.</remarks>
internal sealed class RadialBrushLayout
{
    internal Vector2 Anchor { get; }
    internal Vector2 Center { get; }
    internal bool Compact { get; }
    internal float HubRadius { get; }
    internal float RadiusArc { get; }
    internal float ToolInner { get; }
    internal float ToolOuter { get; }
    internal float OuterInner { get; }
    internal float OuterOuter { get; }
    internal Rectangle Bounds { get; }
    internal Rectangle UndoBounds => At(Center.X - 43, Center.Y + (Compact ? 34 : 36), 36, Compact ? 24 : 28);
    internal Rectangle RedoBounds => At(Center.X + 7, Center.Y + (Compact ? 34 : 36), 36, Compact ? 24 : 28);
    internal Rectangle CircleBounds => At(Center.X - 35, Center.Y + RadiusArc - 15, 30, 27);
    internal Rectangle SquareBounds => At(Center.X + 5, Center.Y + RadiusArc - 15, 30, 27);
    internal Rectangle NumericBounds => At(Center.X - HubRadius + 7, Center.Y + 7, (HubRadius - 7) * 2, 25);

    private RadialBrushLayout(Rectangle viewport, Vector2 anchor)
    {
        Anchor = anchor;
        Compact = viewport.Width < 516 || viewport.Height < 516;
        // At the minimum window the compact wheel needs 280 logical pixels, including a small rim.
        float factor = Compact ? Math.Min(1, Math.Min(viewport.Width, viewport.Height) / 284f) : 1;
        HubRadius = (Compact ? 59 : 82) * factor;
        RadiusArc = (Compact ? 74 : 94) * factor;
        ToolInner = (Compact ? 90 : 110) * factor;
        ToolOuter = (Compact ? 132 : 166) * factor;
        OuterInner = Compact ? ToolInner : 175;
        OuterOuter = Compact ? ToolOuter : 225;
        float extent = Compact ? OuterOuter + 7 : 250;
        Center = new Vector2(ClampCentre(anchor.X, viewport.Left, viewport.Right, extent), ClampCentre(anchor.Y, viewport.Top, viewport.Bottom, extent));
        Bounds = At(Center.X - extent, Center.Y - extent, extent * 2, extent * 2);
    }

    internal static RadialBrushLayout Create(Rectangle viewport, Vector2 anchor) => new(viewport, anchor);

    /// <summary>Retains legible compact controls when the inspector's requested scale cannot fit.</summary>
    internal static float EffectiveScale(Rectangle physicalViewport, float requested) =>
        Math.Min(requested, Math.Min(physicalViewport.Width, physicalViewport.Height) / 300f);

    private static float ClampCentre(float coordinate, int first, int last, float extent) => last - first < extent * 2
        ? (first + last) * 0.5f : Math.Clamp(coordinate, first + extent, last - extent);

    internal Vector2 PolarPoint(float radius, double degrees)
    {
        double radians = degrees * Math.PI / 180;
        return Center + new Vector2((float)(Math.Cos(radians) * radius), (float)(Math.Sin(radians) * radius));
    }

    internal Vector2 ToolPoint(int tool) => PolarPoint((ToolInner + ToolOuter) * 0.5f, ToolAngle(tool));
    internal static double ToolAngle(int tool) => -90 + tool * 45;
    internal double Angle(Point point) => Math.Atan2(point.Y - Center.Y, point.X - Center.X) * 180 / Math.PI;
    internal float Distance(Point point) => Vector2.Distance(point.ToVector2(), Center);

    /// <summary>Returns one of eight fixed wedges, or -1 outside the tool annulus.</summary>
    internal int HitTool(Point point)
    {
        float distance = Distance(point);
        if (distance < ToolInner || distance > ToolOuter) { return -1; }
        return (int)Math.Floor(((Angle(point) + 112.5 + 360) % 360) / 45) % 8;
    }

    internal bool HitRadius(Point point) => Math.Abs(Distance(point) - RadiusArc) <= 9 && ArcFraction(point) >= 0;

    /// <summary>Returns clockwise position along the 270-degree arc, with -1 in the bottom gap.</summary>
    internal double ArcFraction(Point point)
    {
        double angle = (Angle(point) - 135 + 720) % 360;
        return angle <= 270 ? angle / 270 : -1;
    }

    internal static double RadiusFraction(double radius) => Math.Clamp(Math.Log(radius / 0.001) / Math.Log(500), 0, 1);
    internal static double RadiusValue(double fraction) => 0.001 * Math.Pow(500, Math.Clamp(fraction, 0, 1));

    internal bool SectorContains(Point point, float inner, float outer, double start, double sweep)
    {
        float distance = Distance(point);
        return distance >= inner && distance <= outer && (Angle(point) - start + 720) % 360 <= sweep;
    }

    private static Rectangle At(float x, float y, float width, float height) => new((int)MathF.Round(x), (int)MathF.Round(y), (int)MathF.Round(width), (int)MathF.Round(height));
}
