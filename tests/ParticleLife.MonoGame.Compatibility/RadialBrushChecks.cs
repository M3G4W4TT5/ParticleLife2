using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ParticleLife.MonoGame.Editor;
using ParticleLife.Viewer;

/// <summary>Exercises actual menu preferences and geometry without a graphics device or particle capture.</summary>
internal static class RadialBrushChecks
{
    internal static void Run()
    {
        SelectionAndHover();
        CapturedAndNumericEdits();
        SpeciesAndPreferences();
        Geometry();
        IconManifest();
        Console.WriteLine("Radial brush managed checks: preview/selection, hysteresis, captured cancellation, wheel ownership, exact numeric validation, species pages and edge/compact geometry passed.");
    }

    private static void SelectionAndHover()
    {
        RadialBrushState state = new();
        Require(!state.BrushEnabled && state.Radius == 0.06 && state.ActiveBrush.Intensity == 2 && state.ActiveBrush.AddCount == 8,
            "Menu defaults changed existing brush semantics.");
        state.Open();
        state.Wheel(120);
        Require(state.Radius == 0.06 && state.ActiveTool == 0, "Navigate wheel resized a brush.");
        state.Hover(5, 0.07);
        Require(state.FocusedTool == 0, "A brief wedge crossing switched the contextual preview.");
        state.Hover(6, 0.07);
        state.Hover(5, 0.08);
        Require(state.FocusedTool == 0, "Unrelated wedge dwell accumulated.");
        state.Hover(5, 0.07);
        Require(state.FocusedTool == 5 && state.ActiveTool == 0 && !state.BrushEnabled,
            "Hover activated the previewed brush.");
        state.Tick(0.06);
        Require(state.ContextVisibility > 0 && state.ContextVisibility < 1, "Context did not emerge using presentation time.");
        state.Hover(-1, 1);
        Require(state.FocusedTool == 5, "Crossing a context gap hid its controls.");
        state.ActivateFocused();
        Require(state.ActiveTool == 5 && state.IsOpen, "Selecting a brush closed its configuration menu.");
        state.Wheel(120);
        Require(state.Radius > 0.06, "Open editing-tool wheel did not adjust radius.");
        state.Close();
        double completedRadius = state.Radius;
        state.Open();
        Require(state.FocusedTool == 5 && state.Radius == completedRadius, "Reopening lost completed preferences.");
        state.ReducedMotion = true;
        state.Tick(0.07);
        Require(state.Visibility == 1, "Reduced-motion reveal did not use its brief fade interval.");
        state.Close();
        state.Tick(0.1);
        Require(state.IsVisible, "Close did not retain its fading presentation.");
        state.Open();
        state.Tick(0.07);
        Require(state.IsOpen && state.Visibility == 1, "Interrupting close did not reverse presentation correctly.");
    }

    private static void CapturedAndNumericEdits()
    {
        RadialBrushState state = new();
        state.Open();
        state.FocusTool(7);
        state.BeginDrag(RadialBrushParameter.Strength);
        state.Drag(36);
        Require(state.Value(RadialBrushParameter.Strength) > 2 && state.IsDragging && state.ActiveTool == 7,
            "Outward captured strength drag did not activate its parent tool.");
        state.Hover(1, 1);
        state.FocusTool(2);
        Require(state.FocusedTool == 7, "Captured dragging changed its parent tool.");
        Require(state.CancelEdit() && state.IsOpen && state.Value(RadialBrushParameter.Strength) == 2,
            "Escape failed to restore an unfinished drag while retaining the menu.");
        Require(!state.CancelEdit(), "A completed cancellation remained captured.");
        state.BeginDrag(RadialBrushParameter.Strength);
        state.Drag(-20);
        state.EndDrag();
        double saved = state.Value(RadialBrushParameter.Strength);
        Require(saved < 2 && !state.CancelEdit() && state.Value(RadialBrushParameter.Strength) == saved,
            "Released drag was incorrectly cancelled.");

        Edit(state, RadialBrushParameter.Radius, "12.345678912345678");
        double exact = double.Parse("12.345678912345678", System.Globalization.CultureInfo.InvariantCulture);
        Require(state.Radius == exact, "Exact radius outside the convenient drag range was clamped or rounded.");
        state.BeginNumeric(RadialBrushParameter.Radius);
        state.NumericText('-');
        Require(!state.TryCommitNumeric() && state.EditingNumeric && state.ValidationError is not null && state.Radius == exact,
            "Invalid draft applied, blurred, or silently used a stale value.");
        state.NumericKey(Keys.A, true);
        foreach (char value in "0") { state.NumericText(value); }
        Require(!state.TryCommitNumeric(), "Zero radius was accepted.");
        state.CancelEdit();
        Require(!state.EditingNumeric && state.ValidationError is null && state.Radius == exact,
            "Cancelling a numeric draft changed the completed setting.");
        Edit(state, RadialBrushParameter.Strength, "0");
        Require(state.FocusedBrush.Intensity == 0, "Nonnegative strength rejected zero.");
        Edit(state, RadialBrushParameter.Direction, "450");
        Require(Math.Abs(state.FocusedBrush.DirectionRadians - Math.PI / 2) < 1e-14 && state.Value(RadialBrushParameter.Direction) == 90,
            "Clockwise degrees did not map to the existing radians representation.");
        Require(!state.SetParameter(RadialBrushParameter.Strength, double.PositiveInfinity)
            && !state.SetParameter(RadialBrushParameter.Strength, -1)
            && !state.SetParameter(RadialBrushParameter.Radius, double.NaN), "Nonfinite or negative brush settings were accepted.");

        state.FocusTool(2);
        Require(!state.SetParameter(RadialBrushParameter.ParticlesPerSample, 8.5)
            && !state.SetParameter(RadialBrushParameter.ParticlesPerSample, 0)
            && !state.SetParameter(RadialBrushParameter.ParticlesPerSample, 4097), "Addition count lost integer/range validation.");
        Edit(state, RadialBrushParameter.ParticlesPerSample, "4096");
        state.BeginDrag(RadialBrushParameter.ParticlesPerSample);
        state.Drag(1000);
        state.EndDrag();
        Require(state.FocusedBrush.AddCount == 4096, "Add drag exceeded the supported per-sample count.");

        state.BeginNumeric(RadialBrushParameter.ParticlesPerSample);
        state.NumericKey(Keys.Home, false);
        state.NumericKey(Keys.Right, false);
        state.NumericKey(Keys.Delete, false);
        state.NumericKey(Keys.Back, false);
        Require(state.NumericDraft == "96", "Numeric cursor editing did not preserve surrounding digits.");
        state.Close();
        Require(!state.EditingNumeric && state.FocusedBrush.AddCount == 4096,
            "Dismissal applied an unfinished numeric draft.");
    }

    private static void SpeciesAndPreferences()
    {
        RadialBrushState state = new();
        state.ReconcileSpecies(31);
        state.Open();
        state.FocusTool(3);
        state.SelectSpecies(29, destination: true);
        state.SelectSpecies(27, destination: false);
        state.SetShape(BrushShape.Square);
        state.SetParameter(RadialBrushParameter.Radius, 0.23);
        state.FocusTool(5);
        Require(state.FocusedBrush.SpeciesFilter == -1 && state.FocusedBrush.Shape == BrushShape.Square && state.Radius == 0.23,
            "Tool-local affected species or shared shape/radius ownership changed.");
        state.SelectSpecies(4, destination: false);
        state.FocusTool(3);
        Require(state.FocusedBrush.Species == 29 && state.FocusedBrush.SpeciesFilter == 27,
            "Paint as and Affect selections were conflated or lost across tools.");
        state.SelectSpecies(-1, destination: true);
        Require(state.FocusedBrush.Species == 29, "Destination allowed the All-species sentinel.");
        state.SelectSpecies(-1, destination: false);
        Require(state.FocusedBrush.SpeciesFilter == -1, "Affected species did not support All.");
        for (int page = 0; page < 100; page++) { state.PageSpecies(1, destination: true); }
        Require(state.DestinationPage == 4, "Paged species could not reach the last active identity or exceeded it.");
        state.PageSpecies(-1, destination: true);
        Require(state.DestinationPage == 3 && state.AffectedPage == 0, "Paint as and Affect shared paging state.");
        state.ReconcileSpecies(3);
        Require(state.FocusedBrush.Species == 2 && state.DestinationPage == 0 && state.FocusedBrush.SpeciesFilter == -1,
            "Reduced species count retained an unavailable selection or page.");
        state.FocusTool(5);
        Require(state.FocusedBrush.SpeciesFilter == -1, "Unavailable tool-local filter was not reconciled.");
        state.ActivateFocused(compact: true);
        Require(state.CompactDetails && state.ActiveTool == 5, "Compact selection did not expose contextual controls.");
        state.Back();
        Require(!state.CompactDetails && state.ActiveTool == 5, "Compact Back lost the active tool.");
    }

    private static void Geometry()
    {
        RadialBrushLayout layout = RadialBrushLayout.Create(new Rectangle(0, 0, 1000, 800), new Vector2(995, 5));
        Require(!layout.Compact && layout.Bounds.Left >= 0 && layout.Bounds.Right <= 1000 && layout.Bounds.Top >= 0 && layout.Bounds.Bottom <= 800,
            "Full contextual extent was not reserved at the canvas edge.");
        Require(layout.Anchor == new Vector2(995, 5) && layout.Center != layout.Anchor,
            "Edge adjustment moved the brush's independent canvas anchor.");
        for (int tool = 0; tool < 8; tool++)
        {
            Require(layout.HitTool(layout.ToolPoint(tool).ToPoint()) == tool, $"Fixed tool {tool} hit testing changed orientation/order.");
        }
        Require(layout.ToolPoint(0).Y < layout.Center.Y && layout.ToolPoint(2).X > layout.Center.X
            && layout.ToolPoint(4).Y > layout.Center.Y && layout.ToolPoint(6).X < layout.Center.X,
            "Tool ring did not start at top and progress clockwise.");
        Require(layout.HitTool(layout.Center.ToPoint()) == -1 && layout.HitTool(new Point(-10, -10)) == -1,
            "Tool hit testing consumed hub/outside positions.");
        Require(layout.HitRadius(layout.PolarPoint(layout.RadiusArc, 180).ToPoint())
            && !layout.HitRadius(layout.PolarPoint(layout.RadiusArc, 90).ToPoint()), "Radius arc bottom gap was not excluded.");
        Require(RadialBrushLayout.RadiusValue(0) < RadialBrushLayout.RadiusValue(1)
            && layout.ArcFraction(layout.PolarPoint(layout.RadiusArc, 270).ToPoint()) > layout.ArcFraction(layout.PolarPoint(layout.RadiusArc, 180).ToPoint()),
            "Clockwise arc travel did not increase radius.");
        RadialBrushLayout compact = RadialBrushLayout.Create(new Rectangle(0, 0, 420, 290), new Vector2(0, 0));
        Require(compact.Compact && compact.Bounds.Left >= 0 && compact.Bounds.Right <= 420
            && compact.Bounds.Top >= 0 && compact.Bounds.Bottom <= 290,
            "Compact wheel clipped at the minimum native window.");
        Require(compact.OuterOuter == compact.ToolOuter, "Compact contexts did not reuse the legible tool footprint.");
        Require(!compact.UndoBounds.Intersects(compact.CircleBounds) && !compact.RedoBounds.Intersects(compact.SquareBounds)
            && !compact.NumericBounds.Intersects(compact.UndoBounds), "Compact history, numeric entry and shape controls overlap.");
        foreach (Rectangle physical in new[] { new Rectangle(0, 0, 420, 340), new Rectangle(0, 0, 960, 340), new Rectangle(0, 0, 1360, 400) })
        {
            foreach (float requested in new[] { 0.5f, 1, 1.26f, 2, 3 })
            {
                float effective = RadialBrushLayout.EffectiveScale(physical, requested);
                Rectangle logical = new(8, 8, (int)(physical.Width / effective) - 16, (int)(physical.Height / effective) - 16);
                Require(effective <= requested && Math.Min(logical.Width, logical.Height) >= 284,
                    "High UI scale did not reserve a legible compact canvas at minimum window height.");
                RadialBrushLayout scaled = RadialBrushLayout.Create(logical, logical.Center.ToVector2());
                Require(!scaled.UndoBounds.Intersects(scaled.CircleBounds) && !scaled.RedoBounds.Intersects(scaled.SquareBounds)
                    && !scaled.NumericBounds.Intersects(scaled.UndoBounds),
                    "Effective menu scaling left numeric/history/shape controls overlapping.");
            }
        }
    }

    private static void Edit(RadialBrushState state, RadialBrushParameter parameter, string value)
    {
        state.BeginNumeric(parameter);
        foreach (char character in value) { state.NumericText(character); }
        Require(state.TryCommitNumeric(), $"Valid numeric value {value} was rejected for {parameter}.");
    }

    private static void IconManifest()
    {
        string[] tools = ["mouse-pointer-2", "hand", "circle-plus", "paintbrush", "circle-minus", "magnet", "expand", "wind"];
        for (int index = 0; index < tools.Length; index++)
        {
            Require(LucideIcons.ForTool(index) == tools[index], "Tool did not use the selected canonical Lucide icon.");
        }
        string[] names = [.. tools, "undo-2", "redo-2", "circle-dashed", "square-dashed", "circle-check", "circle-ellipsis",
            "square-chevron-right", "square-chevron-left"];
        HashSet<Rectangle> regions = [];
        foreach (string name in names)
        {
            Rectangle source = LucideIcons.SourceBounds(name);
            Require(source.Width == 96 && source.Height == 96 && regions.Add(source), "Lucide manifest lost an icon or shared an atlas region.");
        }
        using Stream? atlas = typeof(LucideIcons).Assembly.GetManifestResourceStream("ParticleLife.MonoGame.Assets.Lucide.lucide-atlas.png");
        Require(atlas is not null, "Offline Lucide asset was not embedded in the application.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
