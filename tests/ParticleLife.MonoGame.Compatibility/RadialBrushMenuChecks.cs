using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.State;
using ParticleLife.MonoGame.Editor;
using ParticleLife.Viewer;

/// <summary>Routes real native menu pointer and ordered keyboard events without creating a graphics device.</summary>
internal static class RadialBrushMenuChecks
{
    internal static void Run()
    {
        PointerOwnershipAndEdits();
        KeyboardOwnsParkedPointer();
        DraftAndHistory();
        KeyboardAndCompact();
        StandardSpeciesArcs();
        ShortDirectionDial();
        ContextualWheelRouting();
        Console.WriteLine("Radial menu adapter checks: held opening, pointer capture/cancellation, numeric/history validation, keyboard settings and compact Back passed.");
    }

    private static void PointerOwnershipAndEdits()
    {
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 12, 3, 72), diagnosticEverySteps: 0);
        ExperimentEditor owner = new(playback);
        using EditorSession session = new(owner);
        RadialBrushState state = new();
        RadialBrushMenu menu = new(state, session);
        Rectangle available = new(0, 0, 1000, 800);
        Point anchor = new(500, 400);
        menu.Open(available, anchor, new Vector2d(0.8, 0.2), MouseAt(anchor));
        Point attract = menu.Layout.ToolPoint(5).ToPoint();
        Tick(attract, elapsed: 0.08);
        Require(state.FocusedTool == 0, "A short tool crossing changed the focused context.");
        Tick(attract, elapsed: 0.08);
        Require(state.FocusedTool == 5 && state.ActiveTool == 0, "Actual native hover activated its brush.");
        Point strength = menu.Layout.PolarPoint(menu.Layout.OuterInner + 3, 137).ToPoint();
        Tick(strength);
        Tick(strength, pressed: true);
        Require(state.IsDragging && state.ActiveTool == 5, "A contextual strength press failed to capture or activate its previewed parent.");
        Tick(menu.Layout.PolarPoint(menu.Layout.OuterOuter + 45, 137).ToPoint(), pressed: true);
        Require(state.FocusedBrush.Intensity > 2, "Outward native dragging did not increase strength.");
        Tick(menu.Layout.ToolPoint(1).ToPoint(), pressed: true, elapsed: 0.3);
        Require(state.FocusedTool == 5 && state.IsDragging, "A dragged control lost pointer capture across another wedge.");
        Tick(anchor, pressed: true, input: [UiInputEvent.KeyPress(Keys.Escape, false, false)]);
        Require(menu.IsOpen && !state.IsDragging && state.FocusedBrush.Intensity == 2,
            "First Escape did not cancel only the captured edit.");
        Tick(anchor);
        Tick(anchor, input: [UiInputEvent.KeyPress(Keys.Escape, false, false)]);
        Require(!menu.IsOpen && owner.SessionEventCount == 0 && !owner.CanUndo && playback.Observe().Step == 0,
            "Closing/configuring a brush changed particle history or advanced simulation.");

        // An opening press remains owned by the menu until release, even if over a tool wedge.
        state.FocusTool(0); state.ActivateFocused();
        menu.Open(available, anchor, new Vector2d(0.8, 0.2), MouseAt(attract, pressed: true));
        Tick(attract, pressed: true, elapsed: 0.2);
        Require(state.ActiveTool == 0, "A held opening press selected a tool.");
        Tick(attract);
        Tick(attract, pressed: true);
        Require(state.ActiveTool == 5 && menu.IsOpen, "A fresh tool click did not select while retaining the menu.");
        Tick(attract);
        Tick(new Point(5, 5), pressed: true);
        Require(!menu.IsOpen && state.ActiveTool == 5, "Outside dismissal discarded a completed tool selection.");

        // Navigate owns its wheel input but cannot resize a brush; editing contexts can.
        state.FocusTool(0); state.ActivateFocused();
        menu.Open(available, anchor, new Vector2d(0.8, 0.2), MouseAt(anchor));
        menu.Update(MouseAt(anchor, wheel: 120), [], 0.02, available);
        Require(state.Radius == 0.06, "Navigate wheel was routed to radius.");
        state.FocusTool(1);
        menu.Update(MouseAt(anchor, wheel: 240), [], 0.02, available);
        Require(state.Radius > 0.06, "Editing-tool wheel was not routed to radius.");
        Require(menu.WorldAnchor == new Vector2d(0.8, 0.2), "Canvas footprint anchor changed with menu interactions.");
        menu.LoseFocus();
        Require(!menu.IsOpen && !state.IsDragging && !state.EditingNumeric,
            "Focus loss retained a captured menu gesture.");

        void Tick(Point point, bool pressed = false, double elapsed = 0.02, IReadOnlyList<UiInputEvent>? input = null) =>
            menu.Update(MouseAt(point, pressed), input ?? [], elapsed, available);
    }

    private static void DraftAndHistory()
    {
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 12, 3, 73), diagnosticEverySteps: 0);
        ExperimentEditor owner = new(playback);
        using EditorSession session = new(owner);
        session.SetAppearance(session.Appearance with { Camera = session.Appearance.Camera with { Zoom = 2 } });
        Require(session.CanUndo, "History fixture did not create an editor command.");
        RadialBrushState state = new();
        state.FocusTool(5); state.ActivateFocused();
        RadialBrushMenu menu = new(state, session);
        Rectangle available = new(0, 0, 1000, 800);
        Point anchor = new(500, 400);
        menu.Open(available, anchor, new Vector2d(0.5, 0.5), MouseAt(anchor));
        state.BeginNumeric(RadialBrushParameter.Strength);
        Tick(input: [UiInputEvent.Text('-', false, false), UiInputEvent.KeyPress(Keys.Enter, false, false)]);
        Require(state.EditingNumeric && state.ValidationError is not null && state.FocusedBrush.Intensity == 2,
            "Invalid exact draft reached an action or lost input ownership.");
        Tick(input: [UiInputEvent.KeyPress(Keys.Z, true, false)]);
        Require(session.Appearance.Camera.Zoom == 2 && session.CanUndo, "Ctrl+Z escaped focused numeric input into editor history.");
        Point undo = menu.Layout.UndoBounds.Center;
        Tick(undo, pressed: true);
        Require(session.Appearance.Camera.Zoom == 2 && state.EditingNumeric, "Undo button used stale settings after numeric validation failed.");
        Tick(undo);
        Tick(input: [UiInputEvent.KeyPress(Keys.Escape, false, false)]);
        Require(menu.IsOpen && !state.EditingNumeric, "Escape failed to cancel numeric input before closing.");
        Tick(undo, pressed: true);
        Require(session.Appearance.Camera.Zoom == 1 && session.CanRedo && menu.IsOpen,
            "Fixed hub Undo did not dispatch global history and retain the menu.");
        Tick(undo);
        Point redo = menu.Layout.RedoBounds.Center;
        Tick(redo, pressed: true);
        Require(session.Appearance.Camera.Zoom == 2 && menu.IsOpen, "Hub Redo failed to restore inspector history.");
        Tick(redo);
        Tick(input: [UiInputEvent.KeyPress(Keys.Z, true, false)]);
        Require(session.Appearance.Camera.Zoom == 1 && menu.IsOpen, "Menu Ctrl+Z did not retain the global history shortcut.");
        Tick(input: [UiInputEvent.KeyPress(Keys.Y, true, false)]);
        Require(session.Appearance.Camera.Zoom == 2 && menu.IsOpen, "Menu Ctrl+Y did not retain the global history shortcut.");
        state.BeginNumeric(RadialBrushParameter.Strength);
        Tick(input: [UiInputEvent.Text('-', false, false), UiInputEvent.KeyPress(Keys.Enter, false, false)]);
        Tick(new Point(5, 5), pressed: true);
        Require(!menu.IsOpen && !state.EditingNumeric && state.FocusedBrush.Intensity == 2 && session.Appearance.Camera.Zoom == 2,
            "Outside dismissal failed to cancel an invalid numeric draft while retaining completed settings/history.");

        void Tick(Point? point = null, bool pressed = false, IReadOnlyList<UiInputEvent>? input = null) =>
            menu.Update(MouseAt(point ?? anchor, pressed), input ?? [], 0.02, available);
    }

    private static void KeyboardAndCompact()
    {
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 12, 20, 74), diagnosticEverySteps: 0);
        ExperimentEditor owner = new(playback);
        using EditorSession session = new(owner);
        RadialBrushState state = new();
        RadialBrushMenu menu = new(state, session);
        Rectangle available = new(0, 0, 420, 290);
        Point anchor = available.Center;
        foreach (bool paused in new[] { false, true })
        {
            playback.SetPaused(paused);
            menu.Open(available, anchor, new Vector2d(0.3, 0.7), MouseAt(anchor));
            state.PageSpecies(-100, destination: true);
            Require(menu.Layout.Compact, "Minimum-window fixture did not use compact mode.");
            Focus("Add particles");
            Key(Keys.Space);
            Require(state.CompactDetails && menu.IsOpen, "Compact tool selection failed to expose contextual controls.");
            Focus("Particles per sample");
            Key(Keys.Enter);
            Enter("13");
            Require(state.FocusedBrush.AddCount == 13, "Keyboard could not edit compact particles per sample.");
            Focus("Radius [L]"); Key(Keys.Enter); Enter("0.17");
            Focus("Square brush · radius is half-width"); Key(Keys.Space);
            Require(state.Radius == 0.17 && state.Shape == BrushShape.Square, "Compact shared radius/shape were unreachable without a wheel.");
            Focus("Next species"); Key(Keys.Space);
            Require(state.DestinationPage == 1, "Compact keyboard paging did not reveal later species.");
            Focus("Add species: S7"); Key(Keys.Space);
            Require(state.FocusedBrush.Species == 7, "Paged destination species was not keyboard selectable.");
            Focus("Back to tools"); Key(Keys.Enter);
            Require(!state.CompactDetails && menu.IsOpen, "Compact Back did not return to the fixed tool ring.");
            Focus("Paint species"); Key(Keys.Space);
            Focus("Paint as: S7"); Key(Keys.Space);
            Focus("Affect: All species"); Key(Keys.Space);
            Require(state.FocusedBrush.Species == 7 && state.FocusedBrush.SpeciesFilter == -1,
                "Compact paint destination and affected species were conflated.");
            Focus("Back to tools"); Key(Keys.Space);
            Focus("Directional force"); Key(Keys.Space);
            Focus("Strength"); Key(Keys.Enter); Enter("7.25");
            Focus("Direction"); Key(Keys.Enter); Enter("270");
            Require(state.FocusedBrush.Intensity == 7.25 && Math.Abs(state.FocusedBrush.DirectionRadians - 1.5 * Math.PI) < 1e-14,
                "Compact force strength or degree dial exact input was unreachable.");
            Point back = new(menu.Layout.Bounds.Right - 28, menu.Layout.Bounds.Bottom - 14);
            menu.Update(MouseAt(back), [], 0.02, available);
            menu.Update(MouseAt(back, pressed: true), [], 0.02, available);
            menu.Update(MouseAt(back), [], 0.02, available);
            Require(!state.CompactDetails && !state.IsDragging && menu.IsOpen,
                "Compact pointer Back was covered by Direction's neighbouring dial.");
            Focus("Directional force"); Key(Keys.Space);
            Focus("Motion: full · F8"); Key(Keys.Space);
            Require(state.ReducedMotion, "Keyboard reduced-motion preference was unreachable.");
            Key(Keys.F8);
            Require(!state.ReducedMotion, "Reduced-motion shortcut did not toggle its session preference.");
            Focus("Undo · Ctrl+Z");
            Focus("Redo · Ctrl+Y");
            Focus("Back to tools"); Key(Keys.Space);
            Focus("Move"); Key(Keys.Enter);
            Require(!menu.IsOpen && state.ActiveTool == 1 && playback.IsPaused == paused,
                "Enter failed to select/close or menu interactions changed pause state.");
        }
        Require(owner.SessionEventCount == 0 && playback.Observe().Step == 0 && !owner.CanUndo,
            "Keyboard-only brush configuration created particle edits or simulation progress.");

        void Key(Keys key) => menu.Update(MouseAt(anchor), [UiInputEvent.KeyPress(key, false, false)], 0.02, available);
        void Focus(string name)
        {
            for (int count = 0; menu.FocusedControlName != name && count < 256; count++) { Key(Keys.Tab); }
            Require(menu.FocusedControlName == name, $"Keyboard could not reach compact control '{name}'.");
        }
        void Enter(string value)
        {
            Require(state.EditingNumeric, "Keyboard parameter activation did not start exact entry.");
            menu.Update(MouseAt(anchor), value.Select(character => UiInputEvent.Text(character, false, false)).ToArray(), 0.02, available);
            Key(Keys.Enter);
            Require(!state.EditingNumeric && menu.IsOpen, "Enter closed the menu instead of confirming exact input.");
        }
    }

    private static void KeyboardOwnsParkedPointer()
    {
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 12, 3, 75), diagnosticEverySteps: 0);
        ExperimentEditor owner = new(playback);
        using EditorSession session = new(owner);
        RadialBrushState state = new();
        RadialBrushMenu menu = new(state, session);
        Rectangle available = new(0, 0, 1000, 800);
        Point anchor = new(500, 400);
        menu.Open(available, anchor, new Vector2d(0.5, 0.5), MouseAt(anchor));
        Point parked = menu.Layout.ToolPoint(5).ToPoint();
        menu.Update(MouseAt(parked), [], 0.02, available);
        for (int tab = 0; tab < 3; tab++)
        {
            menu.Update(MouseAt(parked), [UiInputEvent.KeyPress(Keys.Tab, false, false)], 0.02, available);
        }
        Require(menu.FocusedControlName == "Add particles" && state.FocusedTool == 2, "Keyboard fixture failed to focus Add particles.");
        for (int frame = 0; frame < 12; frame++) { menu.Update(MouseAt(parked), [], 0.03, available); }
        Require(menu.FocusedControlName == "Add particles" && state.FocusedTool == 2 && state.ActiveTool == 0,
            "A parked pointer over another wedge overrode keyboard focus or activated a brush.");
        Point moved = menu.Layout.ToolPoint(4).ToPoint();
        menu.Update(MouseAt(moved), [], 0.2, available);
        Require(menu.FocusedControlName is null && state.FocusedTool == 4,
            "Actual pointer movement failed to reclaim contextual hover after keyboard navigation.");
    }

    private static void StandardSpeciesArcs()
    {
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 12, 3, 76), diagnosticEverySteps: 0);
        ExperimentEditor owner = new(playback);
        using EditorSession session = new(owner);
        RadialBrushState state = new();
        RadialBrushMenu menu = new(state, session);
        foreach (Rectangle available in new[] { new Rectangle(0, 0, 1000, 800), new Rectangle(0, 0, 420, 290) })
        {
            Point anchor = available.Center;
            for (int tool = 1; tool < 8; tool++)
            {
                state.FocusTool(tool); state.ActivateFocused();
                menu.Open(available, anchor, new Vector2d(0.5, 0.5), MouseAt(anchor));
                if (menu.Layout.Compact) { Click(menu.Layout.ToolPoint(tool).ToPoint()); }
                float radius = (menu.Layout.OuterInner + menu.Layout.OuterOuter) / 2;
                // With three species, Add has three destination swatches; other tools add All first.
                double speciesOneAngle = tool == 2 ? -90 : -71.25;
                Click(menu.Layout.PolarPoint(radius, speciesOneAngle).ToPoint());
                Require(state.ActiveTool == tool && state.FocusedTool == tool
                    && (tool == 2 ? state.FocusedBrush.Species == 1 : state.FocusedBrush.SpeciesFilter == 1),
                    $"Tool {tool} did not use the fixed upper species arc hit positions.");
                if (tool == 3)
                {
                    Click(menu.Layout.PolarPoint(radius, 140).ToPoint());
                    Require(state.FocusedBrush.Species == 2 && state.FocusedBrush.SpeciesFilter == 1,
                        "Paint's lower destination arc changed its upper affected-species selection.");
                }
                if (tool != 2)
                {
                    Click(menu.Layout.PolarPoint(radius, -146.25).ToPoint());
                    Require(state.FocusedBrush.SpeciesFilter == -1,
                        $"Tool {tool} did not use the fixed All-species hit position.");
                }
                menu.Close();
            }

            void Click(Point point)
            {
                menu.Update(MouseAt(point), [], 0.02, available);
                menu.Update(MouseAt(point, pressed: true), [], 0.02, available);
                menu.Update(MouseAt(point), [], 0.02, available);
            }
        }
        Require(owner.SessionEventCount == 0 && !owner.CanUndo, "Selecting standardized species swatches edited particles or history.");
    }

    private static void ShortDirectionDial()
    {
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 12, 3, 77), diagnosticEverySteps: 0);
        ExperimentEditor owner = new(playback);
        using EditorSession session = new(owner);
        foreach (Rectangle available in new[] { new Rectangle(0, 0, 1000, 800), new Rectangle(0, 0, 420, 290) })
        {
            RadialBrushState state = new();
            state.FocusTool(7); state.ActivateFocused();
            RadialBrushMenu menu = new(state, session);
            Point anchor = available.Center;
            menu.Open(available, anchor, new Vector2d(0.5, 0.5), MouseAt(anchor));
            if (menu.Layout.Compact) { state.ActivateFocused(compact: true); }
            Vector2 center = menu.Layout.PolarPoint((menu.Layout.OuterInner + menu.Layout.OuterOuter) / 2, 52.5);
            Vector2 arrowOrigin = center - new Vector2(0, 6);
            Point outside = menu.Layout.PolarPoint((menu.Layout.OuterInner + menu.Layout.OuterOuter) / 2, 15).ToPoint();
            Tick(outside, pressed: true);
            Require(menu.IsOpen && !state.IsDragging && !state.EditingNumeric,
                "Direction's former long sector remained interactive outside the short curved dial.");
            Tick(outside);
            Point top = new((int)arrowOrigin.X, (int)arrowOrigin.Y - 8);
            Tick(top, pressed: true);
            Require(state.IsDragging && state.EditingParameter == RadialBrushParameter.Direction
                && Math.Abs(state.Value(RadialBrushParameter.Direction) - 270) < 5,
                "The short curved direction dial did not capture an upward arrow drag.");
            Point left = new((int)arrowOrigin.X - 100, (int)arrowOrigin.Y);
            Tick(left, pressed: true);
            Require(state.IsDragging && state.FocusedTool == 7 && Math.Abs(state.Value(RadialBrushParameter.Direction) - 180) < 1,
                "Direction dragging lost capture outside its short sector or used the wheel centre as its origin.");
            Tick(left);
            Require(!state.IsDragging, "Direction dial retained capture after pointer release.");
            Point readout = new((int)center.X, (int)center.Y + 20);
            Tick(readout, pressed: true);
            Require(state.EditingNumeric && state.EditingParameter == RadialBrushParameter.Direction,
                "The degree readout below the arrow did not open exact Direction input.");
            Tick(readout);
            menu.Update(MouseAt(readout), "90".Select(character => UiInputEvent.Text(character, false, false)).ToArray(), 0.02, available);
            menu.Update(MouseAt(readout), [UiInputEvent.KeyPress(Keys.Enter, false, false)], 0.02, available);
            Require(!state.EditingNumeric && menu.IsOpen && state.Value(RadialBrushParameter.Direction) == 90
                && Math.Abs(state.FocusedBrush.DirectionRadians - Math.PI / 2) < 1e-14,
                "Short curved dial exact numeric entry lost clockwise degree/radian meaning or closed the menu.");

            void Tick(Point point, bool pressed = false) => menu.Update(MouseAt(point, pressed), [], 0.02, available);
        }
    }

    private static void ContextualWheelRouting()
    {
        using PlaybackController playback = new(PresetGenerator.Create(PresetKind.Random, 12, 3, 78), diagnosticEverySteps: 0);
        ExperimentEditor owner = new(playback);
        using EditorSession session = new(owner);
        RadialBrushState state = new();
        RadialBrushMenu menu = new(state, session);
        Rectangle available = new(0, 0, 1000, 800);
        Point anchor = available.Center;
        int wheel = 0;
        menu.Open(available, anchor, new Vector2d(0.5, 0.5), MouseAt(anchor));
        Wheel(anchor, 120);
        Require(state.Radius == 0.06 && state.ActiveTool == 0, "Navigate wheel changed a brush or selected a tool.");
        state.FocusTool(5);
        Point segment = menu.Layout.PolarPoint(menu.Layout.OuterInner + 3, 137).ToPoint();
        Wheel(segment, 120);
        Require(state.FocusedBrush.Intensity > 2 && state.Radius == 0.06 && state.ActiveTool == 5,
            "Strength segment wheel changed radius or failed to activate its previewed parent.");
        double strength = state.FocusedBrush.Intensity;
        Point strengthReadout = ParameterReadout();
        Wheel(strengthReadout, 120);
        Require(state.FocusedBrush.Intensity > strength && state.Radius == 0.06,
            "Strength numeric readout wheel was routed to the shared radius.");
        Point hub = menu.Layout.NumericBounds.Center;
        Tick(hub, pressed: true);
        Require(state.EditingNumeric && state.EditingParameter == RadialBrushParameter.Strength,
            "Clicking a wheel-updated centre readout edited a different parameter.");
        Tick(hub);
        strength = state.FocusedBrush.Intensity;
        Wheel(strengthReadout, 120);
        Require(state.EditingNumeric && state.FocusedBrush.Intensity == strength && state.Radius == 0.06,
            "Wheel input modified completed values during exact numeric editing.");
        state.CancelEdit();
        state.SetParameter(RadialBrushParameter.Strength, 0);
        Wheel(segment, -120);
        Require(state.FocusedBrush.Intensity == 0, "Strength wheel allowed a negative value.");
        Wheel(segment, 120);
        Require(state.FocusedBrush.Intensity > 0 && state.Radius == 0.06, "Strength wheel could not increase from zero.");
        state.SetParameter(RadialBrushParameter.Strength, 1_000_000);
        Wheel(strengthReadout, 120);
        Require(state.FocusedBrush.Intensity > 1_000_000, "Strength wheel silently clamped an exact value to a convenient drag range.");

        state.FocusTool(2);
        Tick(hub, pressed: true);
        Require(state.EditingNumeric && state.EditingParameter == RadialBrushParameter.Radius,
            "Changing tools retained a stale Strength wheel readout/action in Add particles.");
        state.CancelEdit();
        Tick(hub);
        Point countReadout = ParameterReadout();
        Wheel(countReadout, 120);
        Require(state.FocusedBrush.AddCount == 9 && state.ActiveTool == 2 && state.Radius == 0.06,
            "Particles-per-sample readout wheel changed radius, lost integer increments or failed parent activation.");
        state.SetParameter(RadialBrushParameter.ParticlesPerSample, 1);
        Wheel(countReadout, -120);
        Require(state.FocusedBrush.AddCount == 1, "Count wheel decremented below one.");
        Wheel(countReadout, 1);
        Require(state.FocusedBrush.AddCount == 2, "A high-resolution count wheel tick failed to advance by at least one particle.");
        state.SetParameter(RadialBrushParameter.ParticlesPerSample, 4096);
        Wheel(segment, 120);
        Require(state.FocusedBrush.AddCount == 4096, "Count segment wheel exceeded 4096 particles per sample.");
        Wheel(segment, -120);
        Require(state.FocusedBrush.AddCount is >= 1 and < 4096 && state.Radius == 0.06,
            "Count segment wheel failed to decrement within its supported integer range.");

        state.FocusTool(7);
        Vector2 directionCenter = menu.Layout.PolarPoint((menu.Layout.OuterInner + menu.Layout.OuterOuter) / 2, 52.5);
        Wheel((directionCenter - new Vector2(0, 6)).ToPoint(), 120);
        Require(state.Value(RadialBrushParameter.Direction) == 5 && state.Radius == 0.06,
            "Direction tile wheel failed to advance five clockwise degrees.");
        Wheel(new Point((int)directionCenter.X, (int)directionCenter.Y + 20), 120);
        Require(state.Value(RadialBrushParameter.Direction) == 10 && state.Radius == 0.06,
            "Direction numeric readout wheel changed radius instead of the angle.");
        Wheel(new Point(anchor.X, anchor.Y - 40), 120);
        Require(state.Radius > 0.06 && state.Value(RadialBrushParameter.Direction) == 10,
            "Unclaimed editing-tool wheel input failed to adjust shared radius.");
        Require(owner.SessionEventCount == 0 && !owner.CanUndo, "Brush value wheel configuration edited particle history.");

        Point ParameterReadout()
        {
            Vector2 center = menu.Layout.PolarPoint((menu.Layout.OuterInner + menu.Layout.OuterOuter) / 2, 167.5);
            return new Point((int)center.X, (int)center.Y + 11);
        }
        void Wheel(Point point, int delta)
        {
            wheel += delta;
            menu.Update(MouseAt(point, wheel: wheel), [], 0.02, available);
        }
        void Tick(Point point, bool pressed = false) => menu.Update(MouseAt(point, pressed, wheel), [], 0.02, available);
    }

    private static MouseState MouseAt(Point point, bool pressed = false, int wheel = 0) => new(point.X, point.Y, wheel,
        pressed ? ButtonState.Pressed : ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
