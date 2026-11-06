using Microsoft.Xna.Framework.Input;

namespace ParticleLife.MonoGame.Editor;

/// <summary>Resolves canvas-focused UI scale shortcuts without modifying world or simulation values.</summary>
internal static class UiAccessibility
{
    internal static bool TryAdjustScale(Keys key, double current, out double scale)
    {
        scale = key switch
        {
            Keys.OemPlus or Keys.Add => Math.Clamp(current + 0.1, 0.5, 3),
            Keys.OemMinus or Keys.Subtract => Math.Clamp(current - 0.1, 0.5, 3),
            Keys.D0 or Keys.NumPad0 => 1,
            _ => current
        };
        return key is Keys.OemPlus or Keys.Add or Keys.OemMinus or Keys.Subtract or Keys.D0 or Keys.NumPad0;
    }
}
