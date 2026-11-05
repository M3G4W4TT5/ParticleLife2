namespace ParticleLife.Viewer;

/// <summary>Selects a presentation-only palette recipe.</summary>
public enum PaletteKind
{
    /// <summary>The original seven soft colours.</summary>
    Default,
    /// <summary>A high-contrast categorical palette.</summary>
    HighContrast,
    /// <summary>Warm red, orange and yellow colours.</summary>
    Warm,
    /// <summary>Cool blue, green and violet colours.</summary>
    Cool,
    /// <summary>Evenly spaced saturated hues.</summary>
    Spectrum,
    /// <summary>Seeded independent hues.</summary>
    Random
}

/// <summary>Creates immutable palette values without changing species assignments or simulation data.</summary>
public static class PaletteCatalog
{
    /// <summary>Creates a palette with at least one explicit colour for every species.</summary>
    /// <param name="kind">Palette recipe.</param><param name="speciesCount">Positive species count.</param>
    /// <param name="seed">Provenance seed for random hues.</param><returns>Owned 24-bit RGB colours.</returns>
    public static uint[] Create(PaletteKind kind, int speciesCount, int seed = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(speciesCount);
        if (!Enum.IsDefined(kind)) { throw new ArgumentOutOfRangeException(nameof(kind)); }
        uint[] contrast = [0xE69F00, 0x56B4E9, 0x009E73, 0xF0E442, 0x0072B2, 0xD55E00, 0xCC79A7, 0xEEEEEE];
        AppearanceSettings defaults = new();
        Random random = new(seed);
        uint[] result = new uint[speciesCount];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = kind switch
            {
                PaletteKind.Default => defaults.ColorForSpecies(i),
                PaletteKind.HighContrast => contrast[i % contrast.Length],
                PaletteKind.Warm => Hue(0.02 + (double)i / speciesCount * 0.14),
                PaletteKind.Cool => Hue(0.4 + (double)i / speciesCount * 0.36),
                PaletteKind.Spectrum => Hue((double)i / speciesCount),
                PaletteKind.Random => Hue(random.NextDouble()),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
        }
        return result;
    }
    private static uint Hue(double hue)
    {
        double sector = (hue - Math.Floor(hue)) * 6;
        double secondary = 1 - Math.Abs(sector % 2 - 1);
        (double red, double green, double blue) = (int)sector switch
        {
            0 => (1d, secondary, 0d),
            1 => (secondary, 1d, 0d),
            2 => (0d, 1d, secondary),
            3 => (0d, secondary, 1d),
            4 => (secondary, 0d, 1d),
            _ => (1d, 0d, secondary)
        };
        static uint Channel(double value) => (uint)Math.Round((0.24 + value * 0.7) * 255);
        return Channel(red) << 16 | Channel(green) << 8 | Channel(blue);
    }
}
