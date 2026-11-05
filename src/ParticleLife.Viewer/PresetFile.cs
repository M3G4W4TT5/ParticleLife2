using System.Text.Json;
using System.Text.Json.Serialization;
using ParticleLife.Core.Experiments;

namespace ParticleLife.Viewer;

/// <summary>Reads and writes the strict versioned reusable-preset format, distinct from actual experiments.</summary>
/// <remarks>All fields are required; unknown and duplicate fields, non-finite values, unsupported
/// versions, invalid populations and enum strings fail before any running editor can be changed.</remarks>
public static class PresetFile
{
    /// <summary>Gets the written preset schema version; legacy schema 1 remains readable.</summary>
    public const int SchemaVersion = 2;

    /// <summary>Serializes exact current rules and a reusable generation recipe.</summary>
    /// <param name="preset">Validated immutable preset.</param>
    /// <returns>Versioned JSON; no actual evolved particle state is implied.</returns>
    public static string Serialize(PresetDefinition preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return JsonSerializer.Serialize(new PresetData("particle-life-preset", SchemaVersion, preset.Name,
            ConfigurationFile.Encode(preset.Configuration), preset.Populations.ToArray(), GenerationData.From(preset.Generation),
            AppearanceData.From(preset.Appearance), PlaybackData.From(preset.Playback)), EditorJson.Options);
    }

    /// <summary>Parses and validates a complete reusable preset without generating particles.</summary>
    /// <param name="json">Strict preset JSON.</param>
    /// <returns>Independent immutable configuration and settings.</returns>
    public static PresetDefinition Deserialize(string json)
    {
        PresetData data = JsonSerializer.Deserialize<PresetData>(json, EditorJson.Options)
            ?? throw new JsonException("A preset must be a JSON object.");
        if (data.DocumentKind != "particle-life-preset" || data.SchemaVersion is not (1 or SchemaVersion))
        {
            throw new NotSupportedException("Unsupported preset document kind or schema version.");
        }

        return new PresetDefinition(data.Name, ConfigurationFile.Decode(data.Configuration, legacy: data.SchemaVersion == 1), data.Populations,
            data.Generation.ToSettings(), data.Appearance.ToSettings(), data.Playback.ToSettings());
    }

    /// <summary>Loads a validated preset from a UTF-8 file.</summary>
    /// <param name="path">Existing preset path.</param>
    /// <returns>A validated preset, without mutating an editor.</returns>
    public static PresetDefinition Load(string path) => Deserialize(File.ReadAllText(path));

    /// <summary>Exclusively creates a preset file; existing files are never overwritten.</summary>
    /// <param name="path">New output path.</param>
    /// <param name="preset">Validated preset.</param>
    public static void SaveNew(string path, PresetDefinition preset) => ExclusiveFileStorage.Save(path, Serialize(preset));

    private sealed record PresetData(string DocumentKind, int SchemaVersion, string Name, JsonElement Configuration,
        int[] Populations, GenerationData Generation, AppearanceData Appearance, PlaybackData Playback);
}

internal static class EditorJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };
}

internal sealed record GenerationData(int Seed, string Placement, string Velocity, double Speed, double DirectionRadians)
{
    internal static GenerationData From(GenerationSettings settings) => new(settings.Seed, settings.Placement.ToString(),
        settings.Velocity.ToString(), settings.Speed, settings.DirectionRadians);

    internal GenerationSettings ToSettings()
    {
        if (!Enum.TryParse(Placement, out PlacementKind placement) || !Enum.IsDefined(placement) || placement.ToString() != Placement
            || !Enum.TryParse(Velocity, out VelocityKind velocity) || !Enum.IsDefined(velocity) || velocity.ToString() != Velocity)
        {
            throw new NotSupportedException("Unsupported generation placement or velocity recipe.");
        }

        GenerationSettings settings = new() { Seed = Seed, Placement = placement, Velocity = velocity, Speed = Speed, DirectionRadians = DirectionRadians };
        settings.Validate();
        return settings;
    }
}

internal sealed record AppearanceData(uint[] SpeciesColors, double ParticleRadius, bool ShowDiagnostics,
    bool ShowHelp, bool ShowOverlay, bool ShowEditor, double UiScale)
{
    internal static AppearanceData From(AppearanceSettings settings) => new(settings.SpeciesColors.ToArray(), settings.ParticleRadius,
        settings.ShowDiagnostics, settings.ShowHelp, settings.ShowOverlay, settings.ShowEditor, settings.UiScale);

    internal AppearanceSettings ToSettings()
    {
        AppearanceSettings settings = new(SpeciesColors)
        {
            ParticleRadius = ParticleRadius,
            ShowDiagnostics = ShowDiagnostics,
            ShowHelp = ShowHelp,
            ShowOverlay = ShowOverlay,
            ShowEditor = ShowEditor,
            UiScale = UiScale
        };
        settings.Validate();
        return settings;
    }
}

internal sealed record PlaybackData(double Speed, int MaxStepsPerFrame, int DiagnosticEverySteps, double? DiagnosticRadius)
{
    internal static PlaybackData From(PlaybackSettings settings) => new(settings.Speed, settings.MaxStepsPerFrame,
        settings.DiagnosticEverySteps, settings.DiagnosticRadius);

    internal PlaybackSettings ToSettings()
    {
        PlaybackSettings settings = new()
        {
            Speed = Speed,
            MaxStepsPerFrame = MaxStepsPerFrame,
            DiagnosticEverySteps = DiagnosticEverySteps,
            DiagnosticRadius = DiagnosticRadius
        };
        settings.Validate();
        return settings;
    }
}
