using Microsoft.Xna.Framework;

namespace ParticleLife.MonoGame.Editor;

internal sealed partial class RadialBrushMenu
{
    /// <summary>Names the current control in the hub for both pointer exploration and keyboard focus.</summary>
    internal string HubTitle => ShownParameter is { } parameter
        ? parameter == RadialBrushParameter.ParticlesPerSample ? "Particles / sample" : RadialBrushState.ParameterName(parameter)
        : DescribedControl is { } control ? control.Kind switch
        {
            ControlKind.Species => control.Destination ? state.FocusedTool == 3 ? "Paint as" : "Add species" : "Affect",
            ControlKind.Undo => "Undo",
            ControlKind.Redo => "Redo",
            ControlKind.Shape => "Brush shape",
            ControlKind.Page => "Species",
            ControlKind.Back => "Brush tools",
            ControlKind.Motion => "Menu motion",
            _ => RadialBrushState.ToolNames[state.FocusedTool]
        } : RadialBrushState.ToolNames[state.FocusedTool];

    internal string HubStatus => state.EditingNumeric ? "Exact value" : state.IsDragging ? "Adjusting"
        : ShownParameter.HasValue ? "Adjust"
        : DescribedControl is { Kind: not ControlKind.Tool } ? string.Empty
        : state.FocusedTool == state.ActiveTool ? "Selected" : "Preview";

    /// <summary>Keeps instructions and recoverable errors beside the value instead of in a detached tooltip strip.</summary>
    internal string HubDescription
    {
        get
        {
            if (state.ValidationError is { } error) { return error; }
            if (state.EditingNumeric) { return "Enter applies\nEsc cancels"; }
            if (state.IsDragging) { return state.EditingParameter == RadialBrushParameter.Direction ? "Rotate direction\nEsc cancels" : "Drag around the arc\nEsc cancels"; }
            if (ShownParameter is { } parameter)
            {
                return parameter == RadialBrushParameter.Direction ? "Clockwise · 0° right\nClick value to type" : "Drag arc or scroll\nClick value to type";
            }
            if (DescribedControl is { } control)
            {
                switch (control.Kind)
                {
                    case ControlKind.Species:
                        bool chosen = control.Tool == (control.Destination ? state.FocusedBrush.Species : state.FocusedBrush.SpeciesFilter);
                        return $"{(control.Tool < 0 ? "All species" : $"Species {control.Tool}")}\n{(chosen ? "Selected" : "Click to choose")}";
                    case ControlKind.Undo: return editor.CanUndo ? "Undo last edit\nCtrl+Z" : "Nothing to undo";
                    case ControlKind.Redo: return editor.CanRedo ? "Restore last edit\nCtrl+Y" : "Nothing to redo";
                    case ControlKind.Shape: return control.Tool == 0 ? "Circle footprint" : "Square footprint\nRadius is half-width";
                    case ControlKind.Page:
                        int page = control.Destination ? state.DestinationPage : state.AffectedPage;
                        return $"{(control.Tool < 0 ? "Previous" : "Next")} colours\nPage {page + 1} of {(state.SpeciesCount - 1) / RadialBrushState.SpeciesPerPage + 1}";
                    case ControlKind.Back: return "Return to the tools";
                    case ControlKind.Motion: return state.ReducedMotion ? "Reduced · F8 toggles" : "Full · F8 toggles";
                }
            }
            return state.FocusedTool != state.ActiveTool
                ? $"{RadialBrushState.ToolDescriptions[state.FocusedTool]}\nClick to select"
                : RadialBrushState.ToolDescriptions[state.FocusedTool];
        }
    }

    private void DrawHub(UiCanvas canvas, Vector2 origin, float expansion, float opacity, Rectangle numericBox)
    {
        int width = (int)((layout.HubRadius * 2 - 14) * expansion);
        int left = (int)origin.X - width / 2;
        bool compact = layout.Compact;
        canvas.TextCentered(HubStatus, new Rectangle(left, (int)origin.Y - (compact ? 62 : 74), width, 17), UiCanvas.Accent * opacity, 1.2f);
        canvas.TextCentered(HubTitle, new Rectangle(left, (int)origin.Y - (compact ? 46 : 55), width, 25), UiCanvas.Ink * opacity, compact ? 1.45f : 1.7f);
        DrawHubLines(canvas, HubDescription, new Rectangle(left, (int)origin.Y - (compact ? 24 : 29), width, compact ? 30 : 34),
            (state.ValidationError is null ? UiCanvas.Ink : UiCanvas.Error) * opacity, compact ? 1.2f : 1.35f);
        if (state.FocusedTool == 0) { return; }
        canvas.Rounded(numericBox, UiCanvas.Raised * opacity, 5);
        if (state.EditingNumeric)
        {
            canvas.RoundedBorder(numericBox, (state.ValidationError is null ? UiCanvas.Accent : UiCanvas.Error) * opacity, 4);
            UiDraftText.Draw(canvas, state.NumericDraft, state.NumericCursor, true, state.NumericSelected, numericBox);
        }
        else
        {
            RadialBrushParameter parameter = ShownParameter ?? RadialBrushParameter.Radius;
            string prefix = parameter == RadialBrushParameter.Radius && ShownParameter is null ? "Radius " : string.Empty;
            canvas.TextCentered($"{prefix}{state.DisplayValue(parameter)} {RadialBrushState.Units(parameter)}", numericBox, UiCanvas.Ink * opacity, compact ? 1.3f : 1.5f);
        }
    }

    /// <summary>Wraps hub feedback to two legible lines without shrinking it to fit.</summary>
    private static void DrawHubLines(UiCanvas canvas, string text, Rectangle bounds, Color color, float size)
    {
        int split = text.IndexOf('\n');
        if (split < 0 && canvas.Measure(text, size) > bounds.Width)
        {
            split = text.LastIndexOf(' ');
            while (split > 0 && canvas.Measure(text[..split], size) > bounds.Width) { split = text.LastIndexOf(' ', split - 1); }
        }
        if (split <= 0)
        {
            canvas.TextCentered(text, bounds, color, size);
            return;
        }
        Rectangle line = new(bounds.X, bounds.Y, bounds.Width, bounds.Height / 2);
        canvas.TextCentered(text[..split], line, color, size);
        line.Y += line.Height;
        canvas.TextCentered(text[(split + 1)..], line, color, size);
    }
}
