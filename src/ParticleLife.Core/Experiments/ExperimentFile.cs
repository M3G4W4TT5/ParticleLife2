using System.Text.Json;
using System.Text.Json.Serialization;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Core.Experiments;

/// <summary>Reads and writes the strict versioned JSON experiment format outside numerical hot loops.</summary>
/// <remarks>
/// Schema 3 stores reduced units, explicit boundaries, directed optional core/cutoff tables, model, integrator and neighbour-search identifiers, every matrix
/// entry, and complete initial state. Unknown fields, unsupported versions, omitted required fields,
/// non-finite numbers and invalid model inputs are rejected. JSON doubles round-trip without rounding
/// to display precision. Schema 1 remains readable with its original direct-solver meaning.
/// Saving always writes schema 3 with an explicit workerCount. Older schema-2 files without this
/// optional field retain serial execution. Search conventions remain unchanged by packed storage.
/// This is an initial-condition format; run reports are separate documents.
/// </remarks>
public static class ExperimentFile
{
    /// <summary>Gets the written experiment schema version; loading also accepts legacy schemas 1 and 2.</summary>
    public const int SchemaVersion = 3;

    /// <summary>Gets the versioned phenomenological radial and wall model identifier.</summary>
    public const string ModelVersion = "particle-life/2";

    /// <summary>Gets the original global-range, soft-wall model identifier retained for old files.</summary>
    public const string LegacyModelVersion = "particle-life/1";

    /// <summary>Gets the legacy schema-1 combined direct-search/integrator identifier.</summary>
    /// <remarks>Retained for legacy input compatibility only. Current execution reports separate
    /// <see cref="IntegratorVersion"/> from <see cref="GetNeighbourSearchVersion"/>.</remarks>
    public const string SolverVersion = "direct-damped-semi-implicit-euler/1";

    /// <summary>Gets the unchanged double-precision damped semi-implicit Euler integration identifier.</summary>
    public const string IntegratorVersion = "damped-semi-implicit-euler/1";

    /// <summary>Gets the neighbour-search convention for an intended or actual backend, independent of workers.</summary>
    /// <param name="backend">Direct or uniform-grid enumeration.</param>
    /// <returns>A versioned search identifier independent of the physical model and integrator.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The backend is unsupported.</exception>
    public static string GetNeighbourSearchVersion(ForceBackend backend) => backend switch
    {
        ForceBackend.Direct => "direct/1",
        ForceBackend.UniformGrid => "uniform-grid/1",
        _ => throw new ArgumentOutOfRangeException(nameof(backend))
    };

    /// <summary>Identifies the current implementation layout without changing the saved search convention.</summary>
    /// <param name="backend">The actual or requested backend.</param>
    /// <returns>A versioned implementation identifier for runtime provenance.</returns>
    /// <remarks>The grid retains its nine-cell and ascending in-cell enumeration contract; packing changes
    /// storage, not the meaning of schema-2 uniform-grid/1. Worker scheduling owns disjoint receivers.</remarks>
    public static string GetForceAlgorithmVersion(ForceBackend backend) => backend switch
    {
        ForceBackend.Direct => "direct/1",
        ForceBackend.UniformGrid => UniformGridForceEvaluator.AlgorithmVersion,
        _ => throw new ArgumentOutOfRangeException(nameof(backend))
    };

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
    /// <returns>Schema 3 JSON, including actual pair rules, particle values and intended neighbour search.</returns>
    public static string Serialize(ExperimentDefinition experiment)
    {
        ArgumentNullException.ThrowIfNull(experiment);
        ParticleSnapshot initial = experiment.InitialState;
        return JsonSerializer.Serialize(new FileDataV3(
            SchemaVersion, ModelVersion, IntegratorVersion, GetNeighbourSearchVersion(experiment.Backend), "reduced-l0-t0-m0", BoundaryIdentifier(experiment.Parameters.Boundary),
            experiment.Name, experiment.TimeStep, ParameterData.From(experiment.Parameters),
            new MatrixDataV3(experiment.Matrix.SpeciesCount, experiment.Matrix.Values.ToArray(),
                experiment.Matrix.Ranges is PairInteractionRanges ranges ? new(ranges.CoreRadii.ToArray(), ranges.CutoffRadii.ToArray()) : null),
            new StateData(initial.Ids.ToArray(), ToPoints(initial.Positions), ToPoints(initial.Velocities), initial.Species.ToArray()),
            experiment.Generator, experiment.WorkerCount, experiment.Provenance), Options);
    }

    /// <summary>Parses and validates a versioned experiment; it never runs its generator.</summary>
    /// <param name="json">JSON containing the complete experiment.</param>
    /// <returns>An owned immutable experiment.</returns>
    /// <exception cref="JsonException">JSON is invalid, incomplete, contains null objects or unknown fields.</exception>
    /// <exception cref="ArgumentException">State, metadata or parameters are invalid.</exception>
    /// <exception cref="NotSupportedException">The document requests an unsupported version or convention.</exception>
    public static ExperimentDefinition Deserialize(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("schemaVersion", out JsonElement version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int schema))
        {
            throw new JsonException("An experiment must be an object with an integer schemaVersion.");
        }

        // Deserialize the complete original text with a schema-specific shape. This retains duplicate,
        // unknown-field and required-field checks, including the version used for this dispatch.
        if (schema == 1)
        {
            FileDataV1 data = JsonSerializer.Deserialize<FileDataV1>(json, Options)!;
            ValidateConventions(data.ModelVersion, data.Units, data.Boundary);
            if (data.SolverVersion != SolverVersion)
            {
                throw new NotSupportedException("Unsupported legacy experiment solver convention.");
            }

            return CreateDefinition(data.Name, data.Parameters, data.Matrix, data.InitialState, data.TimeStep,
                data.Generator, ForceBackend.Direct);
        }

        if (schema == 2)
        {
            FileDataV2 data = JsonSerializer.Deserialize<FileDataV2>(json, Options)!;
            ValidateConventions(data.ModelVersion, data.Units, data.Boundary);
            if (data.IntegratorVersion != IntegratorVersion)
            {
                throw new NotSupportedException("Unsupported experiment integrator convention.");
            }

            ForceBackend backend = data.NeighbourSearchVersion switch
            {
                "direct/1" => ForceBackend.Direct,
                "uniform-grid/1" => ForceBackend.UniformGrid,
                _ => throw new NotSupportedException("Unsupported experiment neighbour-search convention.")
            };
            return CreateDefinition(data.Name, data.Parameters, data.Matrix, data.InitialState, data.TimeStep,
                data.Generator, backend, data.WorkerCount, data.Provenance);
        }

        if (schema == SchemaVersion)
        {
            FileDataV3 data = JsonSerializer.Deserialize<FileDataV3>(json, Options)!;
            if (data.ModelVersion != ModelVersion || data.Units != "reduced-l0-t0-m0" || data.IntegratorVersion != IntegratorVersion)
            {
                throw new NotSupportedException("Unsupported experiment model, units or integrator convention.");
            }
            ForceBackend backend = ParseNeighbourSearch(data.NeighbourSearchVersion);
            ModelParameters parameters = data.Parameters.ToParameters() with { Boundary = ParseBoundary(data.Boundary) };
            PairInteractionRanges? ranges = data.Matrix.PairRanges is RangeData pair
                ? new(data.Matrix.SpeciesCount, pair.CoreRadii, pair.CutoffRadii) : null;
            return new(data.Name, parameters, new InteractionMatrix(data.Matrix.SpeciesCount, data.Matrix.Values, ranges),
                new ParticleSnapshot(data.InitialState.Ids, ToVectors(data.InitialState.Positions),
                    ToVectors(data.InitialState.Velocities), data.InitialState.Species),
                data.TimeStep, data.Generator, backend, data.WorkerCount, data.Provenance);
        }

        throw new NotSupportedException("Unsupported experiment schema.");
    }

    private static void ValidateConventions(string model, string units, string boundary)
    {
        if (model != LegacyModelVersion || units != "reduced-l0-t0-m0" || boundary != "soft-wall")
        {
            throw new NotSupportedException("Unsupported experiment model, units or boundary convention.");
        }
    }

    /// <summary>Returns the stable file identifier for an explicit two-dimensional boundary rule.</summary>
    /// <param name="boundary">A supported world rule.</param>
    /// <returns>A lowercase format identifier independent of display labels.</returns>
    public static string BoundaryIdentifier(BoundaryKind boundary) => boundary switch
    {
        BoundaryKind.SoftWall => "soft-wall",
        BoundaryKind.Open => "open",
        BoundaryKind.Reflecting => "reflecting",
        BoundaryKind.Periodic => "periodic",
        _ => throw new ArgumentOutOfRangeException(nameof(boundary))
    };

    /// <summary>Parses a supported file boundary identifier without defaulting unknown rules.</summary>
    /// <param name="identifier">The exact saved boundary identifier.</param>
    /// <returns>The corresponding world rule.</returns>
    /// <exception cref="NotSupportedException">The identifier is unknown.</exception>
    public static BoundaryKind ParseBoundary(string identifier) => identifier switch
    {
        "soft-wall" => BoundaryKind.SoftWall,
        "open" => BoundaryKind.Open,
        "reflecting" => BoundaryKind.Reflecting,
        "periodic" => BoundaryKind.Periodic,
        _ => throw new NotSupportedException("Unsupported world boundary convention.")
    };

    private static ForceBackend ParseNeighbourSearch(string identifier) => identifier switch
    {
        "direct/1" => ForceBackend.Direct,
        "uniform-grid/1" => ForceBackend.UniformGrid,
        _ => throw new NotSupportedException("Unsupported experiment neighbour-search convention.")
    };

    private static ExperimentDefinition CreateDefinition(string name, ParameterData parameters, MatrixData matrix,
        StateData state, double timeStep, GeneratorMetadata generator, ForceBackend backend, int workerCount = 1,
        ExperimentProvenance? provenance = null)
    {
        // Constructor contracts additionally validate lengths, identities, species and finite state.
        return new ExperimentDefinition(name, parameters.ToParameters(),
            new InteractionMatrix(matrix.SpeciesCount, matrix.Values),
            new ParticleSnapshot(state.Ids, ToVectors(state.Positions), ToVectors(state.Velocities), state.Species),
            timeStep, generator, backend, workerCount, provenance);
    }

    private sealed record FileDataV1(int SchemaVersion, string ModelVersion, string SolverVersion, string Units,
        string Boundary, string Name, double TimeStep, ParameterData Parameters, MatrixData Matrix,
        StateData InitialState, GeneratorMetadata Generator);

    private sealed record FileDataV2(int SchemaVersion, string ModelVersion, string IntegratorVersion, string NeighbourSearchVersion, string Units,
        string Boundary, string Name, double TimeStep, ParameterData Parameters, MatrixData Matrix,
        StateData InitialState, GeneratorMetadata Generator, int WorkerCount = 1,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExperimentProvenance? Provenance = null);

    private sealed record MatrixData(int SpeciesCount, double[] Values);

    private sealed record FileDataV3(int SchemaVersion, string ModelVersion, string IntegratorVersion, string NeighbourSearchVersion, string Units,
        string Boundary, string Name, double TimeStep, ParameterData Parameters, MatrixDataV3 Matrix,
        StateData InitialState, GeneratorMetadata Generator, int WorkerCount,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExperimentProvenance? Provenance = null);

    private sealed record MatrixDataV3(int SpeciesCount, double[] Values, RangeData? PairRanges);

    private sealed record RangeData(double[] CoreRadii, double[] CutoffRadii);

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
