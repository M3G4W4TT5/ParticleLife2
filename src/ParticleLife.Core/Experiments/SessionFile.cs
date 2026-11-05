using System.Text.Json;
using System.Text.Json.Serialization;
using ParticleLife.Core.State;

namespace ParticleLife.Core.Experiments;

/// <summary>Serializes the strictly versioned, graphics-independent scientific session format.</summary>
/// <remarks>All event types have one explicit shape: complete settings and an optional actual structural
/// state. No runtime type names, arbitrary command objects or generator replay are accepted. Unknown,
/// duplicate, missing required and unsupported-version fields fail. File I/O is outside numerical loops.
/// A complete replay additionally verifies structural times against actual accumulated timestep sums.</remarks>
public static class SessionFile
{
    /// <summary>Gets the supported session format identifier, distinct from experiments and run reports.</summary>
    public const string Format = "particle-life-session";

    /// <summary>Gets the written session schema version; legacy schema 1 remains readable.</summary>
    public const int SchemaVersion = 2;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    /// <summary>Loads exact initial values and the ordered edit sequence without regenerating particles.</summary>
    /// <param name="path">UTF-8 session file path.</param>
    /// <returns>A validated immutable session ready for shared replay.</returns>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="JsonException">Contract fields are malformed, duplicated, unknown or incomplete.</exception>
    /// <exception cref="ArgumentException">The state, configuration or event ordering is invalid.</exception>
    /// <exception cref="NotSupportedException">The format or version is unsupported.</exception>
    public static SessionDefinition Load(string path) => Deserialize(File.ReadAllText(path));

    /// <summary>Writes a session using exclusive file creation, protecting any existing destination.</summary>
    /// <param name="path">Destination whose parent directory already exists.</param>
    /// <param name="session">Immutable session to serialize before opening the destination.</param>
    /// <exception cref="IOException">The destination exists or cannot be written.</exception>
    /// <remarks>Hosts may choose a different name after failure. This operation never mutates a simulation.</remarks>
    public static void Save(string path, SessionDefinition session)
    {
        string json = Serialize(session);
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using StreamWriter writer = new(stream);
        writer.WriteLine(json);
    }

    /// <summary>Serializes complete saved values and authoritative same-boundary event ordering.</summary>
    /// <param name="session">Validated immutable session.</param>
    /// <returns>Versioned JSON with round-trippable double precision.</returns>
    public static string Serialize(SessionDefinition session)
    {
        ArgumentNullException.ThrowIfNull(session);
        using JsonDocument initial = JsonDocument.Parse(ExperimentFile.Serialize(session.InitialExperiment));
        EventData[] events = new EventData[session.Events.Count];
        for (int i = 0; i < events.Length; i++)
        {
            SessionEvent change = session.Events[i];
            events[i] = new(change.BoundaryStep, ConfigurationFile.Encode(change.Configuration), change.Label,
                change.ReplacementState is null ? null : SnapshotData.From(change.ReplacementState), change.Generator);
        }

        return JsonSerializer.Serialize(new FileData(Format, SchemaVersion, initial.RootElement,
            events, session.TerminalStep), Options);
    }

    /// <summary>Strictly parses an explicit session schema, keeping reports and experiments distinct.</summary>
    /// <param name="json">The complete JSON document.</param>
    /// <returns>An immutable, ordered session; no simulation or generator runs while parsing.</returns>
    /// <exception cref="JsonException">The document violates the explicit field contract.</exception>
    /// <exception cref="ArgumentException">A value, population or boundary order is invalid.</exception>
    /// <exception cref="NotSupportedException">A format, schema or model convention is unsupported.</exception>
    public static SessionDefinition Deserialize(string json)
    {
        FileData data = JsonSerializer.Deserialize<FileData>(json, Options)
            ?? throw new JsonException("A session must be an object.");
        if (data.Format != Format || data.SchemaVersion is not (1 or SchemaVersion))
        {
            throw new NotSupportedException("Unsupported session format or schema version.");
        }

        ExperimentDefinition initial = ExperimentFile.Deserialize(data.InitialExperiment.GetRawText());
        SessionEvent[] events = new SessionEvent[data.Events.Length];
        for (int i = 0; i < events.Length; i++)
        {
            EventData change = data.Events[i] ?? throw new JsonException("Session events cannot contain null entries.");
            events[i] = new(change.BoundaryStep, ConfigurationFile.Decode(change.Configuration, legacy: data.SchemaVersion == 1),
                change.ReplacementState?.ToSnapshot(), change.Label, change.Generator);
        }

        return new(initial, events, data.TerminalStep);
    }

    private sealed record FileData(string Format, int SchemaVersion, JsonElement InitialExperiment,
        EventData[] Events, long TerminalStep);

    private sealed record EventData(long BoundaryStep, JsonElement Configuration, string Label,
        SnapshotData? ReplacementState = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GeneratorMetadata? Generator = null);

    private sealed record SnapshotData(long[] Ids, PointData[] Positions, PointData[] Velocities,
        int[] Species, double Time, long Step)
    {
        internal static SnapshotData From(ParticleSnapshot state) => new(state.Ids.ToArray(), ToPoints(state.Positions),
            ToPoints(state.Velocities), state.Species.ToArray(), state.Time, state.Step);

        internal ParticleSnapshot ToSnapshot() => new(Ids, ToVectors(Positions), ToVectors(Velocities), Species, Time, Step);
    }

    private sealed record PointData(double X, double Y);

    private static PointData[] ToPoints(ReadOnlySpan<Vector2d> vectors)
    {
        PointData[] points = new PointData[vectors.Length];
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = new(vectors[i].X, vectors[i].Y);
        }

        return points;
    }

    private static Vector2d[] ToVectors(PointData[] points)
    {
        Vector2d[] vectors = new Vector2d[points.Length];
        for (int i = 0; i < vectors.Length; i++)
        {
            PointData point = points[i] ?? throw new JsonException("Particle vectors cannot contain null entries.");
            vectors[i] = new(point.X, point.Y);
        }

        return vectors;
    }

}
