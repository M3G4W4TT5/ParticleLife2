namespace ParticleLife.Viewer;

/// <summary>Selects an explicit starting point for optional display cost and appearance.</summary>
public enum VisualQualityPreset
{
    /// <summary>Disables trails, glow, heatmaps and sampled speed statistics.</summary>
    Economy,
    /// <summary>Adds a subtle glow with ordinary source-over particle blending.</summary>
    Balanced,
    /// <summary>Adds short trails and brighter additive glow.</summary>
    Luminous
}

/// <summary>Applies manual, overridable visual presets without selecting simulation settings.</summary>
/// <remarks>These choices never change population, world, timestep, rules, precision, execution,
/// playback, diagnostic sampling or recorded particle state. Palette, velocity shading/reference
/// speed and camera remain independent. Matching is descriptive; no automatic adaptation occurs.</remarks>
public static class VisualQuality
{
    /// <summary>Copies a visual draft with the selected opacity, blending, effects and spatial-observation settings.</summary>
    /// <param name="current">Validated existing visual choices; velocity shading and its scale are retained.</param>
    /// <param name="preset">Explicit user choice.</param>
    /// <returns>A validated immutable draft whose individual controls remain editable.</returns>
    public static VisualSettings Apply(VisualSettings current, VisualQualityPreset preset)
    {
        ArgumentNullException.ThrowIfNull(current);
        current.Validate();
        VisualSettings result = preset switch
        {
            VisualQualityPreset.Economy => current with
            {
                Opacity = 1, Blend = ParticleBlend.Normal, TrailHalfLifeSeconds = 0,
                GlowStrength = 0, GlowRadiusPixels = 6, Heatmap = HeatmapKind.Off,
                ShowStatistics = false, ObservationSeconds = 2
            },
            VisualQualityPreset.Balanced => current with
            {
                Opacity = 1, Blend = ParticleBlend.Normal, TrailHalfLifeSeconds = 0,
                GlowStrength = 0.35, GlowRadiusPixels = 6, Heatmap = HeatmapKind.Off,
                ShowStatistics = false, ObservationSeconds = 0.5
            },
            VisualQualityPreset.Luminous => current with
            {
                Opacity = 0.8, Blend = ParticleBlend.Additive, TrailHalfLifeSeconds = 0.35,
                GlowStrength = 1.25, GlowRadiusPixels = 10, Heatmap = HeatmapKind.Off,
                ShowStatistics = false, ObservationSeconds = 0.5
            },
            _ => throw new ArgumentOutOfRangeException(nameof(preset))
        };
        result.Validate();
        return result;
    }

    /// <summary>Identifies a preset's controlled values without changing a custom draft.</summary>
    /// <param name="settings">Validated current visual choices.</param>
    /// <returns>The matching preset, or null when individual settings differ.</returns>
    public static VisualQualityPreset? Match(VisualSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        foreach (VisualQualityPreset preset in Enum.GetValues<VisualQualityPreset>())
        {
            if (Apply(settings, preset) == settings) { return preset; }
        }
        return null;
    }
}
