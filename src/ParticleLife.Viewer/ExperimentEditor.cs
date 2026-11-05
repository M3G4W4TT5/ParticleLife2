using System.Runtime.InteropServices;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Core.State;

namespace ParticleLife.Viewer;

/// <summary>Coordinates completed-boundary live editing, generation, undo and scientific replay history.</summary>
/// <remarks>
/// <para>The caller owns <see cref="Playback"/> and invokes editor operations sequentially between
/// completed steps. This object never mutates an exposed state view. Scalar and matrix edits retain
/// particle state and clock. Ordinary undo changes rules at the current boundary, without rewinding motion.</para>
/// <para>Population undo preserves current motion for identities present on both sides, restores removed
/// identities at their saved removal-boundary state, and removes additions. Explicit resampling undo
/// restores saved coordinates and velocities at the current clock. Redo uses saved actual values, never seeds.</para>
/// <para>Undo gestures group user actions only. Every physical edit actually applied remains in the
/// separate ordered replay timeline, including intermediate drag values and undo/redo operations.</para>
/// </remarks>
public sealed partial class ExperimentEditor
{
    private readonly List<SessionEvent> events = [];
    private readonly List<UndoEntry> history = [];
    private int historyPosition;
    private int[] populations;
    private long nextIdentity;
    private bool identityExhausted;
    private EditorValues? gestureBefore;
    private bool gestureChanged;
    private string gestureLabel = "Edit settings";

    /// <summary>Creates editing around an existing controller, whose experiment establishes the baseline.</summary>
    /// <param name="playback">Sequential graphics-free controller; ownership stays with the caller.</param>
    public ExperimentEditor(PlaybackController playback)
    {
        ArgumentNullException.ThrowIfNull(playback);
        Playback = playback;
        Generation = new GenerationSettings { Seed = playback.Experiment.Generator.Seed ?? 1 };
        Appearance = new AppearanceSettings();
        populations = GetCurrentPopulations();
        AdvanceIdentityHighWater(playback.Observe().Ids);
    }

    /// <summary>Gets the caller-owned simulation/playback controller.</summary>
    public PlaybackController Playback { get; }

    /// <summary>Gets current physical, matrix, numerical and execution settings.</summary>
    public SimulationConfiguration Configuration => Playback.Configuration;

    /// <summary>Gets future generation settings; changes alone never regenerate existing particles.</summary>
    public GenerationSettings Generation { get; private set; }

    /// <summary>Gets presentation choices, independent of numerical state.</summary>
    public AppearanceSettings Appearance { get; private set; }

    /// <summary>Gets future generation counts by species, which may differ from the current population.</summary>
    public ReadOnlySpan<int> Populations => populations;

    /// <summary>Gets a change counter for UI cache invalidation; completed numerical steps do not increment it.</summary>
    public long Revision { get; private set; }

    /// <summary>Gets whether at least one completed editor operation can be undone.</summary>
    public bool CanUndo => historyPosition > 0 || gestureChanged || brushStroke?.Changed == true;

    /// <summary>Gets whether an undone operation can be reapplied.</summary>
    public bool CanRedo => historyPosition < history.Count && gestureBefore is null;

    /// <summary>Gets the next undo description, or null when no operation is available.</summary>
    public string? UndoLabel => gestureBefore is not null ? gestureLabel : historyPosition > 0 ? history[historyPosition - 1].Label : null;

    /// <summary>Gets the next redo description, or null when no operation is available.</summary>
    public string? RedoLabel => CanRedo ? history[historyPosition].Label : null;

    /// <summary>Gets the number of applied scientific events, independent of undo grouping.</summary>
    public int SessionEventCount => events.Count;

    /// <summary>Counts current particles by species; call on editor refresh, outside rendering hot loops.</summary>
    /// <returns>A newly owned array with one count per active matrix species.</returns>
    public int[] GetCurrentPopulations()
    {
        if (Playback.Resident is { } resident) { return resident.SpeciesCounts.ToArray(); }
        int[] counts = new int[Configuration.Matrix.SpeciesCount];
        foreach (int species in Playback.Observe().Species)
        {
            counts[species]++;
        }

        return counts;
    }

    /// <summary>Begins one undo gesture, retaining each intermediate scientific boundary event.</summary>
    /// <param name="label">Human-readable operation name.</param>
    public void BeginGesture(string label)
    {
        EnsureNotReplaying();
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        EndBrushStroke();
        EndGesture();
        gestureBefore = CaptureValues();
        gestureChanged = false;
        gestureLabel = label;
    }

    /// <summary>Ends the current scalar/settings gesture and commits one undo action if values changed.</summary>
    public void EndGesture()
    {
        CompleteGesture(CaptureValues());
    }

    private void CompleteGesture(EditorValues after)
    {
        if (gestureBefore is EditorValues before)
        {
            gestureBefore = null;
            gestureChanged = false;
            if (!SameValues(before, after))
            {
                AddHistory(new UndoEntry(gestureLabel, before, after, null, null, false));
            }
        }
    }

    /// <summary>Applies a complete rule candidate atomically without changing state, clock or baseline.</summary>
    /// <param name="configuration">Validated model, matrix, timestep and solver configuration.</param>
    /// <param name="label">Undo and replay description.</param>
    public void ApplyConfiguration(SimulationConfiguration configuration, string label = "Edit settings")
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Matrix.SpeciesCount != Configuration.Matrix.SpeciesCount)
        {
            throw new ArgumentException("Use Apply population to change species and matrix dimensions together.", nameof(configuration));
        }

        ModelParameters previous = Configuration.Parameters;
        ModelParameters next = configuration.Parameters;
        bool mapWorld = next.Boundary is BoundaryKind.Periodic or BoundaryKind.Reflecting
            && (previous.Boundary != next.Boundary || previous.WorldWidth != next.WorldWidth || previous.WorldHeight != next.WorldHeight);
        ParticleSnapshot? before = mapWorld ? Snapshot() : null;
        ParticleSnapshot? mapped = null;
        if (before is not null)
        {
            Vector2d[] positions = before.Positions.ToArray();
            Vector2d[] velocities = before.Velocities.ToArray();
            for (int index = 0; index < before.Count; index++)
            {
                WorldBoundary.Map(ref positions[index], ref velocities[index], next);
            }
            mapped = new(before.Ids, positions, velocities, before.Species, before.Time, before.Step);
        }
        Apply(CaptureValues() with { Configuration = configuration }, mapped, mapWorld, label, before);
    }

    /// <summary>Changes one directed coefficient, optionally linking its reverse as an explicit edit choice.</summary>
    /// <param name="receiver">Zero-based receiver row.</param>
    /// <param name="source">Zero-based source column.</param>
    /// <param name="value">Finite dimensionless coefficient in [-1, 1].</param>
    /// <param name="linkSymmetry">Whether this edit also sets the transposed coefficient.</param>
    public void SetMatrixCoefficient(int receiver, int source, double value, bool linkSymmetry = false)
    {
        InteractionMatrix matrix = Configuration.Matrix;
        _ = matrix[receiver, source];
        double[] values = matrix.Values.ToArray();
        values[receiver * matrix.SpeciesCount + source] = value;
        if (linkSymmetry)
        {
            values[source * matrix.SpeciesCount + receiver] = value;
        }

        ApplyConfiguration(WithMatrix(matrix.WithCoefficients(values)), "Edit directed coefficient");
    }

    /// <summary>Applies a matrix template live while preserving every particle, the clock and reset baseline.</summary>
    /// <param name="kind">Built-in matrix rule compatible with the current species count.</param>
    /// <param name="seed">Provenance seed used only for Random matrix generation.</param>
    /// <exception cref="ArgumentException">The template does not support the current species count; no edit is applied.</exception>
    public void ApplyMatrixTemplate(PresetKind kind, int seed)
    {
        InteractionMatrix generated = CreateMatrix(kind, Configuration.Matrix.SpeciesCount, seed);
        Apply(CaptureValues() with { Configuration = WithMatrix(Configuration.Matrix.WithCoefficients(generated.Values)) },
            null, false, $"Apply {kind} matrix", generator: Metadata($"viewer-{kind}-matrix", seed));
    }

    /// <summary>Sets every species coefficient to zero while retaining species-independent core repulsion.</summary>
    public void ResetMatrix() => ApplyConfiguration(WithMatrix(Configuration.Matrix.WithCoefficients(
        new double[Configuration.Matrix.Values.Length])), "Zero matrix");

    /// <summary>Changes future generation settings without changing active particles or scientific history.</summary>
    /// <param name="settings">Complete validated generation recipe.</param>
    public void SetGeneration(GenerationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        Apply(CaptureValues() with { Generation = settings }, null, false, "Edit generation recipe");
    }

    /// <summary>Changes presentation without altering trajectories or scientific history.</summary>
    /// <param name="settings">Complete validated presentation settings.</param>
    public void SetAppearance(AppearanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        Apply(CaptureValues() with { Appearance = settings }, null, false, "Edit appearance");
    }

    /// <summary>Changes wall-time playback and measurement policy without changing scientific history.</summary>
    /// <param name="settings">Complete validated playback and diagnostic settings.</param>
    public void SetPlayback(PlaybackSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        Apply(CaptureValues() with { Playback = settings }, null, false, "Edit playback");
    }

    /// <summary>Changes only future generation counts, without applying them to current particles.</summary>
    /// <param name="counts">Nonnegative count for each current matrix species.</param>
    public void SetPopulationRecipe(ReadOnlySpan<int> counts)
    {
        PresetDefinition.ValidatePopulations(counts, Configuration.Matrix.SpeciesCount);
        Apply(CaptureValues() with { Populations = counts.ToArray() }, null, false, "Edit population recipe");
    }

    /// <summary>Atomically edits matrix dimensions, particle membership and species assignments.</summary>
    /// <param name="speciesCount">Positive new species count.</param>
    /// <param name="counts">Desired nonnegative count for each new species.</param>
    /// <param name="preserveDensity">When true, scale both world extents by sqrt(new population / old population).
    /// Both populations must be nonzero. Soft/open survivors retain absolute motion; periodic/reflecting
    /// worlds explicitly map retained motion into the resized geometry.</param>
    /// <remarks>Removed species map by oldSpecies modulo newSpeciesCount. In storage order, the first
    /// requested particles of each remapped species survive with exact identity, position and velocity.
    /// Shortfalls are appended in species order using the current generation recipe and never-reused
    /// increasing identities. Retained top-left matrix entries survive; new rows/columns start at zero.
    /// Density-preserving world changes and membership form one saved, undoable operation. The wall
    /// layer is clamped to half the new shorter extent when necessary; cutoff is unchanged. Additions
    /// use the resized world. This preserves nominal count/area, not the local density of survivors.</remarks>
    public void ApplyPopulation(int speciesCount, int[] counts, bool preserveDensity = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(speciesCount);
        ArgumentNullException.ThrowIfNull(counts);
        PresetDefinition.ValidatePopulations(counts, speciesCount);
        ParticleSnapshot before = Snapshot();
        int total = counts.Sum();
        ModelParameters populationParameters = Configuration.Parameters;
        if (preserveDensity)
        {
            if (before.Count == 0 || total == 0)
            {
                throw new ArgumentException("Preserve density requires nonzero old and new populations.", nameof(counts));
            }
            double worldScale = Math.Sqrt((double)total / before.Count);
            double worldWidth = populationParameters.WorldWidth * worldScale;
            double worldHeight = populationParameters.WorldHeight * worldScale;
            populationParameters = populationParameters with
            {
                WorldWidth = worldWidth,
                WorldHeight = worldHeight,
                WallWidth = Math.Min(populationParameters.WallWidth, Math.Min(worldWidth, worldHeight) / 2)
            };
            populationParameters.Validate();
        }
        int[] retainedCounts = new int[speciesCount];
        List<int> retained = new(Math.Min(before.Count, total));
        for (int i = 0; i < before.Count; i++)
        {
            int species = before.Species[i] % speciesCount;
            if (retainedCounts[species] < counts[species])
            {
                retained.Add(i);
                retainedCounts[species]++;
            }
        }

        int additions = total - retained.Count;
        EnsureIdentityCapacity(additions);
        long[] ids = new long[total];
        Vector2d[] positions = new Vector2d[total];
        Vector2d[] velocities = new Vector2d[total];
        int[] speciesValues = new int[total];
        int output = 0;
        foreach (int index in retained)
        {
            ids[output] = before.Ids[index];
            positions[output] = before.Positions[index];
            velocities[output] = before.Velocities[index];
            speciesValues[output++] = before.Species[index] % speciesCount;
        }

        Random random = new(Generation.Seed);
        int generated = 0;
        for (int species = 0; species < speciesCount; species++)
        {
            for (int count = retainedCounts[species]; count < counts[species]; count++)
            {
                ids[output] = nextIdentity + generated;
                (positions[output], velocities[output]) = GenerateParticle(random, generated++, additions, populationParameters, Generation, species, speciesCount);
                speciesValues[output++] = species;
            }
        }

        InteractionMatrix matrix = ResizeMatrix(Configuration.Matrix, speciesCount, populationParameters);
        HashSet<long>? remapped = null;
        if (populationParameters.Boundary is BoundaryKind.Periodic or BoundaryKind.Reflecting
            && (populationParameters.WorldWidth != Configuration.Parameters.WorldWidth || populationParameters.WorldHeight != Configuration.Parameters.WorldHeight))
        {
            remapped = [];
            for (int index = 0; index < retained.Count; index++)
            {
                Vector2d originalPosition = positions[index], originalVelocity = velocities[index];
                WorldBoundary.Map(ref positions[index], ref velocities[index], populationParameters);
                if (positions[index] != originalPosition || velocities[index] != originalVelocity) { remapped.Add(ids[index]); }
            }
        }
        ParticleSnapshot after = new(ids, positions, velocities, speciesValues, before.Time, before.Step);
        SimulationConfiguration populationConfiguration = new(populationParameters, matrix,
            Configuration.TimeStep, Configuration.Backend, Configuration.WorkerCount);
        Apply(CaptureValues() with { Configuration = populationConfiguration, Populations = (int[])counts.Clone() },
            after, false, "Apply population", before, additions > 0 ? Metadata($"viewer-{Generation.Placement}-{Generation.Velocity}", Generation.Seed) : null,
            restoreIdentities: remapped);
    }

    /// <summary>Returns an even deterministic distribution, assigning the remainder to low species indices.</summary>
    /// <param name="total">Nonnegative total population.</param>
    /// <param name="speciesCount">Positive species count.</param>
    /// <returns>Owned per-species counts summing exactly to total.</returns>
    public static int[] DistributePopulation(int total, int speciesCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(total);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(speciesCount);
        int[] counts = new int[speciesCount];
        for (int i = 0; i < counts.Length; i++)
        {
            counts[i] = total / speciesCount + (i < total % speciesCount ? 1 : 0);
        }

        return counts;
    }

    /// <summary>Explicitly regenerates positions and velocities at the current clock, retaining identities and species.</summary>
    /// <remarks>Undo/redo for this operation restores saved full coordinates and velocities at the later
    /// current clock. It does not regenerate a seed or rewind intervening time.</remarks>
    public void ResampleParticles()
    {
        ParticleSnapshot before = Snapshot();
        Vector2d[] positions = new Vector2d[before.Count];
        Vector2d[] velocities = new Vector2d[before.Count];
        Random random = new(Generation.Seed);
        for (int i = 0; i < before.Count; i++)
        {
            (positions[i], velocities[i]) = GenerateParticle(random, i, before.Count, Configuration.Parameters, Generation,
                before.Species[i], Configuration.Matrix.SpeciesCount);
        }

        Apply(CaptureValues(), new ParticleSnapshot(before.Ids, positions, velocities, before.Species, before.Time, before.Step),
            true, "Resample positions and velocities", before, Metadata($"viewer-{Generation.Placement}-{Generation.Velocity}", Generation.Seed));
    }

    /// <summary>Creates a new population and matrix from the current recipe, establishing a time-zero baseline.</summary>
    /// <param name="kind">Matrix template for the explicitly new experiment.</param>
    /// <param name="name">Nonempty experiment name.</param>
    public void NewExperiment(PresetKind kind, string name)
    {
        EnsureNotReplaying();
        SimulationConfiguration configuration = WithMatrix(CreateMatrix(kind, Configuration.Matrix.SpeciesCount, Generation.Seed));
        ExperimentDefinition experiment = GenerateExperiment(name, configuration, populations, Generation);
        ReplaceBaseline(experiment, pause: false);
    }

    /// <summary>Starts a complete built-in recipe with its recommended rules, population and species count.</summary>
    /// <param name="kind">Recipe selected from <see cref="PresetCatalog"/>.</param>
    /// <remarks>
    /// Uses the current generation seed with the core generator's uniform positions and zero velocities.
    /// Model parameters and timestep return to the recipe defaults. The actual generated state establishes
    /// a new time/step-zero reset baseline, clearing undo and replay history. Execution backend/workers,
    /// appearance, playback settings and pause status are retained. To preserve particles and the clock,
    /// use <see cref="ApplyMatrixTemplate"/> with a compatible species count instead.
    /// </remarks>
    /// <exception cref="ArgumentException">The recipe is unsupported; the current experiment is retained.</exception>
    /// <exception cref="InvalidOperationException">A recorded session is currently replaying.</exception>
    public void NewRecipe(PresetKind kind)
    {
        EnsureNotReplaying();
        GenerationSettings generation = new() { Seed = Generation.Seed };
        ExperimentDefinition experiment = PresetGenerator.Create(kind, seed: generation.Seed,
            backend: Configuration.Backend, workerCount: Configuration.WorkerCount);
        ReplaceBaseline(experiment, pause: false);
        Generation = generation;
    }

    /// <summary>Restores the exact baseline state and configuration at time/step zero and starts a fresh timeline.</summary>
    /// <remarks>Presentation, playback and generation choices remain. Undo/redo and abandoned scientific
    /// events are cleared; the baseline is never changed by ordinary edits or saving.</remarks>
    public void Reset()
    {
        EnsureNotReplaying();
        Playback.Reset();
        ClearTimeline();
    }

    /// <summary>Undoes one editor operation at the current boundary without rewinding simulation time.</summary>
    public void Undo()
    {
        EnsureNotReplaying();
        EndBrushStroke();
        EndGesture();
        if (historyPosition == 0)
        {
            return;
        }

        UndoEntry entry = history[historyPosition - 1];
        RestoreHistory(entry, forward: false);
        historyPosition--;
    }

    /// <summary>Reapplies one undone operation using saved actual structural payloads.</summary>
    public void Redo()
    {
        EnsureNotReplaying();
        EndBrushStroke();
        EndGesture();
        if (!CanRedo)
        {
            return;
        }

        UndoEntry entry = history[historyPosition];
        RestoreHistory(entry, forward: true);
        historyPosition++;
    }

    /// <summary>Captures the exact completed state as a new experiment at time/step zero.</summary>
    /// <param name="name">Nonempty name for the rebased experiment.</param>
    /// <returns>Headless-compatible complete actual state, current configuration and honest source clock metadata.</returns>
    public ExperimentDefinition CaptureCurrentExperiment(string name)
    {
        ParticleStateView state = Playback.Observe();
        SimulationConfiguration configuration = Configuration;
        return new ExperimentDefinition(name, configuration.Parameters, configuration.Matrix,
            new ParticleSnapshot(state.Ids, state.Positions, state.Velocities, state.Species), configuration.TimeStep,
            Metadata("viewer-current-state", null), configuration.Backend, configuration.WorkerCount,
            new ExperimentProvenance(state.Time, state.Step, "current-state-rebase", Playback.Experiment.Generator, Playback.Experiment.Name));
    }

    /// <summary>Captures current reusable settings and future population recipe, without evolving particle state.</summary>
    /// <param name="name">Nonempty preset name.</param>
    /// <returns>Validated preset with separately typed presentation and playback settings.</returns>
    public PresetDefinition CapturePreset(string name) => new(name, Configuration, populations, Generation, Appearance,
        PlaybackSettings.Capture(Playback));

    /// <summary>Captures exact initial conditions and all applied edits through the current terminal boundary.</summary>
    /// <param name="name">Human-readable label applied to the saved initial experiment, retaining exact state and provenance.</param>
    /// <returns>Pure headless session independent of presentation and wall-clock timing.</returns>
    public SessionDefinition CaptureSession(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ExperimentDefinition baseline = Playback.Experiment;
        ExperimentDefinition named = new(name, baseline.Parameters, baseline.Matrix, baseline.InitialState, baseline.TimeStep,
            baseline.Generator, baseline.Backend, baseline.WorkerCount, baseline.Provenance);
        return new SessionDefinition(named, events, Playback.Boundary.Step);
    }

    /// <summary>Applies a reusable preset live or explicitly generates a new population and baseline.</summary>
    /// <param name="preset">Validated reusable settings.</param>
    /// <param name="generatePopulation">True explicitly creates a new population; false requires compatible species dimensions.</param>
    public void ApplyPreset(PresetDefinition preset, bool generatePopulation)
    {
        EnsureNotReplaying();
        ArgumentNullException.ThrowIfNull(preset);
        if (generatePopulation)
        {
            ExperimentDefinition experiment = GenerateExperiment(preset.Name, preset.Configuration, preset.Populations, preset.Generation);
            Playback.ReplaceExperiment(experiment);
            Generation = preset.Generation;
            Appearance = preset.Appearance;
            populations = preset.Populations.ToArray();
            ApplyPlayback(preset.Playback);
            Playback.SetPaused(true);
            ClearTimeline();
            return;
        }

        if (preset.Configuration.Matrix.SpeciesCount != Configuration.Matrix.SpeciesCount)
        {
            throw new ArgumentException("Live preset application requires the same species count. Use New population from preset explicitly.");
        }

        Apply(new EditorValues(preset.Configuration, preset.Generation, preset.Appearance, preset.Playback,
            preset.Populations.ToArray()), null, false, "Apply preset live", separateAction: true);
    }

    private void Apply(EditorValues values, ParticleSnapshot? state, bool restoreMotion, string label,
        ParticleSnapshot? beforeState = null, GeneratorMetadata? generator = null, bool separateAction = false,
        HashSet<long>? restoreIdentities = null)
    {
        EnsureNotReplaying();
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (brushStroke is not null && !applyingBrushSample) { EndBrushStroke(); }
        EditorValues before = CaptureValues();
        state = Playback.PrepareReplacement(state);
        bool physical = !before.Configuration.EquivalentTo(values.Configuration);
        bool structural = state is not null && !SameState(state, Playback.Observe());
        if (!physical && !structural && SameValues(before, values))
        {
            return;
        }

        if (structural)
        {
            beforeState ??= Snapshot();
        }

        if (physical || structural)
        {
            Playback.Reconfigure(values.Configuration, structural ? state : null);
            events.Add(new SessionEvent(Playback.Boundary.Step, values.Configuration, structural ? state : null, label, generator));
        }

        if ((structural || separateAction) && brushStroke is null)
        {
            CompleteGesture(before);
        }

        PublishValues(values);
        if (historyPosition < history.Count)
        {
            history.RemoveRange(historyPosition, history.Count - historyPosition);
        }

        if (structural)
        {
            AdvanceIdentityHighWater(state!.Ids);
        }

        if (brushStroke is BrushStroke stroke)
        {
            stroke.Changed = true;
        }
        else if (gestureBefore is null)
        {
            AddHistory(new UndoEntry(label, before, values, structural ? beforeState : null,
                structural ? state : null, restoreMotion, restoreIdentities));
        }
        else
        {
            gestureChanged = true;
        }

        Revision++;
    }

    private void RestoreHistory(UndoEntry entry, bool forward)
    {
        EditorValues target = forward ? entry.After : entry.Before;
        ParticleSnapshot? targetState = forward ? entry.AfterState : entry.BeforeState;
        ParticleSnapshot? replacement = targetState is null ? null : RestoreStructure(targetState, entry.RestoreMotion, entry.RestoreIdentities);
        replacement = Playback.PrepareReplacement(replacement);
        bool physical = !Configuration.EquivalentTo(target.Configuration);
        if (physical || replacement is not null)
        {
            Playback.Reconfigure(target.Configuration, replacement);
            events.Add(new SessionEvent(Playback.Boundary.Step, target.Configuration, replacement,
                $"{(forward ? "Redo" : "Undo")}: {entry.Label}"));
        }

        PublishValues(target);
        Revision++;
    }

    /// <summary>Reconstructs membership from an undo payload, preserving motion of surviving identities.</summary>
    private ParticleSnapshot RestoreStructure(ParticleSnapshot target, bool restoreMotion, HashSet<long>? restoreIdentities = null)
    {
        ParticleStateView current = Playback.Observe();
        Vector2d[] positions = target.Positions.ToArray();
        Vector2d[] velocities = target.Velocities.ToArray();
        if (!restoreMotion || restoreIdentities is not null)
        {
            Dictionary<long, int> indices = new(current.Count);
            for (int i = 0; i < current.Count; i++)
            {
                indices.Add(current.Ids[i], i);
            }

            for (int i = 0; i < target.Count; i++)
            {
                if (restoreIdentities?.Contains(target.Ids[i]) != true && indices.TryGetValue(target.Ids[i], out int index))
                {
                    positions[i] = current.Positions[index];
                    velocities[i] = current.Velocities[index];
                }
            }
        }

        return new ParticleSnapshot(target.Ids, positions, velocities, target.Species, current.Time, current.Step);
    }

    private void PublishValues(EditorValues values)
    {
        Generation = values.Generation;
        Appearance = values.Appearance;
        populations = values.Populations;
        ApplyPlayback(values.Playback);
    }

    private void ApplyPlayback(PlaybackSettings settings)
    {
        Playback.SetPlaybackSpeed(settings.Speed);
        Playback.SetMaxStepsPerFrame(settings.MaxStepsPerFrame);
        if (Playback.DiagnosticEverySteps != settings.DiagnosticEverySteps)
        {
            Playback.DiagnosticEverySteps = settings.DiagnosticEverySteps;
        }

        if (Playback.DiagnosticRadius != settings.DiagnosticRadius)
        {
            Playback.DiagnosticRadius = settings.DiagnosticRadius;
        }
    }

    private void AddHistory(UndoEntry entry)
    {
        if (historyPosition < history.Count)
        {
            history.RemoveRange(historyPosition, history.Count - historyPosition);
        }

        history.Add(entry);
        historyPosition++;
    }

    private void ReplaceBaseline(ExperimentDefinition experiment, bool pause)
    {
        Playback.ReplaceExperiment(experiment);
        if (pause)
        {
            Playback.SetPaused(true);
        }

        ClearTimeline();
    }

    private void ClearTimeline()
    {
        events.Clear();
        ClearCapturedTimeline();
        history.Clear();
        historyPosition = 0;
        gestureBefore = null;
        gestureChanged = false;
        brushStroke = null;
        populations = GetCurrentPopulations();
        nextIdentity = 0;
        identityExhausted = false;
        AdvanceIdentityHighWater(Playback.Observe().Ids);
        RecordInitialPrecisionConversion();
        Revision++;
    }

    private EditorValues CaptureValues() => new(Configuration, Generation, Appearance, PlaybackSettings.Capture(Playback), populations);

    private ParticleSnapshot Snapshot()
    {
        ParticleStateView state = Playback.Observe();
        return new ParticleSnapshot(state.Ids, state.Positions, state.Velocities, state.Species, state.Time, state.Step);
    }

    private SimulationConfiguration WithMatrix(InteractionMatrix matrix) => new(Configuration.Parameters, matrix,
        Configuration.TimeStep, Configuration.Backend, Configuration.WorkerCount);

    private static bool SameValues(EditorValues left, EditorValues right) =>
        left.Configuration.EquivalentTo(right.Configuration) && left.Generation == right.Generation
        && SameAppearance(left.Appearance, right.Appearance) && left.Playback == right.Playback
        && left.Populations.AsSpan().SequenceEqual(right.Populations);

    private static bool SameAppearance(AppearanceSettings left, AppearanceSettings right) =>
        left.ParticleRadius == right.ParticleRadius && left.ShowDiagnostics == right.ShowDiagnostics
        && left.ShowHelp == right.ShowHelp && left.ShowOverlay == right.ShowOverlay
        && left.ShowEditor == right.ShowEditor && left.UiScale == right.UiScale
        && left.ParticleSizeMode == right.ParticleSizeMode && left.WorldParticleRadius == right.WorldParticleRadius && left.Camera == right.Camera
        && left.SpeciesColors.SequenceEqual(right.SpeciesColors);

    private static bool SameState(ParticleSnapshot left, ParticleStateView right) =>
        left.Time == right.Time && left.Step == right.Step && left.Ids.SequenceEqual(right.Ids)
        && left.Positions.SequenceEqual(right.Positions) && left.Velocities.SequenceEqual(right.Velocities)
        && left.Species.SequenceEqual(right.Species);

    private void EnsureIdentityCapacity(int additions)
    {
        if (additions > 0 && (identityExhausted || nextIdentity > long.MaxValue - (additions - 1L)))
        {
            throw new InvalidOperationException("The identity range is exhausted; start an explicitly new experiment to allocate a fresh range.");
        }
    }

    private void AdvanceIdentityHighWater(ReadOnlySpan<long> ids)
    {
        foreach (long id in ids)
        {
            if (id == long.MaxValue)
            {
                identityExhausted = true;
            }
            else if (id >= nextIdentity)
            {
                nextIdentity = id + 1;
            }
        }
    }

    private static InteractionMatrix ResizeMatrix(InteractionMatrix previous, int speciesCount, ModelParameters parameters)
    {
        if (previous.SpeciesCount == speciesCount)
        {
            return previous;
        }

        double[] values = new double[checked(speciesCount * speciesCount)];
        int retained = Math.Min(previous.SpeciesCount, speciesCount);
        for (int receiver = 0; receiver < retained; receiver++)
        {
            for (int source = 0; source < retained; source++)
            {
                values[receiver * speciesCount + source] = previous[receiver, source];
            }
        }

        PairInteractionRanges? ranges = null;
        if (previous.Ranges is PairInteractionRanges oldRanges)
        {
            double[] cores = new double[values.Length];
            double[] cutoffs = new double[values.Length];
            Array.Fill(cores, parameters.RepulsionFraction * parameters.Cutoff);
            Array.Fill(cutoffs, parameters.Cutoff);
            for (int receiver = 0; receiver < retained; receiver++)
            {
                for (int source = 0; source < retained; source++)
                {
                    int oldIndex = receiver * previous.SpeciesCount + source;
                    int newIndex = receiver * speciesCount + source;
                    cores[newIndex] = oldRanges.CoreRadii[oldIndex];
                    cutoffs[newIndex] = oldRanges.CutoffRadii[oldIndex];
                }
            }
            ranges = new(speciesCount, cores, cutoffs);
        }
        return new InteractionMatrix(speciesCount, values, ranges);
    }

    private static InteractionMatrix CreateMatrix(PresetKind kind, int speciesCount, int seed) =>
        PresetGenerator.CreateMatrix(kind, speciesCount, seed);

    private static ExperimentDefinition GenerateExperiment(string name, SimulationConfiguration configuration,
        ReadOnlySpan<int> counts, GenerationSettings generation)
    {
        PresetDefinition.ValidatePopulations(counts, configuration.Matrix.SpeciesCount);
        generation.Validate();
        int total = 0;
        foreach (int count in counts)
        {
            total = checked(total + count);
        }

        long[] ids = new long[total];
        Vector2d[] positions = new Vector2d[total];
        Vector2d[] velocities = new Vector2d[total];
        int[] species = new int[total];
        Random random = new(generation.Seed);
        int output = 0;
        for (int group = 0; group < counts.Length; group++)
        {
            for (int i = 0; i < counts[group]; i++)
            {
                ids[output] = output;
                species[output] = group;
                (positions[output], velocities[output]) = GenerateParticle(random, output, total, configuration.Parameters, generation, group, counts.Length);
                output++;
            }
        }

        return new ExperimentDefinition(name, configuration.Parameters, configuration.Matrix,
            new ParticleSnapshot(ids, positions, velocities, species), configuration.TimeStep,
            Metadata($"viewer-{generation.Placement}-{generation.Velocity}", generation.Seed),
            configuration.Backend, configuration.WorkerCount);
    }

    /// <summary>Generates bounded positions and finite velocities using explicit reduced-unit recipes.</summary>
    /// <remarks>Disk radius uses sqrt(U) for uniform area. Grid centres use aspect-aware columns;
    /// construction may reject extreme finite recipe arithmetic rather than publish non-finite state.</remarks>
    private static (Vector2d Position, Vector2d Velocity) GenerateParticle(Random random, int index, int count,
        ModelParameters parameters, GenerationSettings generation, int species = 0, int speciesCount = 1)
    {
        Vector2d position = ParticleGeneration.Position(random, index, count, species, speciesCount, parameters, generation);
        Vector2d velocity = ParticleGeneration.Velocity(random, position, parameters, generation);
        WorldBoundary.Map(ref position, ref velocity, parameters);
        return (position, velocity);
    }

    private static GeneratorMetadata Metadata(string name, int? seed) => new(name, "1", seed, RuntimeInformation.FrameworkDescription);

    private sealed record EditorValues(SimulationConfiguration Configuration, GenerationSettings Generation,
        AppearanceSettings Appearance, PlaybackSettings Playback, int[] Populations);

    private sealed record UndoEntry(string Label, EditorValues Before, EditorValues After,
        ParticleSnapshot? BeforeState, ParticleSnapshot? AfterState, bool RestoreMotion, HashSet<long>? RestoreIdentities = null);
}
