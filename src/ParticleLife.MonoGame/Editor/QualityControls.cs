using ParticleLife.Viewer;

namespace ParticleLife.MonoGame.Editor;

/// <summary>Offers manual display presets with descriptive custom-state feedback.</summary>
internal static class QualityControls
{
    internal static void AddTo(UiSection appearance, EditorSession editor)
    {
        appearance.Children.Insert(1, new UiDropdown("Display quality", ["Custom", "Economy", "Balanced", "Luminous"],
            () => VisualQuality.Match(editor.Appearance.Visuals) is VisualQualityPreset preset ? (int)preset + 1 : 0,
            index =>
            {
                if (index == 0) { return; }
                editor.SetAppearance(editor.Appearance with
                {
                    Visuals = VisualQuality.Apply(editor.Appearance.Visuals, (VisualQualityPreset)(index - 1))
                });
            }, "Manual starting points for opacity, blend, trails, glow and spatial observations. Custom keeps your values. Velocity colour, palette and simulation settings stay independent; every value remains editable."));
        appearance.Children.Insert(2, new UiLabel(() => "Economy: effects off. Balanced: subtle glow. Luminous: trails and additive glow. Diagnostics and tracking have their own controls.", 56));
    }
}
