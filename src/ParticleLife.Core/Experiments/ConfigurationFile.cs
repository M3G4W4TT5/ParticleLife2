using System.Text.Json;
using System.Text.Json.Serialization;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;

namespace ParticleLife.Core.Experiments;

/// <summary>Encodes the shared rule/configuration payload used by versioned sessions and presets.</summary>
/// <remarks>The enclosing document owns its schema version. Current payloads explicitly contain
/// boundary and pairRanges (null selects global radii). Legacy payloads omit both and retain soft
/// walls/global beta/R. Unknown fields, duplicates and missing current fields are rejected.</remarks>
public static class ConfigurationFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    /// <summary>Creates an owned JSON value containing actual rules and numerical settings.</summary>
    /// <param name="configuration">Complete validated configuration.</param>
    /// <returns>An independent JSON element, valid without a retained document lifetime.</returns>
    public static JsonElement Encode(SimulationConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ModelParameters p = configuration.Parameters;
        InteractionMatrix m = configuration.Matrix;
        return JsonSerializer.SerializeToElement(new ConfigurationData(
            new(p.WorldWidth, p.WorldHeight, p.Cutoff, p.ForceScale, p.RepulsionFraction, p.DampingHalfLife,
                p.WallWidth, p.WallStrength, ExperimentFile.BoundaryIdentifier(p.Boundary)),
            new(m.SpeciesCount, m.Values.ToArray(), m.Ranges is PairInteractionRanges ranges
                ? new(ranges.CoreRadii.ToArray(), ranges.CutoffRadii.ToArray()) : null),
            configuration.TimeStep, ExperimentFile.GetNeighbourSearchVersion(configuration.Backend), configuration.WorkerCount), Options);
    }

    /// <summary>Decodes the rule payload with the enclosing format's explicit legacy/current meaning.</summary>
    /// <param name="element">An object containing all configuration values.</param>
    /// <param name="legacy">True only for schema-1 sessions/presets; new rule fields are forbidden there.</param>
    /// <returns>A validated immutable configuration.</returns>
    /// <exception cref="JsonException">Required, duplicate or unknown fields violate the selected schema.</exception>
    /// <exception cref="ArgumentException">A numerical or model quantity is invalid.</exception>
    /// <exception cref="NotSupportedException">An execution or boundary identifier is unknown.</exception>
    public static SimulationConfiguration Decode(JsonElement element, bool legacy = false)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("parameters", out JsonElement parameters)
            || parameters.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("matrix", out JsonElement matrix)
            || matrix.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Configuration must contain parameter and matrix objects.");
        }
        bool hasBoundary = parameters.TryGetProperty("boundary", out _);
        bool hasRanges = matrix.TryGetProperty("pairRanges", out _);
        if (legacy ? hasBoundary || hasRanges : !hasBoundary || !hasRanges)
        {
            throw new JsonException(legacy ? "Legacy configurations cannot contain boundary or pairRanges fields."
                : "Current configurations require explicit boundary and pairRanges fields.");
        }
        ConfigurationData data = JsonSerializer.Deserialize<ConfigurationData>(element.GetRawText(), Options)!;
        ParameterData p = data.Parameters;
        if (!legacy && p.Boundary is null)
        {
            throw new JsonException("Current configuration boundary cannot be null.");
        }
        ModelParameters model = new()
        {
            WorldWidth = p.WorldWidth,
            WorldHeight = p.WorldHeight,
            Cutoff = p.Cutoff,
            ForceScale = p.ForceScale,
            RepulsionFraction = p.RepulsionFraction,
            DampingHalfLife = p.DampingHalfLife,
            WallWidth = p.WallWidth,
            WallStrength = p.WallStrength,
            Boundary = legacy ? BoundaryKind.SoftWall : ExperimentFile.ParseBoundary(p.Boundary!)
        };
        MatrixData m = data.Matrix;
        PairInteractionRanges? ranges = m.PairRanges is RangeData pair ? new(m.SpeciesCount, pair.CoreRadii, pair.CutoffRadii) : null;
        ForceBackend backend = data.NeighbourSearchVersion switch
        {
            "direct/1" => ForceBackend.Direct,
            "uniform-grid/1" => ForceBackend.UniformGrid,
            _ => throw new NotSupportedException("Unsupported configuration neighbour-search convention.")
        };
        return new(model, new InteractionMatrix(m.SpeciesCount, m.Values, ranges), data.TimeStep, backend, data.WorkerCount);
    }

    private sealed record ConfigurationData(ParameterData Parameters, MatrixData Matrix, double TimeStep,
        string NeighbourSearchVersion, int WorkerCount);

    private sealed record ParameterData(double WorldWidth, double WorldHeight, double Cutoff, double ForceScale,
        double RepulsionFraction, double? DampingHalfLife, double WallWidth, double WallStrength, string? Boundary = null);

    private sealed record MatrixData(int SpeciesCount, double[] Values, RangeData? PairRanges = null);

    private sealed record RangeData(double[] CoreRadii, double[] CutoffRadii);
}
