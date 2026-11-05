using ParticleLife.Core.Numerics;

namespace ParticleLife.Viewer;

/// <summary>Selects the explicit position recipe used only when generating particles.</summary>
public enum PlacementKind
{
    /// <summary>Independent uniform samples over the nominal rectangle.</summary>
    Uniform,
    /// <summary>Centres of a regular rectangular lattice filling the nominal rectangle.</summary>
    Grid,
    /// <summary>Uniform area samples within the largest centred inscribed disk.</summary>
    Disk
}

/// <summary>Selects the explicit initial velocity recipe used only when generating particles.</summary>
public enum VelocityKind
{
    /// <summary>All generated velocities are zero.</summary>
    Zero,
    /// <summary>All velocities have the supplied speed and common direction.</summary>
    UniformDirection,
    /// <summary>Each velocity has the supplied speed and an independently uniform direction.</summary>
    RandomDirection
}

/// <summary>Immutable generation recipe; changing it alone never changes a completed particle state.</summary>
/// <remarks>System.Random seeds record provenance, not a cross-runtime regeneration promise. Generated
/// actual positions and velocities are stored in experiments and structural session events.</remarks>
public sealed record GenerationSettings
{
    /// <summary>Gets the seed for the next explicit generation operation.</summary>
    public int Seed { get; init; } = 1;

    /// <summary>Gets the position distribution for new or resampled particles.</summary>
    public PlacementKind Placement { get; init; }

    /// <summary>Gets the velocity distribution for new or resampled particles.</summary>
    public VelocityKind Velocity { get; init; }

    /// <summary>Gets the finite nonnegative initial speed in L/T.</summary>
    public double Speed { get; init; }

    /// <summary>Gets the finite common direction in radians measured counterclockwise from positive x.</summary>
    public double DirectionRadians { get; init; }

    /// <summary>Validates all recipe fields without generating a state.</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Placement) || !Enum.IsDefined(Velocity))
        {
            throw new ArgumentOutOfRangeException(nameof(Placement), "Unsupported position or velocity recipe.");
        }

        if (!double.IsFinite(Speed) || Speed < 0 || !double.IsFinite(DirectionRadians))
        {
            throw new ArgumentOutOfRangeException(nameof(Speed), "Initial speed must be finite and nonnegative; direction must be finite.");
        }
    }
}

/// <summary>Immutable presentation settings, independent of physical lengths and forces.</summary>
/// <remarks>Colours are 24-bit RGB. The owned palette is copied on construction and colour edits;
/// scalar record copies share immutable palette storage. No setting affects a numerical trajectory.</remarks>
public sealed record AppearanceSettings
{
    private static readonly uint[] DefaultColors = [0xF26D7D, 0x61D6B5, 0x66A8F7, 0xF3CC65, 0xB28AF7, 0xEF9E65, 0x6FD6E5];
    private readonly uint[] colors;

    /// <summary>Creates presentation defaults with an optional owned custom RGB palette.</summary>
    /// <param name="speciesColors">Optional finite palette of RGB values; null uses the default seven colours.</param>
    public AppearanceSettings(IEnumerable<uint>? speciesColors = null)
    {
        colors = speciesColors?.ToArray() ?? (uint[])DefaultColors.Clone();
        Validate();
    }

    /// <summary>Gets a positive finite display radius in unscaled pixels.</summary>
    public double ParticleRadius { get; init; } = 2.5;

    /// <summary>Gets whether sampled diagnostics are visible; visibility does not enable sampling.</summary>
    public bool ShowDiagnostics { get; init; } = true;

    /// <summary>Gets whether the keyboard and interaction help panel is open; hidden by default.</summary>
    public bool ShowHelp { get; init; }

    /// <summary>Gets whether simulation status is shown in the statistics rail.</summary>
    public bool ShowOverlay { get; init; } = true;

    /// <summary>Gets whether the editor panel is visible.</summary>
    public bool ShowEditor { get; init; } = true;

    /// <summary>Gets positive finite interface scale, from 0.5 through 3.</summary>
    public double UiScale { get; init; } = 1;

    /// <summary>Gets read-only owned 24-bit RGB colours without allocating.</summary>
    public ReadOnlySpan<uint> SpeciesColors => colors;

    /// <summary>Returns a colour for any nonnegative species, cycling the configured palette.</summary>
    /// <param name="species">Nonnegative species index.</param>
    /// <returns>A 24-bit RGB value.</returns>
    public uint ColorForSpecies(int species)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(species);
        return colors[species % colors.Length];
    }

    /// <summary>Creates an independent palette with one explicitly selected species colour.</summary>
    /// <param name="species">Nonnegative species index; the palette grows deterministically if needed.</param>
    /// <param name="rgb">24-bit RGB value.</param>
    /// <returns>A presentation record retaining all other settings.</returns>
    public AppearanceSettings WithSpeciesColor(int species, uint rgb)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(species);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rgb, 0xFFFFFFu);
        uint[] replacement = new uint[Math.Max(colors.Length, checked(species + 1))];
        for (int i = 0; i < replacement.Length; i++)
        {
            replacement[i] = ColorForSpecies(i);
        }

        replacement[species] = rgb;
        return new AppearanceSettings(replacement)
        {
            ParticleRadius = ParticleRadius,
            ShowDiagnostics = ShowDiagnostics,
            ShowHelp = ShowHelp,
            ShowOverlay = ShowOverlay,
            ShowEditor = ShowEditor,
            UiScale = UiScale
        };
    }

    /// <summary>Validates display settings at an editor or file boundary.</summary>
    public void Validate()
    {
        if (!double.IsFinite(ParticleRadius) || ParticleRadius <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ParticleRadius), "Display radius must be finite and positive.");
        }

        if (!double.IsFinite(UiScale) || UiScale < 0.5 || UiScale > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(UiScale), "Interface scale must be between 0.5 and 3.");
        }

        if (colors.Length == 0 || colors.Any(color => color > 0xFFFFFFu))
        {
            throw new ArgumentException("A palette must contain at least one 24-bit RGB colour.");
        }
    }
}

/// <summary>Immutable playback and measurement settings, kept separate from scientific configuration.</summary>
public sealed record PlaybackSettings
{
    /// <summary>Gets the finite positive simulation-time to wall-time ratio.</summary>
    public double Speed { get; init; } = 1;

    /// <summary>Gets the positive maximum numerical steps per frame.</summary>
    public int MaxStepsPerFrame { get; init; } = 8;

    /// <summary>Gets measurement cadence in completed steps; zero disables measurements.</summary>
    public int DiagnosticEverySteps { get; init; } = 144;

    /// <summary>Gets connectivity radius in L; null follows the active force cutoff.</summary>
    public double? DiagnosticRadius { get; init; }

    /// <summary>Validates all playback and measurement fields together.</summary>
    public void Validate()
    {
        if (!double.IsFinite(Speed) || Speed <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Speed));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxStepsPerFrame);
        ArgumentOutOfRangeException.ThrowIfNegative(DiagnosticEverySteps);
        if (DiagnosticRadius is double radius && (!double.IsFinite(radius) || radius <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(DiagnosticRadius));
        }
    }

    /// <summary>Captures current playback controls without observing or copying particle state.</summary>
    /// <param name="playback">Controller to inspect.</param>
    /// <returns>Independent immutable playback settings.</returns>
    public static PlaybackSettings Capture(PlaybackController playback) => new()
    {
        Speed = playback.PlaybackSpeed,
        MaxStepsPerFrame = playback.MaxStepsPerFrame,
        DiagnosticEverySteps = playback.DiagnosticEverySteps,
        DiagnosticRadius = playback.DiagnosticRadius
    };
}

/// <summary>Immutable reusable configuration and recipe, without claiming to store an evolved state.</summary>
public sealed class PresetDefinition
{
    private readonly int[] populations;

    /// <summary>Creates a validated preset with exact directed coefficients and separate viewer settings.</summary>
    /// <param name="name">Nonempty preset name.</param>
    /// <param name="configuration">Physical, numerical and execution settings.</param>
    /// <param name="populations">Nonnegative generation count for each matrix species.</param>
    /// <param name="generation">Position and velocity generation recipe.</param>
    /// <param name="appearance">Presentation choices.</param>
    /// <param name="playback">Playback and measurement choices.</param>
    public PresetDefinition(string name, SimulationConfiguration configuration, ReadOnlySpan<int> populations,
        GenerationSettings generation, AppearanceSettings appearance, PlaybackSettings playback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentNullException.ThrowIfNull(playback);
        ValidatePopulations(populations, configuration.Matrix.SpeciesCount);
        generation.Validate();
        appearance.Validate();
        playback.Validate();
        Name = name;
        Configuration = configuration;
        this.populations = populations.ToArray();
        Generation = generation;
        Appearance = appearance;
        Playback = playback;
    }

    /// <summary>Gets the human-readable preset name.</summary>
    public string Name { get; }

    /// <summary>Gets exact physical, matrix, numerical and execution configuration.</summary>
    public SimulationConfiguration Configuration { get; }

    /// <summary>Gets per-species counts for explicit future population generation.</summary>
    public ReadOnlySpan<int> Populations => populations;

    /// <summary>Gets future position and velocity generation settings.</summary>
    public GenerationSettings Generation { get; }

    /// <summary>Gets presentation settings.</summary>
    public AppearanceSettings Appearance { get; }

    /// <summary>Gets playback and measurement settings.</summary>
    public PlaybackSettings Playback { get; }

    internal static void ValidatePopulations(ReadOnlySpan<int> values, int speciesCount)
    {
        if (values.Length != speciesCount)
        {
            throw new ArgumentException("Population counts must have one value for every matrix species.");
        }

        int total = 0;
        foreach (int count in values)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            total = checked(total + count);
        }
    }
}
