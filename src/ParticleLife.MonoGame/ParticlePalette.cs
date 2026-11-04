using Microsoft.Xna.Framework;

namespace ParticleLife.MonoGame;

/// <summary>Maps species identifiers to presentation colours independently of state and model parameters.</summary>
/// <remarks>
/// Species zero through eleven use a fixed bright palette against the dark viewer background.
/// Higher identifiers repeat that palette. Neither window size nor experiment selection changes
/// the mapping, and colours carry no scientific meaning.
/// </remarks>
internal static class ParticlePalette
{
    private static readonly Color[] Colours =
    [
        new(100, 217, 198),
        new(255, 124, 120),
        new(245, 203, 100),
        new(124, 186, 255),
        new(196, 160, 255),
        new(247, 149, 196),
        new(169, 222, 121),
        new(255, 167, 100),
        new(117, 222, 242),
        new(226, 229, 239),
        new(222, 172, 124),
        new(181, 194, 255)
    ];

    /// <summary>Gets the same colour for a nonnegative species identifier on every frame and run.</summary>
    internal static Color ForSpecies(int species)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(species);
        return Colours[species % Colours.Length];
    }
}
