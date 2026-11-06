using System.Text.Json;
using System.Text.Json.Serialization;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.State;

namespace ParticleLife.Core.Execution;

/// <summary>Reads and writes capture-backed sessions independently of graphics, devices and storage compression.</summary>
/// <remarks>The canonical JSON embeds the existing action-session codec without imposing its schema
/// version. Storage layers may compress or chunk these bytes losslessly. Unknown, duplicate, missing,
/// unsupported and inconsistent metadata fails before any simulation is changed.</remarks>
public static class ExecutionCaptureFile
{
    /// <summary>Gets the document kind distinguishing actual captures from action-only sessions and reports.</summary>
    public const string Format = "particle-life-captured-session";
    /// <summary>Gets the supported capture-container schema.</summary>
    public const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    /// <summary>Serializes authoritative captured values and their separate action history.</summary>
    /// <param name="session">Validated capture-backed session.</param>
    /// <returns>Canonical versioned JSON preserving every stored double and widened float value.</returns>
    public static string Serialize(CapturedSessionDefinition session)
    {
        ArgumentNullException.ThrowIfNull(session);
        using JsonDocument actions = JsonDocument.Parse(SessionFile.Serialize(session.Actions));
        CaptureData[] captures = new CaptureData[session.Captures.Count];
        for (int index = 0; index < captures.Length; index++)
        {
            SimulationCapture capture = session.Captures[index];
            captures[index] = new(ConfigurationFile.Encode(capture.Configuration), SnapshotData.From(capture.State),
                capture.Execution, capture.AppliedEventCount, capture.Reason);
        }
        return JsonSerializer.Serialize(new FileData(Format, SchemaVersion, actions.RootElement,
            captures, session.Transitions.ToArray()), Options);
    }

    /// <summary>Strictly parses actual captures without selecting a replay policy or running actions.</summary>
    /// <param name="json">Complete canonical capture-container JSON.</param>
    /// <returns>An independently owned validated captured session.</returns>
    public static CapturedSessionDefinition Deserialize(string json)
    {
        FileData data = JsonSerializer.Deserialize<FileData>(json, Options)
            ?? throw new JsonException("A captured session must be an object.");
        if (data.Format != Format || data.SchemaVersion != SchemaVersion)
        {
            throw new NotSupportedException("Unsupported captured-session format or schema.");
        }
        SessionDefinition actions = SessionFile.Deserialize(data.Actions.GetRawText());
        SimulationCapture[] captures = new SimulationCapture[data.Captures.Length];
        for (int index = 0; index < captures.Length; index++)
        {
            CaptureData capture = data.Captures[index] ?? throw new JsonException("Capture entries cannot be null.");
            captures[index] = new(ConfigurationFile.Decode(capture.Configuration), capture.State.ToSnapshot(),
                capture.Execution, capture.AppliedEventCount, capture.Reason);
        }
        return new(actions, captures, data.Transitions);
    }

    /// <summary>Reads one canonical capture file without requiring a graphics runtime.</summary>
    /// <param name="path">UTF-8 file path.</param>
    /// <returns>Exact validated values with no implicit re-simulation.</returns>
    public static CapturedSessionDefinition Load(string path) => Deserialize(File.ReadAllText(path));

    /// <summary>Exclusively creates a new canonical capture file after serialization succeeds.</summary>
    /// <param name="path">Destination with an existing parent directory.</param>
    /// <param name="session">Actual capture-backed recording.</param>
    public static void Save(string path, CapturedSessionDefinition session)
    {
        string json = Serialize(session);
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using StreamWriter writer = new(stream);
        writer.WriteLine(json);
    }

    private sealed record FileData(string Format, int SchemaVersion, JsonElement Actions,
        CaptureData[] Captures, ExecutionTransition[] Transitions);
    private sealed record CaptureData(JsonElement Configuration, SnapshotData State,
        ExecutionProvenance Execution, int AppliedEventCount, string Reason);
    private sealed record SnapshotData(long[] Ids, PointData[] Positions, PointData[] Velocities,
        int[] Species, double Time, long Step)
    {
        internal static SnapshotData From(ParticleSnapshot state) => new(state.Ids.ToArray(), Points(state.Positions),
            Points(state.Velocities), state.Species.ToArray(), state.Time, state.Step);
        internal ParticleSnapshot ToSnapshot() => new(Ids, Vectors(Positions), Vectors(Velocities), Species, Time, Step);
    }

    private sealed record PointData(double X, double Y);

    private static PointData[] Points(ReadOnlySpan<Vector2d> source)
    {
        PointData[] result = new PointData[source.Length];
        for (int index = 0; index < result.Length; index++)
        {
            result[index] = new(source[index].X, source[index].Y);
        }
        return result;
    }

    private static Vector2d[] Vectors(PointData[] source)
    {
        Vector2d[] result = new Vector2d[source.Length];
        for (int index = 0; index < result.Length; index++)
        {
            PointData point = source[index] ?? throw new JsonException("Captured vectors cannot be null.");
            result[index] = new(point.X, point.Y);
        }
        return result;
    }
}
