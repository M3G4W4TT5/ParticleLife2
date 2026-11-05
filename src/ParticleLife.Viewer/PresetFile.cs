using System.Text.Json;
using System.Text.Json.Serialization;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;

namespace ParticleLife.Viewer;

/// <summary>Reads and writes the strict versioned reusable-preset format, distinct from actual experiments.</summary>
/// <remarks>All fields are required; unknown and duplicate fields, non-finite values, unsupported
/// versions, invalid populations and enum strings fail before any running editor can be changed.</remarks>
public static class PresetFile
{
    /// <summary>Gets the supported preset schema version.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Serializes exact current rules and a reusable generation recipe.</summary>
    /// <param name="preset">Validated immutable preset.</param>
    /// <returns>Versioned JSON; no actual evolved particle state is implied.</returns>
    public static string Serialize(PresetDefinition preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return JsonSerializer.Serialize(new PresetData("particle-life-preset", SchemaVersion, preset.Name,
            ConfigurationData.From(preset.Configuration), preset.Populations.ToArray(), GenerationData.From(preset.Generation),
            AppearanceData.From(preset.Appearance), PlaybackData.From(preset.Playback)), EditorJson.Options);
    }

    /// <summary>Parses and validates a complete reusable preset without generating particles.</summary>
    /// <param name="json">Strict preset JSON.</param>
    /// <returns>Independent immutable configuration and settings.</returns>
    public static PresetDefinition Deserialize(string json)
    {
        PresetData data = JsonSerializer.Deserialize<PresetData>(json, EditorJson.Options)
            ?? throw new JsonException("A preset must be a JSON object.");
        if (data.DocumentKind != "particle-life-preset" || data.SchemaVersion != SchemaVersion)
        {
            throw new NotSupportedException("Unsupported preset document kind or schema version.");
        }

        return new PresetDefinition(data.Name, data.Configuration.ToConfiguration(), data.Populations,
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

    private sealed record PresetData(string DocumentKind, int SchemaVersion, string Name, ConfigurationData Configuration,
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

internal sealed record ConfigurationData(ParameterData Parameters, MatrixData Matrix, double TimeStep,
    string NeighbourSearchVersion, int WorkerCount)
{
    internal static ConfigurationData From(SimulationConfiguration configuration) => new(ParameterData.From(configuration.Parameters),
        new MatrixData(configuration.Matrix.SpeciesCount, configuration.Matrix.Values.ToArray()), configuration.TimeStep,
        Core.Experiments.ExperimentFile.GetNeighbourSearchVersion(configuration.Backend), configuration.WorkerCount);

    internal SimulationConfiguration ToConfiguration() => new(Parameters.ToParameters(), new InteractionMatrix(Matrix.SpeciesCount, Matrix.Values),
        TimeStep, NeighbourSearchVersion switch
        {
            "direct/1" => ForceBackend.Direct,
            "uniform-grid/1" => ForceBackend.UniformGrid,
            _ => throw new NotSupportedException("Unsupported preset neighbour-search convention.")
        }, WorkerCount);
}

internal sealed record ParameterData(double WorldWidth, double WorldHeight, double Cutoff, double ForceScale,
    double RepulsionFraction, double? DampingHalfLife, double WallWidth, double WallStrength)
{
    internal static ParameterData From(ModelParameters parameters) => new(parameters.WorldWidth, parameters.WorldHeight,
        parameters.Cutoff, parameters.ForceScale, parameters.RepulsionFraction, parameters.DampingHalfLife,
        parameters.WallWidth, parameters.WallStrength);

    internal ModelParameters ToParameters() => new()
    {
        WorldWidth = WorldWidth,
        WorldHeight = WorldHeight,
        Cutoff = Cutoff,
        ForceScale = ForceScale,
        RepulsionFraction = RepulsionFraction,
        DampingHalfLife = DampingHalfLife,
        WallWidth = WallWidth,
        WallStrength = WallStrength
    };
}

internal sealed record MatrixData(int SpeciesCount, double[] Values);

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
    bool ShowHelp, bool ShowOverlay, bool ShowEditor, double UiScale,
    CameraSettings? Camera = null, string? ParticleSizeMode = null, double? WorldParticleRadius = null)
{
    internal static AppearanceData From(AppearanceSettings settings) => new(settings.SpeciesColors.ToArray(), settings.ParticleRadius,
        settings.ShowDiagnostics, settings.ShowHelp, settings.ShowOverlay, settings.ShowEditor, settings.UiScale,
        settings.Camera, settings.ParticleSizeMode.ToString(), settings.WorldParticleRadius);

    internal AppearanceSettings ToSettings()
    {
        ParticleSizeMode mode = Viewer.ParticleSizeMode.ScreenPixels;
        if (ParticleSizeMode is not null && (!Enum.TryParse(ParticleSizeMode, out mode) || !Enum.IsDefined(mode)
            || mode.ToString() != ParticleSizeMode)) { throw new NotSupportedException("Unsupported particle display size mode."); }
        AppearanceSettings settings = new(SpeciesColors)
        {
            ParticleRadius = ParticleRadius,
            ShowDiagnostics = ShowDiagnostics,
            ShowHelp = ShowHelp,
            ShowOverlay = ShowOverlay,
            ShowEditor = ShowEditor,
            UiScale = UiScale,
            Camera = Camera ?? new CameraSettings(),
            ParticleSizeMode = mode,
            WorldParticleRadius = WorldParticleRadius ?? 0.003
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
