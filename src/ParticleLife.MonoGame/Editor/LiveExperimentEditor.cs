using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ParticleLife.Core.Experiments;
using ParticleLife.Core.Models;
using ParticleLife.Core.Numerics;
using ParticleLife.Viewer;

namespace ParticleLife.MonoGame.Editor;

/// <summary>Connects retained native controls to the shared, graphics-free experiment editor.</summary>
/// <remarks>Owns only UI drafts and selection. The controller validates and owns numerical changes,
/// undo payloads and replay events. Draw never edits a model or retains a state observation.</remarks>
internal sealed class LiveExperimentEditor
{
    private readonly ExperimentEditor editor;
    private readonly UiPanel panel;
    private readonly Action<string> report;
    private readonly Action printTimings;
    private readonly InteractionMatrixWidget matrix;
    private readonly ExperimentFileBrowser browser;
    private long observedRevision = -1;
    private bool symmetry;
    private bool populationDraft;
    private bool worldDraft;
    private int[] populations = [];
    private int populationSpecies;
    private int populationIndex;
    private int colourSpecies;
    private double width;
    private double height;
    private double wallWidth;
    private double rememberedDamping = 0.025;
    private double rememberedWall = 1.44;
    private int rememberedCadence = 144;
    private string populationSummary = string.Empty;
    private PresetKind selectedRecipe = PresetKind.CoreHalo;
    private string recipePopulationSummary = string.Empty;
    private readonly UiSection recipes;
    private readonly UiSection interactions;
    private readonly UiSection forces;
    private readonly UiSection population;
    private readonly UiSection generation;
    private readonly UiSection simulation;
    private readonly UiSection appearance;
    internal ForceGraph Graph { get; }
    internal bool SuppressPlayback { get; private set; }
    internal bool IsModal => browser.IsOpen;
    internal bool CapturesKeyboard => browser.IsOpen || panel.Capturing;
    internal bool ConsumedKeyboard { get; private set; }
    internal Rectangle PanelBounds => panel.Bounds;
    internal Rectangle PanelBody => panel.Body;
    internal Rectangle BrowserBounds => browser.Bounds;
    internal Rectangle BrowserBody => browser.Body;

    internal LiveExperimentEditor(ExperimentEditor editor, string saveDirectory, Action<string> report, Action printTimings)
    {
        this.editor = editor;
        this.report = report;
        this.printTimings = printTimings;
        panel = new UiPanel(Error);
        matrix = new InteractionMatrixWidget(editor);
        Graph = new ForceGraph(editor, matrix);
        browser = new ExperimentFileBrowser(editor, saveDirectory, report, () => SuppressPlayback = true, ClearDrafts);
        panel.Toolbar.Add(new UiRow(
            Button(() => editor.Playback.IsPaused ? "RESUME" : "PAUSE", () =>
            {
                editor.Playback.SetPaused(!editor.Playback.IsPaused);
                report(editor.Playback.IsPaused ? "Paused. Edit any rule or use Step to advance once." : "Playing. Live rule edits preserve the current particles and clock.");
                SuppressPlayback = true;
            }, "Pause/resume clears pending playback time. No catch-up burst follows a pause."),
            Button(() => "STEP", () => { editor.Playback.SingleStep(); SuppressPlayback = true; }, "Pause and complete exactly one numerical step."),
            Button(() => "RESET", Reset, "Restore the defined baseline state and configuration at time/step zero; clear undo and start a fresh replay timeline.")));
        panel.Toolbar.Add(new UiRow(
            new UiButton(() => "UNDO", () => editor.Undo(), "Undo at the current clock. Membership undo preserves surviving motion and restores removed particles from saved state; resampling undo restores captured positions/velocities.", () => editor.CanUndo),
            new UiButton(() => "REDO", () => editor.Redo(), "Reapply an undone editor operation at the current boundary. A new edit clears redo.", () => editor.CanRedo),
            Button(() => "FILES", OpenFiles, "Save named presets, exact states, initial experiments and sessions; browse local files and replay in-app.")));

        recipes = Section("BUILT-IN RECIPES");
        interactions = Section("INTERACTIONS", true);
        forces = Section("FORCES AND MOTION");
        population = Section("WORLD AND POPULATION");
        generation = Section("GENERATION RECIPE");
        simulation = Section("SIMULATION AND EXECUTION");
        appearance = Section("APPEARANCE");
        SelectRecipe(selectedRecipe);
        BuildRecipes();
        BuildInteractions();
        BuildForces();
        BuildPopulation();
        BuildGeneration();
        BuildSimulation();
        BuildAppearance();
        Synchronize();
    }

    private UiSection Section(string name, bool expanded = false)
    {
        UiSection section = new(name, expanded);
        panel.Content.Add(section);
        return section;
    }

    private static UiButton Button(Func<string> label, Action action, string help) => new(label, action, help);

    private UiNumber Number(string label, Func<double> get, Action<double> set, double reset, double min,
        double max, string help, bool log = false, bool integer = false, bool gesture = true, Func<double>? maximumValue = null) =>
        new(label, get, set, reset, min, max, help, log, integer,
            gesture ? editor.BeginGesture : null, gesture ? editor.EndGesture : null, maximumValue);

    /// <summary>Offers complete starting recipes separately from compatible matrix-only live edits.</summary>
    private void BuildRecipes()
    {
        recipes.Children.Add(new UiLabel(() => "SELECT A RECIPE. SELECTION ALONE DOES NOT CHANGE THE SIMULATION.", 42));
        for (int index = 0; index < PresetCatalog.All.Count; index += 2)
        {
            UiButton first = RecipeButton(PresetCatalog.All[index]);
            recipes.Children.Add(index + 1 < PresetCatalog.All.Count
                ? new UiRow(first, RecipeButton(PresetCatalog.All[index + 1]))
                : first);
        }
        recipes.Children.Add(new UiLabel(() => PresetCatalog.Get(selectedRecipe).Description, 94));
        recipes.Children.Add(new UiLabel(() =>
        {
            PresetInfo recipe = PresetCatalog.Get(selectedRecipe);
            return $"NEW: {recipe.DefaultParticleCount} PARTICLES / {recipe.DefaultSpeciesCount} SPECIES{recipePopulationSummary}. SEED {editor.Generation.Seed}. RESETS RULES AND CLOCK.";
        }, 62, UiCanvas.Accent));
        recipes.Children.Add(Button(() => "NEW FROM RECIPE", StartSelectedRecipe,
            "Start the complete suggested recipe: default world, forces, timestep and populations; uniform positions, zero velocities, current seed. Establish a new reset baseline. Retain appearance, playback and execution choices."));
        recipes.Children.Add(new UiButton(() => "APPLY MATRIX LIVE", ApplySelectedRecipeMatrix,
            "Apply only the selected interaction matrix. Retain particles, populations, forces, generation settings and clock. Requires a compatible current species count; use New from recipe for the complete starting configuration.",
            () => PresetCatalog.SupportsSpeciesCount(selectedRecipe, editor.Configuration.Matrix.SpeciesCount)));
        recipes.Children.Add(new UiLabel(() => PresetCatalog.SupportsSpeciesCount(selectedRecipe, editor.Configuration.Matrix.SpeciesCount)
            ? "MATRIX LIVE RETAINS PARTICLES, POPULATIONS AND CLOCK. * = EXPERIMENTAL."
            : $"MATRIX LIVE NEEDS A COMPATIBLE SPECIES COUNT; TRY NEW FROM RECIPE ({PresetCatalog.Get(selectedRecipe).DefaultSpeciesCount} SPECIES). * = EXPERIMENTAL.", 64));
    }

    private UiButton RecipeButton(PresetInfo recipe) => Button(
        () => (selectedRecipe == recipe.Kind ? "> " : "") + recipe.Name.ToUpperInvariant() + (recipe.IsExperimental ? " *" : ""),
        () => SelectRecipe(recipe.Kind), recipe.Description);

    /// <summary>Selects a catalogue entry without mutating any simulation or generation setting.</summary>
    internal void SelectRecipe(PresetKind kind)
    {
        PresetInfo recipe = PresetCatalog.Get(kind);
        selectedRecipe = kind;
        if (kind == PresetKind.CoreHalo)
        {
            int[] counts = PresetCatalog.CreatePopulations(kind, recipe.DefaultParticleCount, recipe.DefaultSpeciesCount);
            recipePopulationSummary = $" ({counts[0]} CORE / {counts[1]} HALO)";
        }
        else
        {
            recipePopulationSummary = string.Empty;
        }
    }

    /// <summary>Starts the selected complete recipe through the same controller used by headless checks.</summary>
    internal void StartSelectedRecipe()
    {
        editor.NewRecipe(selectedRecipe);
        ClearDrafts();
        matrix.Synchronize();
        SuppressPlayback = true;
        PresetInfo recipe = PresetCatalog.Get(selectedRecipe);
        report($"Started {recipe.Name}{(recipe.IsExperimental ? " (experimental)" : "")}: {recipe.DefaultParticleCount} particles, {recipe.DefaultSpeciesCount} species. New baseline at time/step zero.");
    }

    /// <summary>Applies the selected matrix without adopting its population or other recommended settings.</summary>
    internal void ApplySelectedRecipeMatrix() => Template(selectedRecipe);

    private void BuildInteractions()
    {
        interactions.Children.Add(matrix);
        interactions.Children.Add(new UiLabel(() => $"SELECTED {matrix.Receiver} <- {matrix.Source}   A={editor.Configuration.Matrix[matrix.Receiver, matrix.Source]:G8}", 25, UiCanvas.Accent));
        interactions.Children.Add(Number("COEFFICIENT A", () => editor.Configuration.Matrix[matrix.Receiver, matrix.Source],
            value => editor.SetMatrixCoefficient(matrix.Receiver, matrix.Source, value, symmetry), 0, -1, 1,
            "A[receiver,source] is dimensionless, in [-1,1]. Positive outer values attract; negative repel. Core repulsion is independent of A."));
        interactions.Children.Add(new UiToggle("LINK REVERSE WHILE EDITING", () => symmetry, value => symmetry = value,
            "Optional editing symmetry: the next coefficient edit also sets A[source,receiver]. The solver remains directed."));
        interactions.Children.Add(new UiRow(
            Button(() => "RANDOM", () => Template(PresetKind.Random), "Apply a seeded random matrix live. Preserve particles, time and completed steps."),
            Button(() => "CHAINS", () => Template(PresetKind.Chains), "Apply the Chains matrix live without regenerating particles or resetting the clock."),
            Button(() => "SNAKES", () => Template(PresetKind.Snakes), "Apply the Snakes matrix live without regenerating particles or resetting the clock.")));
        interactions.Children.Add(Button(() => "RANDOMIZE MATRIX WITH NEW SEED [M]", RandomizeMatrix,
            "Choose a fresh recorded seed and change the live matrix only. Random template above intentionally uses the generation recipe's existing seed."));
        interactions.Children.Add(Button(() => "RESET MATRIX TO ZERO", editor.ResetMatrix,
            "Set all directed outer coefficients to zero. The species-independent repulsive core remains."));
        interactions.Children.Add(Number("SELECT RECEIVER", () => matrix.Receiver,
            value => matrix.Select(Integer(value), matrix.Source), 0, 0, 31,
            "Exact receiver row index. This selects an editor cell and does not change any rule.", integer: true, gesture: false,
            maximumValue: () => editor.Configuration.Matrix.SpeciesCount - 1));
        interactions.Children.Add(Number("SELECT SOURCE", () => matrix.Source,
            value => matrix.Select(matrix.Receiver, Integer(value)), 0, 0, 31,
            "Exact source column index. Rows receive forces from columns; the reverse direction may be different.", integer: true, gesture: false,
            maximumValue: () => editor.Configuration.Matrix.SpeciesCount - 1));
        interactions.Children.Add(new UiToggle("GRAPH: DIMENSIONLESS COEFFICIENT", () => Graph.ShowCoefficient, value => Graph.ShowCoefficient = value,
            "Off plots F0*g in reduced force units. On plots dimensionless g. Neither curve is a potential energy."));
        interactions.Children.Add(new UiToggle("GRAPH: SHOW REVERSE (YELLOW)", () => Graph.ShowReverse, value => Graph.ShowReverse = value,
            "Cyan is the selected receiver <- source direction; dashed yellow is the reverse directed coefficient."));
        interactions.Children.Add(new ForceGraphWidget(Graph));
    }

    private void BuildForces()
    {
        forces.Children.Add(Number("CUTOFF R [L]", () => Parameters.Cutoff, value => ParametersChange(Parameters with { Cutoff = value }),
            0.1, 0.005, 1, "Finite positive reduced length; R squared must be finite and positive. Slider is logarithmic; typed valid values are unrestricted.", log: true));
        forces.Children.Add(Number("FORCE F0 [F]", () => Parameters.ForceScale, value => ParametersChange(Parameters with { ForceScale = value }),
            1, 0, 10, "Finite nonnegative pair-force scale in reduced force units. Zero disables pair forces. Unit particle mass is assumed."));
        forces.Children.Add(Number("CORE BETA [1]", () => Parameters.RepulsionFraction, value => ParametersChange(Parameters with { RepulsionFraction = value }),
            0.3, 0.01, 0.99, "Dimensionless core fraction, strictly 0 < beta < 1. The core ends at distance beta*R."));
        forces.Children.Add(new UiToggle("DAMPING ENABLED", () => Parameters.DampingHalfLife is not null, value =>
        {
            if (Parameters.DampingHalfLife is double current)
            {
                rememberedDamping = current;
            }
            ParametersChange(Parameters with { DampingHalfLife = value ? rememberedDamping : null });
        }, "Off means a null half-life: no damping. It does not set a tiny/zero half-life. Re-enabling restores the previous enabled value."));
        forces.Children.Add(Number("HALF-LIFE [T]", () => Parameters.DampingHalfLife ?? rememberedDamping, value =>
        {
            ParametersChange(Parameters with { DampingHalfLife = value });
            rememberedDamping = value;
        }, 0.025, 0.001, 10, "Finite positive reduced time. Entering a half-life enables damping. Velocity factor per step is 2^(-dt/halfLife).", log: true));
        forces.Children.Add(new UiToggle("SOFT WALL FORCE ENABLED", () => Parameters.WallStrength > 0, value =>
        {
            if (Parameters.WallStrength > 0)
            {
                rememberedWall = Parameters.WallStrength;
            }
            ParametersChange(Parameters with { WallStrength = value ? rememberedWall : 0 });
        }, "Off sets wall strength to zero. No reflecting or periodic boundary is introduced; finite off-world particles remain valid."));
        forces.Children.Add(Number("WALL WIDTH [L]", () => Parameters.WallWidth, value => ParametersChange(Parameters with { WallWidth = value }),
            0.05, 0.001, 0.5, "Positive layer width, at most half the smaller world dimension. Coupled world-size/width changes can be applied together under World.", log: true));
        forces.Children.Add(Number("WALL STRENGTH [F]", () => Parameters.WallStrength, value => ParametersChange(Parameters with { WallStrength = value }),
            1.44, 0, 10, "Finite nonnegative reduced force. The linear soft wall extends outside the nominal rectangle. Zero disables this force."));
    }

    private void BuildPopulation()
    {
        population.Children.Add(new UiLabel(() => "WORLD DRAFT: APPLY ALL THREE TOGETHER. ABSOLUTE COORDINATES AND VELOCITIES STAY.", 44));
        population.Children.Add(Number("WORLD WIDTH [L]", () => width, value => { width = value; worldDraft = true; }, 1, 0.1, 10,
            "Draft finite positive width. Apply world validates width, height and wall layer together; particles are not rescaled.", log: true, gesture: false));
        population.Children.Add(Number("WORLD HEIGHT [L]", () => height, value => { height = value; worldDraft = true; }, 1, 0.1, 10,
            "Draft finite positive height. Existing off-world particles remain simulated after shrinking.", log: true, gesture: false));
        population.Children.Add(Number("WALL WIDTH [L]", () => wallWidth, value => { wallWidth = value; worldDraft = true; }, 0.05, 0.001, 0.5,
            "Coupled draft layer width; must be at most min(width,height)/2 when Apply world is pressed.", log: true, gesture: false));
        population.Children.Add(Button(() => worldDraft ? "APPLY WORLD *" : "APPLY WORLD", () =>
        {
            ParametersChange(Parameters with { WorldWidth = width, WorldHeight = height, WallWidth = wallWidth });
            worldDraft = false;
            SuppressPlayback = true;
            report("World rectangle changed; absolute positions, velocities and clock retained.");
        }, "Atomically apply these three world/wall drafts. Invalid combinations leave every live parameter unchanged."));
        population.Children.Add(new UiLabel(() => "POPULATION DRAFT: EARLIEST SURVIVORS KEEP IDS/STATE. REMOVED SPECIES REMAP MODULO. ADDITIONS GET NEW IDS.", 60));
        population.Children.Add(Number("TOTAL PARTICLES", () => populations.Sum(), value => Distribute(Integer(value), populationSpecies),
            500, 0, 5000, "Changing this draft distributes the target total evenly. No particle changes until Apply live. Exact larger valid totals can be typed.", integer: true, gesture: false));
        population.Children.Add(Number("SPECIES COUNT", () => populationSpecies, value => Distribute(populations.Sum(), Integer(value)),
            7, 1, 32, "Changing this draft redistributes targets evenly. Apply live retains unaffected matrix entries; new entries are zero. Removed species remap modulo the new count.", integer: true, gesture: false));
        population.Children.Add(new UiLabel(() => populationSummary, 50, UiCanvas.Accent));
        population.Children.Add(Number("SPECIES TO EDIT", () => populationIndex, value =>
        {
            int index = Integer(value);
            if (index < 0 || index >= populationSpecies)
            {
                throw new ArgumentOutOfRangeException(nameof(value), $"Use 0..{populationSpecies - 1}.");
            }
            populationIndex = index;
        }, 0, 0, 31, "Select a draft species, then edit its individual target below. Selection does not change the population.", integer: true, gesture: false,
            maximumValue: () => populationSpecies - 1));
        population.Children.Add(Number("SPECIES TARGET", () => populations.Length == 0 ? 0 : populations[populationIndex], value =>
        {
            int count = Integer(value);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            ValidateDraftPopulation(populations, populationIndex, count);
            populations[populationIndex] = count;
            populationDraft = true;
            UpdatePopulationSummary();
        }, 0, 0, 2000, "Exact count for the selected draft species; the total updates accordingly. Zero particles and empty species are supported.", integer: true, gesture: false));
        population.Children.Add(Button(() => populationDraft ? "APPLY TO LIVE POPULATION *" : "APPLY TO LIVE POPULATION", () =>
        {
            editor.ApplyPopulation(populationSpecies, populations);
            populationDraft = false;
            SuppressPlayback = true;
            report("Population applied: surviving identities/state retained; actual additions recorded in the session.");
        }, "Apply all targets atomically; additions use the recipe. Undo keeps current motion for survivors and restores removed particles from saved state, at the current clock."));
        population.Children.Add(Button(() => "SAVE COUNTS AS GENERATION RECIPE", () =>
        {
            editor.SetPopulationRecipe(populations);
            report("Generation count recipe saved. Current particles are unchanged.");
        }, "Change only future generation counts. Recipe species count must match the active matrix; apply species changes explicitly first."));
        population.Children.Add(Button(() => "DISCARD POPULATION / WORLD DRAFTS", () =>
        {
            populationDraft = worldDraft = false;
            observedRevision = -1;
            Synchronize();
        }, "Reload these unapplied fields from the current experiment without changing any live state."));
    }

    private void BuildGeneration()
    {
        generation.Children.Add(new UiLabel(() => "RECIPE ONLY: THESE VALUES DO NOT MOVE PARTICLES UNTIL AN EXPLICIT GENERATION ACTION.", 44));
        generation.Children.Add(new UiLabel(() => $"SAVED RECIPE: {RecipeTotal()} PARTICLES / {editor.Populations.Length} SPECIES. EDIT FUTURE COUNTS UNDER WORLD AND POPULATION.", 44, UiCanvas.Accent));
        generation.Children.Add(Number("GENERATION SEED", () => editor.Generation.Seed,
            value => editor.SetGeneration(editor.Generation with { Seed = Integer(value) }), 1, 0, 10000,
            "Integer seed for future generation and random matrices. Changing it alone never regenerates the running state. Replay saves actual generated values.", integer: true));
        generation.Children.Add(Button(() => $"PLACEMENT: {editor.Generation.Placement}", () =>
            editor.SetGeneration(editor.Generation with { Placement = (PlacementKind)(((int)editor.Generation.Placement + 1) % 3) }),
            "Cycle uniform, regular grid and central disk placement. Used only for explicit new/resampled/added particles."));
        generation.Children.Add(Button(() => $"VELOCITY: {editor.Generation.Velocity}", () =>
            editor.SetGeneration(editor.Generation with { Velocity = (VelocityKind)(((int)editor.Generation.Velocity + 1) % 3) }),
            "Cycle zero, common direction and random direction. Speed/direction are generation settings, not live velocity overrides."));
        generation.Children.Add(Number("INITIAL SPEED [L/T]", () => editor.Generation.Speed,
            value => editor.SetGeneration(editor.Generation with { Speed = value }), 0, 0, 3,
            "Finite nonnegative reduced speed for newly generated velocities. Zero mode still creates zero velocity."));
        generation.Children.Add(Number("DIRECTION [RAD]", () => editor.Generation.DirectionRadians,
            value => editor.SetGeneration(editor.Generation with { DirectionRadians = value }), 0, -Math.PI, Math.PI,
            "Finite angle in radians for common-direction initial velocities. Slider spans -pi..pi; exact angles may be typed."));
        generation.Children.Add(Button(() => "RESAMPLE LIVE PARTICLES", () =>
        {
            editor.ResampleParticles();
            SuppressPlayback = true;
            report("Resampled positions/velocities using the recipe; identities, rules and clock retained.");
        }, "Explicitly generate new positions and velocities for existing particles. Preserve identities, species, matrix, time and steps; record the actual new values."));
        generation.Children.Add(new UiLabel(() => "NEW EXPERIMENT: REGENERATES ALL PARTICLES; SETS A NEW RESET BASELINE AT TIME/STEP ZERO.", 44));
        generation.Children.Add(new UiRow(
            Button(() => "NEW RANDOM", () => NewExperiment(PresetKind.Random), "Generate a new population and random matrix from the recipe, clearing incompatible undo/history."),
            Button(() => "NEW CHAINS", () => NewExperiment(PresetKind.Chains), "Generate a new population and Chains matrix, establishing a new baseline."),
            Button(() => "NEW SNAKES", () => NewExperiment(PresetKind.Snakes), "Generate a new population and Snakes matrix, establishing a new baseline.")));
    }

    private void BuildSimulation()
    {
        simulation.Children.Add(Number("TIMESTEP DT [T]", () => editor.Configuration.TimeStep,
            value => ConfigurationChange(timeStep: value), 1.0 / 144, 0.0001, 0.1,
            "Finite positive fixed integration timestep. Applies to future increments; elapsed simulation time is not recomputed. Clears fractional playback backlog.", log: true));
        simulation.Children.Add(Number("PLAYBACK SPEED [X]", () => editor.Playback.PlaybackSpeed,
            value => { editor.SetPlayback(PlaybackSettings.Capture(editor.Playback) with { Speed = value }); SuppressPlayback = true; }, 1, 0.05, 8,
            "Wall-clock speed only; does not alter dt or equations. Changing speed clears pending playback time.", log: true));
        simulation.Children.Add(Button(() => $"BACKEND: {editor.Configuration.Backend}", () =>
            ConfigurationChange(backend: editor.Configuration.Backend == ForceBackend.Direct ? ForceBackend.UniformGrid : ForceBackend.Direct, workers: 1),
            "Cycle direct reference / exact uniform grid. A switch selects one worker; choose grid workers separately. Rules and completed state are retained."));
        simulation.Children.Add(Number("WORKERS", () => editor.Configuration.WorkerCount,
            value => ConfigurationChange(workers: Integer(value)), 1, 1, 8,
            "Explicit worker count including caller, 1..8. Direct requires exactly one; invalid combinations are rejected atomically.", integer: true));
        simulation.Children.Add(Number("MAX STEPS / FRAME", () => editor.Playback.MaxStepsPerFrame,
            value => editor.SetPlayback(PlaybackSettings.Capture(editor.Playback) with { MaxStepsPerFrame = Integer(value) }), 8, 1, 32,
            "Positive catch-up cap. Remaining whole-step playback time is dropped and displayed. It does not bound the cost of one numerical step.", integer: true));
        simulation.Children.Add(new UiToggle("SAMPLED DIAGNOSTICS ENABLED", () => editor.Playback.DiagnosticEverySteps > 0,
            SetDiagnosticsEnabled, "Independent measurements after completed steps. Turning them off changes no numerical force or trajectory."));
        simulation.Children.Add(Number("DIAGNOSTIC CADENCE", () => editor.Playback.DiagnosticEverySteps,
            value => SetDiagnosticCadence(Integer(value)), 144, 0, 1440,
            "Nonnegative completed-step cadence. Zero disables samples. Editing clears stale measurements; wall-clock frame rate is irrelevant.", integer: true));
        simulation.Children.Add(new UiToggle("CONNECTIVITY FOLLOWS CUTOFF", () => editor.Playback.DiagnosticRadius is null,
            value => editor.SetPlayback(PlaybackSettings.Capture(editor.Playback) with { DiagnosticRadius = value ? null : Parameters.Cutoff }),
            "When enabled, connectivity measurements use the current interaction cutoff. A separate radius changes diagnostics only."));
        simulation.Children.Add(Number("CONNECTIVITY R [L]", () => editor.Playback.DiagnosticRadius ?? Parameters.Cutoff,
            value => editor.SetPlayback(PlaybackSettings.Capture(editor.Playback) with { DiagnosticRadius = value }), 0.1, 0.001, 2,
            "Finite positive diagnostic connection length. Entering a value selects an independent radius and clears previous samples.", log: true));
        simulation.Children.Add(new UiLabel(() => $"REQUESTED {editor.Configuration.Backend}/{editor.Configuration.WorkerCount}; ACTUAL {editor.Playback.LastExecution?.ActualBackend.ToString() ?? "UNSTEPPED"}/{editor.Playback.LastExecution?.ActualWorkerCount.ToString() ?? "-"}. FALLBACK {editor.Playback.LastExecution?.FallbackReason.ToString() ?? "NONE"}.", 62));
        simulation.Children.Add(Button(() => "PRINT VIEWER TIMINGS [F9]", printTimings,
            "Print the latest approximately one-second FPS, controller update, editor input/update and render submission timings to the launch console. Present is excluded."));
    }

    private void BuildAppearance()
    {
        appearance.Children.Add(new UiLabel(() => "DISPLAY ONLY. THESE SETTINGS CANNOT CHANGE TRAJECTORIES OR PHYSICAL LENGTHS.", 44));
        appearance.Children.Add(Number("PARTICLE RADIUS [PX]", () => editor.Appearance.ParticleRadius,
            value => editor.SetAppearance(editor.Appearance with { ParticleRadius = value }), 2.5, 0.5, 12,
            "Display radius in pixels. Independent of cutoff, wall width and diagnostic connectivity."));
        appearance.Children.Add(Number("UI SCALE [X]", () => editor.Appearance.UiScale,
            value => editor.SetAppearance(editor.Appearance with { UiScale = value }), 1, 0.75, 1.5,
            "Scale editor controls and text. World geometry continues to use one isotropic scale."));
        appearance.Children.Add(Number("COLOUR SPECIES", () => colourSpecies, value =>
        {
            int index = Integer(value);
            if (index < 0 || index >= editor.Configuration.Matrix.SpeciesCount)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Choose an existing species.");
            }
            colourSpecies = index;
        }, 0, 0, 31, "Select the species colour to edit. Matrix row/column labels use the same colour.", integer: true, gesture: false,
            maximumValue: () => editor.Configuration.Matrix.SpeciesCount - 1));
        appearance.Children.Add(new UiTextField("SPECIES COLOUR #RRGGBB", () => $"#{editor.Appearance.ColorForSpecies(colourSpecies):X6}", text =>
        {
            string value = text.TrimStart('#');
            if (value.Length != 6 || !uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
            {
                throw new ArgumentException("Enter exactly six hexadecimal digits, such as 64D9C6.");
            }
            editor.SetAppearance(editor.Appearance.WithSpeciesColor(colourSpecies, rgb));
        }, "Exact RGB colour, six hexadecimal digits with an optional #. This is presentation data only."));
        for (int component = 0; component < 3; component++)
        {
            int shift = (2 - component) * 8;
            string label = component == 0 ? "RED [0..255]" : component == 1 ? "GREEN [0..255]" : "BLUE [0..255]";
            appearance.Children.Add(Number(label, () => (editor.Appearance.ColorForSpecies(colourSpecies) >> shift) & 255,
                value =>
                {
                    int channel = Integer(value);
                    if (channel is < 0 or > 255)
                    {
                        throw new ArgumentOutOfRangeException(nameof(value), "RGB channels are in 0..255.");
                    }
                    uint current = editor.Appearance.ColorForSpecies(colourSpecies);
                    editor.SetAppearance(editor.Appearance.WithSpeciesColor(colourSpecies,
                        current & ~(255u << shift) | (uint)channel << shift));
                }, 128, 0, 255, "Presentation-only colour component. Use the hex field for an exact RGB value.", integer: true));
        }
        appearance.Children.Add(Button(() => "RESET SELECTED SPECIES COLOUR", () =>
        {
            editor.SetAppearance(editor.Appearance.WithSpeciesColor(colourSpecies, new AppearanceSettings().ColorForSpecies(colourSpecies)));
        }, "Restore this species' deterministic default display colour."));
        appearance.Children.Add(new UiToggle("WORLD OVERLAY", () => editor.Appearance.ShowOverlay,
            value => editor.SetAppearance(editor.Appearance with { ShowOverlay = value }), "Show state, timing and execution information over the world."));
        appearance.Children.Add(new UiToggle("DIAGNOSTIC OVERLAY", () => editor.Appearance.ShowDiagnostics,
            value => editor.SetAppearance(editor.Appearance with { ShowDiagnostics = value }), "Show the latest sample with its own time and step. This does not enable or disable measurement."));
        appearance.Children.Add(new UiToggle("KEYBOARD HELP", () => editor.Appearance.ShowHelp,
            value => editor.SetAppearance(editor.Appearance with { ShowHelp = value }), "Show keyboard hints beside the world. Shortcuts are disabled while editing fields or interacting with controls."));
        appearance.Children.Add(new UiToggle("EDITOR VISIBLE [E]", () => editor.Appearance.ShowEditor,
            value => editor.SetAppearance(editor.Appearance with { ShowEditor = value }), "Hide the editor and graph. Press E or click EDITOR at the top to show it again."));
    }

    private ModelParameters Parameters => editor.Configuration.Parameters;

    private int RecipeTotal()
    {
        int total = 0;
        foreach (int count in editor.Populations)
        {
            total += count;
        }
        return total;
    }

    private void ParametersChange(ModelParameters parameters) => ConfigurationChange(parameters: parameters);

    private void ConfigurationChange(ModelParameters? parameters = null, double? timeStep = null, ForceBackend? backend = null, int? workers = null)
    {
        SimulationConfiguration current = editor.Configuration;
        editor.ApplyConfiguration(new SimulationConfiguration(parameters ?? current.Parameters, current.Matrix,
            timeStep ?? current.TimeStep, backend ?? current.Backend, workers ?? current.WorkerCount));
    }

    private static int Integer(double value)
    {
        if (value != Math.Truncate(value) || value < int.MinValue || value > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Enter a supported whole number.");
        }
        return (int)value;
    }

    internal static void ValidateDraftPopulation(ReadOnlySpan<int> counts, int index, int replacement)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(replacement);
        long total = replacement;
        for (int current = 0; current < counts.Length; current++)
        {
            if (current != index)
            {
                total += counts[current];
            }
        }
        if (total > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(replacement), "Combined population exceeds the supported whole-number total.");
        }
    }

    private void ClearDrafts()
    {
        populationDraft = worldDraft = false;
        observedRevision = -1;
    }

    private void Distribute(int total, int species)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(total);
        ArgumentOutOfRangeException.ThrowIfLessThan(species, 1);
        if (species > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(species), "The editor supports up to 4096 draft species.");
        }
        populations = new int[species];
        for (int index = 0; index < species; index++)
        {
            populations[index] = total / species + (index < total % species ? 1 : 0);
        }
        populationSpecies = species;
        populationIndex = Math.Min(populationIndex, species - 1);
        populationDraft = true;
        UpdatePopulationSummary();
    }

    private void UpdatePopulationSummary()
    {
        string entries = string.Join("  ", populations.Take(12).Select((count, index) => $"S{index}:{count}"));
        populationSummary = entries + (populations.Length > 12 ? " ... USE SPECIES SELECTOR" : string.Empty);
    }

    private void Synchronize()
    {
        if (observedRevision == editor.Revision)
        {
            return;
        }
        observedRevision = editor.Revision;
        matrix.Synchronize();
        colourSpecies = Math.Min(colourSpecies, editor.Configuration.Matrix.SpeciesCount - 1);
        if (!worldDraft)
        {
            width = Parameters.WorldWidth;
            height = Parameters.WorldHeight;
            wallWidth = Parameters.WallWidth;
        }
        if (!populationDraft)
        {
            populations = editor.GetCurrentPopulations();
            populationSpecies = populations.Length;
            populationIndex = Math.Min(populationIndex, populationSpecies - 1);
            UpdatePopulationSummary();
        }
    }

    internal void Update(Rectangle bounds, Rectangle modalBounds, float scale, MouseState mouse,
        IReadOnlyList<UiInputEvent> input)
    {
        SuppressPlayback = false;
        ConsumedKeyboard = false;
        panel.Arrange(bounds);
        browser.Arrange(modalBounds);
        Synchronize();
        if (editor.IsReplaying)
        {
            ConsumedKeyboard = true;
            SuppressPlayback = true;
            if (input.Any(entry => !entry.IsText && entry.Key == Keys.Escape))
            {
                editor.CancelSessionReplay();
                browser.SetStatus("Replay cancelled. The previous completed experiment is retained, paused.");
                report("Session replay cancelled; previous completed experiment retained, paused.");
            }
            else
            {
                try
                {
                    bool complete = editor.AdvanceSessionReplay(8);
                    if (complete)
                    {
                        browser.SetStatus("Replay complete at the recorded terminal boundary, paused. Close the library to inspect or continue experimenting.");
                        report("Session replay completed at the recorded terminal boundary; paused. Continue experimenting or reset to its starting baseline.");
                        ClearDrafts();
                        Synchronize();
                    }
                }
                catch (Exception exception) when (ViewerErrors.IsExpected(exception))
                {
                    editor.CancelSessionReplay();
                    browser.SetStatus("Replay failed: " + exception.Message + ". Previous completed experiment retained.");
                    throw;
                }
            }
        }
        else if (browser.IsOpen)
        {
            ConsumedKeyboard = true;
            browser.Update(modalBounds, scale, mouse, input);
        }
        else if (editor.Appearance.ShowEditor)
        {
            panel.Bounds = bounds;
            panel.Update(mouse, scale, input);
            ConsumedKeyboard = panel.ConsumedKeyboard;
        }
        else
        {
            panel.ClearFocus();
        }
        Synchronize();
    }

    internal void LoseFocus()
    {
        panel.ClearFocus();
        editor.EndGesture();
    }

    internal void OpenFiles()
    {
        panel.ClearFocus();
        browser.Open();
    }

    internal void ToggleVisibility()
    {
        panel.ClearFocus();
        editor.SetAppearance(editor.Appearance with { ShowEditor = !editor.Appearance.ShowEditor });
    }

    internal void Reset()
    {
        editor.Reset();
        populationDraft = worldDraft = false;
        SuppressPlayback = true;
        report("Reset experiment: exact baseline state and rules restored at time/step zero; fresh replay timeline.");
    }

    internal void ReloadExperiment(string path, ForceBackend? backendOverride, int? workerOverride, int? diagnosticEveryStepsOverride = null)
    {
        editor.LoadExperiment(path, backendOverride, workerOverride, diagnosticEveryStepsOverride);
        ClearDrafts();
        SuppressPlayback = true;
        report(backendOverride is not null || workerOverride is not null
            ? "Reloaded startup experiment with explicit execution overrides, paused."
            : "Reloaded startup experiment with its saved execution settings, paused.");
    }

    internal void Template(PresetKind kind)
    {
        editor.ApplyMatrixTemplate(kind, editor.Generation.Seed);
        PresetInfo recipe = PresetCatalog.Get(kind);
        report($"{recipe.Name}{(recipe.IsExperimental ? " (experimental)" : "")} matrix applied live. Particles, velocities, time and completed steps retained.");
    }

    /// <summary>Uses the same undoable diagnostic operation for the shortcut and the native toggle.</summary>
    internal void ToggleDiagnostics() => SetDiagnosticsEnabled(editor.Playback.DiagnosticEverySteps == 0);

    private void SetDiagnosticsEnabled(bool enabled)
    {
        SetDiagnosticCadence(enabled ? rememberedCadence : 0);
    }

    /// <summary>Applies a typed or toggled cadence through history, retaining the last positive value.</summary>
    internal void SetDiagnosticCadence(int cadence)
    {
        int previous = editor.Playback.DiagnosticEverySteps;
        editor.SetPlayback(PlaybackSettings.Capture(editor.Playback) with
        {
            DiagnosticEverySteps = cadence
        });
        if (cadence > 0 || previous > 0)
        {
            rememberedCadence = cadence > 0 ? cadence : previous;
        }
    }

    internal void RandomizeMatrix()
    {
        int seed = RandomNumberGenerator.GetInt32(int.MaxValue);
        editor.ApplyMatrixTemplate(PresetKind.Random, seed);
        report($"Randomized matrix live with recorded seed {seed}. Particles and clock retained; the generation recipe is unchanged.");
    }

    private void NewExperiment(PresetKind kind)
    {
        editor.NewExperiment(kind, $"{kind} experiment");
        populationDraft = worldDraft = false;
        SuppressPlayback = true;
        report($"New {kind} experiment generated. New baseline at time/step zero.");
    }

    private void Error(string message) => report("Edit rejected: " + message);
    internal void DrawChrome(UiCanvas canvas) => panel.DrawChrome(canvas, "LIVE EXPERIMENT EDITOR");
    internal void DrawBody(UiCanvas canvas) => panel.DrawBody(canvas);
    internal void DrawBrowserChrome(UiCanvas canvas)
    {
        browser.DrawChrome(canvas);
        if (editor.IsReplaying)
        {
            Rectangle box = new(browser.Bounds.X + 20, browser.Bounds.Y + 140, browser.Bounds.Width - 40, 80);
            canvas.Fill(box, UiCanvas.Raised);
            canvas.Text($"REPLAYING {editor.ReplayCompletedStep} / {editor.ReplayTerminalStep}", box.X + 14, box.Y + 17, UiCanvas.Accent);
            canvas.Text("ESC CANCELS. REPLAY USES SIMULATION STEPS.", box.X + 14, box.Y + 43, UiCanvas.Muted, (box.Width - 28) / 9);
        }
    }
    internal void DrawBrowserBody(UiCanvas canvas)
    {
        if (!editor.IsReplaying)
        {
            browser.DrawBody(canvas);
        }
    }
}
