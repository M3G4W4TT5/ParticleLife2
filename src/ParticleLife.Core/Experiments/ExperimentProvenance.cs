using System.Text.Json.Serialization;
using ParticleLife.Core.Execution;

namespace ParticleLife.Core.Experiments;

/// <summary>Records the source boundary when completed state is honestly rebased as a new initial experiment.</summary>
public sealed record ExperimentProvenance
{
    /// <summary>Validates source-clock metadata without changing the new experiment's zero clock.</summary>
    /// <param name="sourceTime">Finite nonnegative source time in reduced units.</param>
    /// <param name="sourceStep">Nonnegative count of completed source steps.</param>
    /// <param name="operation">Nonempty description of the capture or transformation.</param>
    /// <param name="sourceGenerator">Optional original generation provenance, distinct from the capture operation.</param>
    /// <param name="sourceName">Optional nonempty name of the source experiment.</param>
    /// <param name="sourceExecution">Actual source hardware, precision and implementation when a completed state was captured.</param>
    /// <exception cref="ArgumentException">The source clock or operation is invalid.</exception>
    public ExperimentProvenance(double sourceTime, long sourceStep, string operation,
        GeneratorMetadata? sourceGenerator = null, string? sourceName = null, ExecutionProvenance? sourceExecution = null)
    {
        if (!double.IsFinite(sourceTime) || sourceTime < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceTime));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(sourceStep);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (sourceGenerator is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceGenerator.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceGenerator.Version);
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceGenerator.Runtime);
        }

        if (sourceName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        }

        SourceTime = sourceTime;
        SourceStep = sourceStep;
        Operation = operation;
        SourceGenerator = sourceGenerator;
        SourceName = sourceName;
        SourceExecution = sourceExecution;
    }

    /// <summary>Gets the actual source time in reduced units before rebasing.</summary>
    public double SourceTime { get; }

    /// <summary>Gets the source completed-step count before rebasing.</summary>
    public long SourceStep { get; }

    /// <summary>Gets the capture or transformation description.</summary>
    public string Operation { get; }

    /// <summary>Gets original generation identity, seed and runtime rather than a future editor recipe.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeneratorMetadata? SourceGenerator { get; }

    /// <summary>Gets the name of the source experiment, when retained.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceName { get; }

    /// <summary>Gets actual source execution provenance, independently of the CPU settings for future continuation.</summary>
    /// <remarks>Absent in older files. This describes the source capture; it never promises an identical
    /// trajectory after loading it on another backend or changing arithmetic precision.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExecutionProvenance? SourceExecution { get; }
}
