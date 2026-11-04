using System.Text.Json;
using System.Text.Json.Serialization;
using ParticleLife.Core.Models;
using ParticleLife.Core.State;

namespace ParticleLife.Core.Experiments;

/// <summary>Reads and writes the strict versioned JSON experiment format outside numerical hot loops.</summary>
/// <remarks>
/// Schema 1 stores reduced units, soft-wall boundaries, the model and solver identifiers, every matrix
/// entry, and complete initial state. Unknown fields, unsupported versions, omitted required fields,
/// non-finite numbers and invalid model inputs are rejected. JSON doubles round-trip without rounding
/// to display precision. This is an initial-condition format; run reports are separate documents.
/// </remarks>
public static class ExperimentFile
{
    /// <summary>Gets the supported experiment schema version.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Gets the versioned phenomenological radial and wall model identifier.</summary>
    public const string ModelVersion = "particle-life/1";

    /// <summary>Gets the direct, serial, double-precision integration backend identifier.</summary>
    public const string SolverVersion = "direct-damped-semi-implicit-euler/1";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    /// <summary>Loads and validates actual initial conditions from a UTF-8 JSON file.</summary>
    /// <param name="path">Path to an experiment file.</param>
    /// <returns>An immutable validated experiment.</returns>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="JsonException">JSON structure, fields or numbers are invalid.</exception>
    /// <exception cref="ArgumentException">Model quantities or state violate their contracts.</exception>
    /// <exception cref="NotSupportedException">A schema, model, solver, unit or boundary identifier is unsupported.</exception>
    public static ExperimentDefinition Load(string path) => Deserialize(File.ReadAllText(path));

    /// <summary>Saves an experiment with all initial values, replacing an existing file if present.</summary>
    /// <param name="path">Destination path; its parent directory must already exist.</param>
    /// <param name="experiment">Validated experiment to persist.</param>
    /// <exception cref="IOException">The destination cannot be written.</exception>
    /// <remarks>Serialization and file I/O allocate and belong outside simulation steps.</remarks>
    public static void Save(string path, ExperimentDefinition experiment) => File.WriteAllText(path, Serialize(experiment) + "\n");

    /// <summary>Serializes complete initial conditions without regenerating any value from the seed.</summary>
    /// <param name="experiment">Validated experiment.</param>
    /// <returns>Schema 1 JSON, including actual matrix and particle values.</returns>
    public static string Serialize(ExperimentDefinition experiment)
    {
        ArgumentNullException.ThrowIfNull(experiment);
        ParticleSnapshot initial = experiment.InitialState;
        return JsonSerializer.Serialize(new FileData(
            SchemaVersion, ModelVersion, SolverVersion, "reduced-l0-t0-m0", "soft-wall",
            experiment.Name, experiment.TimeStep, ParameterData.From(experiment.Parameters),
            new MatrixData(experiment.Matrix.SpeciesCount, experiment.Matrix.Values.ToArray()),
            new StateData(initial.Ids.ToArray(), ToPoints(initial.Positions), ToPoints(initial.Velocities), initial.Species.ToArray()),
            experiment.Generator), Options);
    }

    /// <summary>Parses and validates a versioned experiment; it never runs its generator.</summary>
    /// <param name="json">JSON containing the complete experiment.</param>
    /// <returns>An owned immutable experiment.</returns>
    /// <exception cref="JsonException">JSON is invalid, incomplete, contains null objects or unknown fields.</exception>
    /// <exception cref="ArgumentException">State, metadata or parameters are invalid.</exception>
    /// <exception cref="NotSupportedException">The document requests an unsupported version or convention.</exception>
    public static ExperimentDefinition Deserialize(string json)
    {
        FileData data = JsonSerializer.Deserialize<FileData>(json, Options) ?? throw new JsonException("An experiment must be a JSON object.");
        if (data.SchemaVersion != SchemaVersion || data.ModelVersion != ModelVersion || data.SolverVersion != SolverVersion
            || data.Units != "reduced-l0-t0-m0" || data.Boundary != "soft-wall")
        {
            throw new NotSupportedException("Unsupported experiment schema, model, solver, units or boundary convention.");
        }

        // Constructor contracts additionally validate lengths, identities, species and finite state.
        return new ExperimentDefinition(data.Name, data.Parameters.ToParameters(),
            new InteractionMatrix(data.Matrix.SpeciesCount, data.Matrix.Values),
            new ParticleSnapshot(data.InitialState.Ids, ToVectors(data.InitialState.Positions), ToVectors(data.InitialState.Velocities), data.InitialState.Species),
            data.TimeStep, data.Generator);
    }

    private sealed record FileData(int SchemaVersion, string ModelVersion, string SolverVersion, string Units,
        string Boundary, string Name, double TimeStep, ParameterData Parameters, MatrixData Matrix,
        StateData InitialState, GeneratorMetadata Generator);

    private sealed record MatrixData(int SpeciesCount, double[] Values);

    private sealed record StateData(long[] Ids, PointData[] Positions, PointData[] Velocities, int[] Species);

    private sealed record PointData(double X, double Y);

    private static PointData[] ToPoints(ReadOnlySpan<Vector2d> vectors)
    {
        PointData[] points = new PointData[vectors.Length];
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = new PointData(vectors[i].X, vectors[i].Y);
        }

        return points;
    }

    private static Vector2d[] ToVectors(PointData[] points)
    {
        Vector2d[] vectors = new Vector2d[points.Length];
        for (int i = 0; i < vectors.Length; i++)
        {
            PointData point = points[i] ?? throw new JsonException("Positions and velocities cannot contain null elements.");
            vectors[i] = new Vector2d(point.X, point.Y);
        }

        return vectors;
    }

    private sealed record ParameterData(double WorldWidth, double WorldHeight, double Cutoff, double ForceScale,
        double RepulsionFraction, double? DampingHalfLife, double WallWidth, double WallStrength)
    {
        public static ParameterData From(ModelParameters p) => new(p.WorldWidth, p.WorldHeight, p.Cutoff,
            p.ForceScale, p.RepulsionFraction, p.DampingHalfLife, p.WallWidth, p.WallStrength);

        public ModelParameters ToParameters() => new()
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
}
