using System.Globalization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ParticleLife.Core.State;
using ParticleLife.Viewer;

namespace ParticleLife.MonoGame.Editor;

/// <summary>Adapts the retained brush preferences to native pointer/keyboard input and cached drawing resources.</summary>
/// <remarks>Owns presentation geometry and input capture only. No particle observations, GPU readbacks,
/// file operations or simulation clocks are used by hover, layout or drawing. The host consumes the
/// entire opening/dismissal frame and seeds underlying controls before returning canvas ownership.</remarks>
internal sealed partial class RadialBrushMenu(RadialBrushState state, EditorSession editor)
{
    internal static readonly string[] ToolIcons = ["mouse-pointer-2", "hand", "circle-plus", "paintbrush", "circle-minus", "magnet", "expand", "wind"];
    private readonly List<Control> controls = [];
    private RadialBrushLayout layout = RadialBrushLayout.Create(new Rectangle(0, 0, 800, 600), new Vector2(400, 300));
    private Rectangle viewport;
    private Point previousPointer;
    private ButtonState previousLeft;
    private int previousWheel;
    private double dragAngle;
    private double dragFraction;
    private double dragSweep;
    private int keyboardIndex = -1;
    private Control? hovered;
    private bool ignoreLeftUntilRelease;
    private float contextProgress;
    private RadialBrushParameter? wheelParameter;
    private double wheelReadoutSeconds;
    private int wheelTool;
    private RadialBrushParameter? contextParameter;
    private int contextTool;
    private RadialBrushParameter? WheelParameter => wheelTool == state.FocusedTool ? wheelParameter : null;
    private RadialBrushParameter? ShownParameter => state.EditingParameter ?? WheelParameter ?? contextParameter;
    private Control? DescribedControl => keyboardIndex >= 0 && keyboardIndex < controls.Count ? controls[keyboardIndex] : hovered;
    internal RadialBrushState State => state;
    internal Vector2d WorldAnchor { get; private set; }
    internal bool IsOpen => state.IsOpen;
    internal bool IsVisible => state.IsVisible;
    internal RadialBrushLayout Layout => layout;
    /// <summary>Gets the accessible name of the keyboard target, also used by managed input acceptance checks.</summary>
    internal string? FocusedControlName => keyboardIndex >= 0 && keyboardIndex < controls.Count ? controls[keyboardIndex].Name : null;

    internal void Open(Rectangle available, Point anchor, Vector2d worldAnchor, MouseState mouse)
    {
        viewport = available;
        layout = RadialBrushLayout.Create(available, anchor.ToVector2());
        WorldAnchor = worldAnchor;
        previousPointer = new Point(mouse.X, mouse.Y);
        previousLeft = mouse.LeftButton;
        previousWheel = mouse.ScrollWheelValue;
        ignoreLeftUntilRelease = mouse.LeftButton == ButtonState.Pressed;
        keyboardIndex = -1;
        wheelParameter = null;
        contextParameter = null;
        contextTool = state.ActiveTool;
        wheelReadoutSeconds = 0;
        state.Open();
        RebuildControls();
    }

    /// <summary>Explicit toggles retain completed settings and cancel an unfinished draft before closing.</summary>
    internal void Close() { state.Close(); keyboardIndex = -1; }

    internal void LoseFocus() { state.Close(); ignoreLeftUntilRelease = true; }

    /// <summary>Consumes all input while open, including validation failures and dismissing presses.</summary>
    internal void Update(MouseState mouse, IReadOnlyList<UiInputEvent> input, double elapsed, Rectangle available)
    {
        state.ReconcileSpecies(editor.Configuration.Matrix.SpeciesCount);
        state.Tick(elapsed);
        wheelReadoutSeconds = Math.Max(0, wheelReadoutSeconds - elapsed);
        if (wheelReadoutSeconds == 0) { wheelParameter = null; }
        if (available != viewport)
        {
            viewport = available;
            layout = RadialBrushLayout.Create(available, layout.Anchor);
        }
        if (!state.IsOpen) { return; }
        Point point = mouse.Position;
        bool pressed = mouse.LeftButton == ButtonState.Pressed && previousLeft == ButtonState.Released;
        bool released = mouse.LeftButton == ButtonState.Released && previousLeft == ButtonState.Pressed;
        int wheel = mouse.ScrollWheelValue - previousWheel;
        previousWheel = mouse.ScrollWheelValue;
        previousLeft = mouse.LeftButton;
        if (mouse.LeftButton == ButtonState.Released) { ignoreLeftUntilRelease = false; }
        if (ignoreLeftUntilRelease) { pressed = false; }
        bool moved = point != previousPointer;
        previousPointer = point;
        RebuildControls();
        if (moved) { keyboardIndex = -1; }
        if (keyboardIndex < 0 && !state.IsDragging && !state.EditingNumeric && !(layout.Compact && state.CompactDetails))
        {
            // Outer controls and the intervening radial gap retain context. Brief crossings of
            // another wedge only start the dwell timer, and never change the active brush.
            int tool = layout.HitTool(point);
            state.Hover(tool, elapsed);
        }
        RebuildControls();
        hovered = Hit(point);
        DescribeControl(DescribedControl);
        contextProgress = (float)state.ContextVisibility;
        if (wheel != 0 && !state.IsDragging && !state.EditingNumeric && state.FocusedTool > 0)
        {
            RadialBrushParameter? target = hovered is { Kind: ControlKind.Parameter or ControlKind.Numeric or ControlKind.Direction or ControlKind.Radius } valueControl
                ? valueControl.Parameter : hovered is null && layout.Distance(point) < layout.HubRadius ? RadialBrushParameter.Radius : null;
            if (target is { } parameter)
            {
                AdjustWheel(parameter, wheel);
                wheelParameter = parameter;
                contextParameter = parameter;
                wheelTool = state.FocusedTool;
                wheelReadoutSeconds = 0.7;
            }
        }
        if (pressed)
        {
            if (hovered is null && (!layout.Bounds.Contains(point) || layout.Distance(point) > layout.OuterOuter + 5))
            {
                Close();
                return;
            }
            if (state.EditingNumeric && !layout.NumericBounds.Contains(point) && hovered is not { Kind: ControlKind.Cancel or ControlKind.Apply })
            {
                if (!CommitNumeric()) { return; }
            }
            if (hovered is not null) { Press(hovered.Value, point); }
        }
        if (state.IsDragging)
        {
            if (moved && (mouse.LeftButton == ButtonState.Pressed || released))
            {
                if (state.EditingParameter == RadialBrushParameter.Direction)
                {
                    Control dial = controls.First(control => control.Kind == ControlKind.Direction);
                    Vector2 delta = point.ToVector2() - dial.Center;
                    if (delta.LengthSquared() >= 9) { state.DragValue(Math.Atan2(delta.Y, delta.X) * 180 / Math.PI); }
                }
                else if (state.EditingParameter is { } parameter)
                {
                    double angle = layout.Angle(point);
                    double delta = (angle - dragAngle + 540) % 360 - 180;
                    dragAngle = angle;
                    // Accumulate small angular changes, so crossing the polar seam or an endpoint
                    // never jumps from the maximum to the minimum while pointer capture is held.
                    if (delta != 0)
                    {
                        dragFraction = Math.Clamp(dragFraction + delta / dragSweep, 0, 1);
                        state.DragValue(RadialBrushLayout.ParameterValue(parameter, dragFraction));
                    }
                }
            }
            if (released) { state.EndDrag(); }
        }
        foreach (UiInputEvent entry in input)
        {
            if (!state.IsOpen) { break; }
            if (state.EditingNumeric)
            {
                NumericInput(entry);
                continue;
            }
            if (entry.IsText) { continue; }
            if (entry.Key == Keys.Escape)
            {
                if (!state.CancelEdit()) { Close(); }
            }
            else if (entry.Key == Keys.F8) { state.ReducedMotion = !state.ReducedMotion; }
            else if (entry.Control && entry.Key is Keys.Z or Keys.Y) { History(entry.Key == Keys.Y); }
            else if (entry.Key == Keys.Tab) { MoveFocus(entry.Shift ? -1 : 1); }
            else if (entry.Key is Keys.Left or Keys.Up or Keys.Right or Keys.Down)
            {
                int delta = entry.Key is Keys.Left or Keys.Up ? -1 : 1;
                if (keyboardIndex >= 0 && keyboardIndex < controls.Count && controls[keyboardIndex].Kind is ControlKind.Parameter or ControlKind.Radius or ControlKind.Direction)
                {
                    RadialBrushParameter parameter = controls[keyboardIndex].Parameter;
                    double value = state.Value(parameter);
                    state.SetParameter(parameter, parameter == RadialBrushParameter.Direction ? value + delta
                        : parameter == RadialBrushParameter.ParticlesPerSample ? Math.Clamp(value + delta, 1, 4096)
                        : parameter == RadialBrushParameter.Strength && value == 0 ? Math.Max(0, delta * 0.1) : value * Math.Pow(1.05, delta));
                }
                else { MoveFocus(delta); }
            }
            else if (entry.Key is Keys.Enter or Keys.Space)
            {
                if (keyboardIndex < 0) { keyboardIndex = Math.Min(state.FocusedTool, controls.Count - 1); }
                Control control = controls[Math.Clamp(keyboardIndex, 0, controls.Count - 1)];
                if (control.Kind == ControlKind.Tool && entry.Key == Keys.Enter)
                {
                    state.FocusTool(control.Tool); state.ActivateFocused(); Close();
                }
                else if (control.Kind is ControlKind.Parameter or ControlKind.Radius or ControlKind.Direction) { state.BeginNumeric(control.Parameter); }
                else { Press(control, control.Center.ToPoint()); }
            }
        }
        RebuildControls();
        DescribeControl(DescribedControl);
    }

    /// <summary>Routes wheel samples to the hovered curved value control, preserving exact values beyond convenience ranges.</summary>
    private void AdjustWheel(RadialBrushParameter parameter, int delta)
    {
        double value = state.Value(parameter);
        double notches = delta / 120.0;
        if (parameter == RadialBrushParameter.Radius) { state.Wheel(delta); return; }
        double next = parameter switch
        {
            RadialBrushParameter.Strength => Math.Max(0, (value + 0.1) * Math.Pow(1.12, notches) - 0.1),
            RadialBrushParameter.ParticlesPerSample => Math.Clamp(value + Math.CopySign(Math.Max(1, Math.Round(value * Math.Abs(Math.Pow(1.12, notches) - 1))), notches), 1, 4096),
            _ => value + notches * 5
        };
        if (double.IsFinite(next)) { state.SetParameter(parameter, next); }
    }

    private void NumericInput(UiInputEvent entry)
    {
        if (entry.IsText) { if (!entry.Control) { state.NumericText(entry.Character); } return; }
        if (entry.Key == Keys.Escape) { state.CancelEdit(); }
        else if (entry.Key == Keys.Enter) { CommitNumeric(); }
        else if (entry.Key == Keys.Tab) { if (CommitNumeric()) { MoveFocus(entry.Shift ? -1 : 1); } }
        else if (entry.Control && entry.Key == Keys.C) { NativeClipboard.Write(state.NumericDraft); }
        else if (entry.Control && entry.Key == Keys.X)
        {
            NativeClipboard.Write(state.NumericDraft);
            state.NumericKey(Keys.A, true);
            state.NumericKey(Keys.Delete, false);
        }
        else if (entry.Control && entry.Key == Keys.V)
        {
            foreach (char character in NativeClipboard.Read()) { state.NumericText(character); }
        }
        else { state.NumericKey(entry.Key, entry.Control); }
    }

    private void MoveFocus(int delta)
    {
        RebuildControls();
        keyboardIndex = (keyboardIndex + delta + controls.Count) % controls.Count;
        Control control = controls[keyboardIndex];
        if (control.Kind == ControlKind.Tool) { state.FocusTool(control.Tool); }
        DescribeControl(control);
    }

    private void DescribeControl(Control? control)
    {
        if (state.EditingNumeric || state.IsDragging) { return; }
        if (control is { Kind: ControlKind.Apply or ControlKind.Cancel }) { return; }
        if (control is { Kind: ControlKind.Parameter or ControlKind.Numeric or ControlKind.Direction or ControlKind.Radius } value)
        {
            contextParameter = value.Parameter;
        }
        else if (control is not null)
        {
            contextParameter = null;
            wheelParameter = null;
        }
    }

    private void History(bool redo)
    {
        // Invalid text blocks the action; completed captured changes end before history dispatch.
        if (!CommitNumeric()) { return; }
        state.EndDrag();
        editor.EndGesture();
        if (redo) { if (editor.CanRedo) { editor.Redo(); } }
        else if (editor.CanUndo) { editor.Undo(); }
    }

    private void Press(Control control, Point point)
    {
        switch (control.Kind)
        {
            case ControlKind.Tool: state.FocusTool(control.Tool); state.ActivateFocused(layout.Compact); break;
            case ControlKind.Undo: History(false); break;
            case ControlKind.Redo: History(true); break;
            case ControlKind.Shape: state.SetShape((BrushShape)control.Tool); break;
            case ControlKind.Numeric: if (!state.EditingNumeric) { state.BeginNumeric(control.Parameter); } break;
            case ControlKind.Radius:
            case ControlKind.Parameter:
                BeginArcDrag(control, point); break;
            case ControlKind.Direction:
                state.BeginDrag(control.Parameter); break;
            case ControlKind.Apply: CommitNumeric(); break;
            case ControlKind.Cancel: state.CancelEdit(); break;
            case ControlKind.Species: state.SelectSpecies(control.Tool, control.Destination); break;
            case ControlKind.Page: state.PageSpecies(control.Tool, control.Destination); break;
            case ControlKind.Back: state.Back(); keyboardIndex = -1; break;
            case ControlKind.Motion: state.ReducedMotion = !state.ReducedMotion; break;
        }
        RebuildControls();
    }

    private bool CommitNumeric()
    {
        RadialBrushParameter? parameter = state.EditingParameter;
        if (!state.TryCommitNumeric()) { return false; }
        if (parameter.HasValue) { contextParameter = parameter; }
        return true;
    }

    /// <summary>Captures a bounded clockwise track; exact values outside its convenience range survive until movement.</summary>
    private void BeginArcDrag(Control control, Point point)
    {
        state.BeginDrag(control.Parameter);
        dragAngle = layout.Angle(point);
        dragSweep = control.Kind == ControlKind.Radius ? 270 : control.Sweep - 2;
        double start = control.Kind == ControlKind.Radius ? 135 : control.Start + 1;
        double value = state.Value(control.Parameter);
        dragFraction = RadialBrushLayout.ParameterFraction(control.Parameter, value);
        if (value >= RadialBrushLayout.ParameterValue(control.Parameter, 0) && value <= RadialBrushLayout.ParameterValue(control.Parameter, 1))
        {
            dragFraction = Math.Clamp((dragAngle - start + 720) % 360 / dragSweep, 0, 1);
            state.DragValue(RadialBrushLayout.ParameterValue(control.Parameter, dragFraction));
        }
    }

    private void RebuildControls()
    {
        if (wheelTool != state.FocusedTool) { wheelParameter = null; wheelReadoutSeconds = 0; }
        if (contextTool != state.FocusedTool) { contextParameter = null; contextTool = state.FocusedTool; }
        controls.Clear();
        if (!(layout.Compact && state.CompactDetails))
        {
            for (int tool = 0; tool < 8; tool++)
            {
                controls.Add(new(ControlKind.Tool, layout.ToolPoint(tool), Tool: tool, Name: RadialBrushState.ToolNames[tool]));
            }
        }
        else
        {
            controls.Add(new(ControlKind.Back, new Vector2(layout.Bounds.Right - 28, layout.Bounds.Bottom - 14), Name: "Back to tools"));
        }
        if (state.EditingNumeric)
        {
            controls.Add(new(ControlKind.Apply, layout.ApplyBounds.Center.ToVector2(), Name: "Apply value", Bounds: layout.ApplyBounds));
            controls.Add(new(ControlKind.Cancel, layout.CancelBounds.Center.ToVector2(), Name: "Cancel edit", Bounds: layout.CancelBounds));
        }
        else
        {
            controls.Add(new(ControlKind.Undo, layout.UndoBounds.Center.ToVector2(), Name: "Undo · Ctrl+Z", Bounds: layout.UndoBounds));
            controls.Add(new(ControlKind.Redo, layout.RedoBounds.Center.ToVector2(), Name: "Redo · Ctrl+Y", Bounds: layout.RedoBounds));
        }
        controls.Add(new(ControlKind.Motion, new Vector2(layout.Center.X, layout.Bounds.Top + 8), Name: state.ReducedMotion ? "Motion: reduced · F8" : "Motion: full · F8",
            Bounds: new Rectangle((int)layout.Center.X - 68, layout.Bounds.Top - 5, 136, 20)));
        if (state.FocusedTool == 0) { return; }
        controls.Add(new(ControlKind.Radius, layout.PolarPoint(layout.RadiusArc, -90), Parameter: RadialBrushParameter.Radius, Name: "Radius [L]"));
        controls.Add(new(ControlKind.Numeric, layout.NumericBounds.Center.ToVector2(), Parameter: ShownParameter ?? RadialBrushParameter.Radius,
            Name: "Enter exact value", Bounds: layout.NumericBounds));
        controls.Add(new(ControlKind.Shape, layout.CircleBounds.Center.ToVector2(), Tool: 0, Name: "Circle brush", Bounds: layout.CircleBounds));
        controls.Add(new(ControlKind.Shape, layout.SquareBounds.Center.ToVector2(), Tool: 1, Name: "Square brush · radius is half-width", Bounds: layout.SquareBounds));
        if (layout.Compact && !state.CompactDetails) { return; }
        if (state.FocusedTool == 3)
        {
            SpeciesArc(-165, 150, false);
            SpeciesArc(15, 150, true);
        }
        else
        {
            SpeciesArc(-165, 150, state.FocusedTool == 2);
            // Finish before the upper species arc begins at 195 degrees, including its rim.
            if (state.FocusedTool == 2) { Parameter(103, 84, RadialBrushParameter.ParticlesPerSample); }
            if (state.FocusedTool >= 5) { Parameter(103, 84, RadialBrushParameter.Strength); }
            if (state.FocusedTool == 7) { Parameter(15, 75, RadialBrushParameter.Direction); }
        }
    }

    private void SpeciesArc(double start, double sweep, bool destination)
    {
        int page = destination ? state.DestinationPage : state.AffectedPage;
        int first = page * RadialBrushState.SpeciesPerPage;
        int count = Math.Min(RadialBrushState.SpeciesPerPage, state.SpeciesCount - first);
        bool paged = state.SpeciesCount > RadialBrushState.SpeciesPerPage;
        int slots = count + (destination ? 0 : 1) + (paged ? 2 : 0);
        float radius = (layout.OuterInner + layout.OuterOuter) / 2;
        int slot = 0;
        if (paged) { Add(ControlKind.Page, -1, "Previous species"); }
        if (!destination) { Add(ControlKind.Species, -1, "Affect: All species"); }
        for (int species = first; species < first + count; species++)
        {
            Add(ControlKind.Species, species, $"{(destination ? state.FocusedTool == 3 ? "Paint as" : "Add species" : "Affect")}: S{species}");
        }
        if (paged) { Add(ControlKind.Page, 1, "Next species"); }
        void Add(ControlKind kind, int species, string name)
        {
            double angle = start + sweep * (++slot - 0.5) / slots;
            controls.Add(new(kind, layout.PolarPoint(radius, angle), Tool: species, Destination: destination, Name: name,
                Start: start, Sweep: sweep));
        }
    }

    private void Parameter(double start, double sweep, RadialBrushParameter parameter)
    {
        float radius = (layout.OuterInner + layout.OuterOuter) / 2;
        Vector2 center = layout.PolarPoint(radius, start + sweep / 2);
        if (parameter == RadialBrushParameter.Direction)
        {
            int side = layout.Compact ? 46 : 58;
            double dialSweep = side / radius * 180 / Math.PI;
            controls.Add(new(ControlKind.Direction, center - new Vector2(0, 6), Parameter: parameter, Name: "Direction",
                Start: start + sweep / 2 - dialSweep / 2, Sweep: dialSweep));
            Rectangle degrees = new((int)center.X - side / 2 + 2, (int)center.Y + 11, side - 4, 18);
            controls.Add(new(ControlKind.Numeric, degrees.Center.ToVector2(), Parameter: parameter, Name: "Exact Direction", Bounds: degrees));
            return;
        }
        controls.Add(new(parameter == RadialBrushParameter.Direction ? ControlKind.Direction : ControlKind.Parameter,
            center, Parameter: parameter, Name: RadialBrushState.ParameterName(parameter), Start: start, Sweep: sweep));
        // Separate readout target allows precise entry without changing the radial drag semantics.
        int width = layout.Compact ? 42 : 48;
        Rectangle readout = new((int)center.X - width / 2, (int)center.Y + 1, width, 23);
        readout.X = Math.Clamp(readout.X, viewport.Left, viewport.Right - readout.Width);
        readout.Y = Math.Clamp(readout.Y, viewport.Top, viewport.Bottom - readout.Height);
        controls.Add(new(ControlKind.Numeric, readout.Center.ToVector2(), Parameter: parameter, Name: $"Exact {RadialBrushState.ParameterName(parameter)}", Bounds: readout));
    }

    private Control? Hit(Point point)
    {
        // Compact Back occupies the annulus too and must win over its neighbouring dial.
        foreach (Control control in controls)
        {
            if (control.Kind == ControlKind.Back && Vector2.Distance(point.ToVector2(), control.Center) < 18) { return control; }
        }
        // Readouts, history and shape choices win over the larger annular targets.
        for (int i = controls.Count - 1; i >= 0; i--)
        {
            Control control = controls[i];
            if (control.Bounds.Width > 0 && control.Bounds.Contains(point)) { return control; }
            if (control.Kind == ControlKind.Tool && layout.HitTool(point) == control.Tool) { return control; }
            if (control.Kind == ControlKind.Radius && layout.HitRadius(point)) { return control; }
            if (control.Kind is ControlKind.Species or ControlKind.Page or ControlKind.Back && Vector2.Distance(point.ToVector2(), control.Center) < (layout.Compact ? 14 : 18)) { return control; }
            if (control.Kind is ControlKind.Parameter or ControlKind.Direction && layout.SectorContains(point, layout.OuterInner, layout.OuterOuter, control.Start, control.Sweep)) { return control; }
        }
        return null;
    }

    internal void Draw(UiCanvas canvas, SpriteBatch batch, LucideIcons icons)
    {
        if (!state.IsVisible) { return; }
        float opacity = (float)state.Visibility;
        float progress = (float)state.Visibility;
        float expansion = state.ReducedMotion ? 1 : state.IsOpen
            ? 1 + 2.2f * MathF.Pow(progress - 1, 3) + 1.2f * MathF.Pow(progress - 1, 2)
            : progress;
        Vector2 origin = layout.Anchor + (layout.Center - layout.Anchor) * Math.Min(1, progress * 2);
        if (state.ReducedMotion) { origin = layout.Center; }
        Vector2 Map(Vector2 point) => origin + (point - layout.Center) * expansion;
        Rectangle MapBox(Rectangle box)
        {
            Vector2 at = Map(new Vector2(box.X, box.Y));
            return new Rectangle((int)at.X, (int)at.Y, Math.Max(1, (int)(box.Width * expansion)), Math.Max(1, (int)(box.Height * expansion)));
        }
        Color surface = UiCanvas.Surface * (0.97f * opacity);
        Disk(canvas, origin, layout.HubRadius * expansion, UiCanvas.Background * (0.98f * opacity));
        for (int destination = 0; destination < 2; destination++)
        {
            Control arc = controls.Find(control => control.Kind == ControlKind.Species && control.Destination == (destination == 1));
            if (arc.Kind == ControlKind.Species)
            {
                Sector(canvas, origin, layout.OuterInner * expansion, layout.OuterOuter * expansion,
                    arc.Start - 3, arc.Sweep + 6, UiCanvas.Surface * (0.96f * contextProgress * opacity));
            }
        }
        if (!(layout.Compact && state.CompactDetails))
        {
            for (int tool = 0; tool < 8; tool++)
            {
                bool focused = tool == state.FocusedTool;
                bool active = tool == state.ActiveTool;
                Sector(canvas, origin, layout.ToolInner * expansion, layout.ToolOuter * expansion,
                    RadialBrushLayout.ToolAngle(tool) - 22, 44, focused ? UiCanvas.Raised * opacity : active ? Color.Lerp(UiCanvas.Surface, UiCanvas.Accent, 0.08f) * opacity : surface);
                if (active)
                {
                    Arc(canvas, origin, layout.ToolInner * expansion + 2, RadialBrushLayout.ToolAngle(tool) - 21, 42, UiCanvas.Accent * opacity, 3);
                    Vector2 check = Map(layout.PolarPoint(layout.ToolInner + 10, RadialBrushLayout.ToolAngle(tool) - 13));
                    icons.Draw(batch, "check", Box(check, 14 * expansion), Color.White * opacity);
                }
                if (keyboardIndex >= 0 && keyboardIndex < controls.Count && controls[keyboardIndex] is { Kind: ControlKind.Tool } target && target.Tool == tool)
                {
                    Arc(canvas, origin, layout.ToolOuter * expansion - 2, RadialBrushLayout.ToolAngle(tool) - 21, 42, UiCanvas.Ink * opacity, 3);
                }
                Vector2 point = Map(layout.ToolPoint(tool));
                float side = (layout.Compact ? 24 : 31) * Math.Max(0.01f, expansion);
                icons.Draw(batch, ToolIcons[tool], Box(point, side), (active ? UiCanvas.Accent : focused ? UiCanvas.Ink : UiCanvas.Muted) * opacity);
            }
        }
        if (state.FocusedTool > 0)
        {
            Arc(canvas, origin, layout.RadiusArc * expansion, 135, 270, UiCanvas.Muted * (0.3f * opacity), 3);
            double fraction = RadialBrushLayout.RadiusFraction(state.Radius);
            Arc(canvas, origin, layout.RadiusArc * expansion, 135, 270 * fraction, UiCanvas.Accent * opacity, 3);
            Disk(canvas, Map(layout.PolarPoint(layout.RadiusArc, 135 + 270 * fraction)), 5 * expansion, UiCanvas.Accent * opacity);
            if (ParameterFocused(RadialBrushParameter.Radius))
            {
                Arc(canvas, origin, layout.RadiusArc * expansion, 135, 270, UiCanvas.Accent * (0.5f * opacity), 2);
            }
        }
        foreach (Control control in controls)
        {
            Vector2 point = Map(control.Center);
            bool focus = keyboardIndex >= 0 && keyboardIndex < controls.Count && controls[keyboardIndex] == control || hovered == control;
            Color ink = (focus ? UiCanvas.Ink : UiCanvas.Muted) * opacity;
            switch (control.Kind)
            {
                case ControlKind.Undo:
                case ControlKind.Redo:
                    canvas.Rounded(MapBox(control.Bounds), UiCanvas.Raised * (0.65f * opacity), 5);
                    bool enabled = control.Kind == ControlKind.Undo ? editor.CanUndo : editor.CanRedo;
                    icons.Draw(batch, control.Kind == ControlKind.Undo ? "undo-2" : "redo-2", Box(point, 23 * expansion), ink * (enabled ? 1 : 0.3f));
                    break;
                case ControlKind.Apply:
                case ControlKind.Cancel:
                    canvas.Rounded(MapBox(control.Bounds), (control.Kind == ControlKind.Apply ? Color.Lerp(UiCanvas.Raised, UiCanvas.Accent, 0.22f) : UiCanvas.Raised) * opacity, 5);
                    canvas.TextCentered(control.Kind == ControlKind.Apply ? "Apply" : "Cancel", MapBox(control.Bounds), UiCanvas.Ink * opacity, layout.Compact ? 1.2f : 1.4f);
                    break;
                case ControlKind.Shape:
                    bool chosen = (int)state.Shape == control.Tool;
                    canvas.Rounded(MapBox(control.Bounds), (chosen ? UiCanvas.Accent * 0.2f : UiCanvas.Surface) * opacity, 5);
                    icons.Draw(batch, control.Tool == 0 ? "circle-dashed" : "square-dashed", Box(point, 21 * expansion), (chosen ? UiCanvas.Accent : UiCanvas.Muted) * opacity);
                    break;
                case ControlKind.Species:
                    // Species colours deliberately ignore speed/direction particle colouring.
                    float r = (layout.Compact ? 10 : 13) * expansion;
                    float context = contextProgress * opacity;
                    Vector2 emerged = Map(Vector2.Lerp(layout.ToolPoint(state.FocusedTool), control.Center, state.ReducedMotion ? 1 : contextProgress));
                    Disk(canvas, emerged, r + 2, UiCanvas.Muted * context);
                    if (control.Tool < 0)
                    {
                        for (int part = 0; part < 12; part++)
                        {
                            uint rgb = editor.Appearance.ColorForSpecies(part % state.SpeciesCount);
                            Sector(canvas, emerged, 0, r, part * 30, 31, Rgb(rgb) * context);
                        }
                        Disk(canvas, emerged, r - 2, UiCanvas.Background * context);
                        canvas.TextCentered("All", Box(emerged, r * 2), UiCanvas.Ink * context, layout.Compact ? 1.0f : 1.2f);
                    }
                    else { Disk(canvas, emerged, r, Rgb(editor.Appearance.ColorForSpecies(control.Tool)) * context); }
                    bool selected = control.Tool == (control.Destination ? state.FocusedBrush.Species : state.FocusedBrush.SpeciesFilter);
                    if (selected)
                    {
                        Arc(canvas, emerged, r + 3, 0, 360, UiCanvas.Accent * context, 1.5f);
                        Vector2 check = control.Tool < 0 ? emerged + new Vector2(r * 0.8f, -r * 0.8f) : emerged;
                        float checkSize = control.Tool < 0 ? 12 * expansion : r * 1.8f;
                        Disk(canvas, check, checkSize / 2, UiCanvas.Background * context);
                        icons.Draw(batch, "circle-check", Box(check, checkSize), UiCanvas.Ink * context);
                    }
                    break;
                case ControlKind.Page:
                case ControlKind.Back:
                    if (control.Kind == ControlKind.Back)
                    {
                        Rectangle back = new((int)point.X - 24, (int)point.Y - 12, 48, 24);
                        canvas.Rounded(back, UiCanvas.Raised * opacity, 5);
                        canvas.TextCentered("Back", back, ink, 1.2f);
                    }
                    else
                    {
                        Disk(canvas, point, (layout.Compact ? 11 : 15) * expansion, UiCanvas.Raised * opacity);
                        float sign = control.Tool < 0 ? -1 : 1;
                        canvas.Line(point + new Vector2(-sign * 3, -5), point + new Vector2(sign * 3, 0), ink, 1.5f);
                        canvas.Line(point + new Vector2(sign * 3, 0), point + new Vector2(-sign * 3, 5), ink, 1.5f);
                    }
                    break;
                case ControlKind.Parameter:
                case ControlKind.Direction:
                    bool parameterFocused = ParameterFocused(control.Parameter);
                    Color materialBase = parameterFocused ? Color.Lerp(UiCanvas.Raised, UiCanvas.Accent, 0.12f) : UiCanvas.Raised;
                    Color material = materialBase * (contextProgress * opacity);
                    float inner = layout.OuterInner * expansion;
                    float outer = layout.OuterOuter * expansion;
                    Sector(canvas, origin, inner, outer, control.Start + 1, control.Sweep - 2, material);
                    if (parameterFocused)
                    {
                        Arc(canvas, origin, outer, control.Start + 1, control.Sweep - 2, UiCanvas.Accent * (0.85f * opacity), 1.5f);
                        Arc(canvas, origin, inner, control.Start + 1, control.Sweep - 2, UiCanvas.Accent * (0.5f * opacity), 1.5f);
                    }
                    double value = state.Value(control.Parameter);
                    double amount = control.Kind == ControlKind.Parameter ? RadialBrushLayout.ParameterFraction(control.Parameter, value) : 0;
                    if (control.Kind == ControlKind.Parameter)
                    {
                        // Paint the final material instead of layering translucent radial strokes,
                        // which otherwise produces stripes where neighbouring strokes overlap.
                        Sector(canvas, origin, inner, outer, control.Start + 1, (control.Sweep - 2) * amount,
                            Color.Lerp(materialBase, UiCanvas.Accent, 0.28f) * (contextProgress * opacity));
                        double mark = control.Start + 1 + (control.Sweep - 2) * amount;
                        canvas.Line(Map(layout.PolarPoint(layout.OuterInner + 4, mark)), Map(layout.PolarPoint(layout.OuterOuter - 4, mark)), UiCanvas.Accent * opacity, 2);
                        Disk(canvas, Map(layout.PolarPoint(layout.OuterOuter - 5, mark)), 3.5f * expansion, UiCanvas.Ink * opacity);
                        bool count = control.Parameter == RadialBrushParameter.ParticlesPerSample;
                        canvas.TextCentered(count ? "Count" : "Force", new Rectangle((int)point.X - 24, (int)point.Y - 20, 48, 20), UiCanvas.Ink * opacity, 1.35f);
                    }
                    else
                    {
                        double radians = value * Math.PI / 180;
                        Vector2 end = point + new Vector2((float)Math.Cos(radians), (float)Math.Sin(radians)) * (layout.Compact ? 11 : 14);
                        canvas.Line(point, end, ink, 2);
                        Vector2 direction = Vector2.Normalize(end - point);
                        Vector2 normal = new(-direction.Y, direction.X);
                        canvas.Line(end, end - direction * 6 + normal * 4, ink, 1.5f);
                        canvas.Line(end, end - direction * 6 - normal * 4, ink, 1.5f);
                    }
                    break;
                case ControlKind.Numeric when control.Bounds != layout.NumericBounds:
                    canvas.TextCentered(state.Value(control.Parameter).ToString("G4", CultureInfo.InvariantCulture) + (control.Parameter == RadialBrushParameter.Direction ? "°" : ""), MapBox(control.Bounds), UiCanvas.Ink * opacity, layout.Compact ? 1.3f : 1.5f);
                    break;
                case ControlKind.Motion:
                    if (focus) { canvas.TextCentered(control.Name, MapBox(control.Bounds), ink, 1.05f); }
                    break;
            }
            if (focus && control.Kind is ControlKind.Species or ControlKind.Page)
            {
                Arc(canvas, point, layout.Compact ? 14 : 18, 0, 360, UiCanvas.Ink * opacity, 2);
            }
            else if (focus && control.Kind is ControlKind.Undo or ControlKind.Redo or ControlKind.Shape or ControlKind.Back or ControlKind.Motion or ControlKind.Apply or ControlKind.Cancel)
            {
                canvas.RoundedBorder(control.Bounds.Width > 0 ? MapBox(control.Bounds) : Box(point, layout.Compact ? 26 : 34), UiCanvas.Accent * opacity, 4);
            }
        }
        // Labels identify destination versus filter even when the swatches have similar colours.
        for (int destinationIndex = 0; destinationIndex < 2; destinationIndex++)
        {
            bool destination = destinationIndex == 1;
            Control? first = controls.Find(control => control.Kind == ControlKind.Species && control.Destination == destination);
            if (first is { Kind: ControlKind.Species } species)
            {
                Vector2 point = Map(layout.PolarPoint(layout.OuterInner + 2, species.Start + species.Sweep / 2));
                canvas.TextCentered(destination ? state.FocusedTool == 3 ? "Paint as" : "Add species" : "Affect", new Rectangle((int)point.X - 40, (int)point.Y - 10, 80, 20), UiCanvas.Ink * opacity, 1.3f);
            }
        }
        DrawHub(canvas, origin, expansion, opacity, MapBox(layout.NumericBounds));

        bool ParameterFocused(RadialBrushParameter parameter)
        {
            Control? target = keyboardIndex >= 0 && keyboardIndex < controls.Count ? controls[keyboardIndex] : hovered;
            return state.EditingParameter == parameter || target is { Kind: ControlKind.Parameter or ControlKind.Direction or ControlKind.Numeric or ControlKind.Radius } value && value.Parameter == parameter
                || hovered is { Kind: ControlKind.Parameter or ControlKind.Direction or ControlKind.Numeric or ControlKind.Radius } pointerValue && pointerValue.Parameter == parameter;
        }
    }

    private static Color Rgb(uint rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    private static Rectangle Box(Vector2 center, float side) => new((int)(center.X - side / 2), (int)(center.Y - side / 2), Math.Max(1, (int)side), Math.Max(1, (int)side));
    private static void Disk(UiCanvas canvas, Vector2 center, float radius, Color color) => canvas.Rounded(Box(center, radius * 2), color, Math.Max(0, (int)radius));
    private static void Arc(UiCanvas canvas, Vector2 center, float radius, double start, double sweep, Color color, float thickness)
    {
        int count = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / 3));
        Vector2 previous = Polar(start);
        for (int i = 1; i <= count; i++)
        {
            Vector2 next = Polar(start + sweep * i / count);
            canvas.Line(previous, next, color, thickness); previous = next;
        }
        Vector2 Polar(double angle) => center + new Vector2((float)Math.Cos(angle * Math.PI / 180), (float)Math.Sin(angle * Math.PI / 180)) * radius;
    }
    private static void Sector(UiCanvas canvas, Vector2 center, float inner, float outer, double start, double sweep, Color color)
    {
        if (outer <= inner || sweep <= 0) { return; }
        int count = Math.Max(1, (int)Math.Ceiling(sweep / 1.2));
        for (int i = 0; i <= count; i++)
        {
            double angle = (start + sweep * i / count) * Math.PI / 180;
            Vector2 direction = new((float)Math.Cos(angle), (float)Math.Sin(angle));
            canvas.Line(center + direction * inner, center + direction * outer, color, Math.Max(1, outer * (float)(sweep / count * Math.PI / 180) + 0.7f));
        }
        Arc(canvas, center, outer, start, sweep, UiCanvas.Muted * (color.A / 255f * 0.25f), 1);
    }

    private enum ControlKind { Tool, Undo, Redo, Radius, Shape, Numeric, Parameter, Direction, Species, Page, Back, Motion, Apply, Cancel }
    private readonly record struct Control(ControlKind Kind, Vector2 Center, int Tool = 0, RadialBrushParameter Parameter = RadialBrushParameter.Radius,
        bool Destination = false, string Name = "", Rectangle Bounds = default, double Start = 0, double Sweep = 0);
}
