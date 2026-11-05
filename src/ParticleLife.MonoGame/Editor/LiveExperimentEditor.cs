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
    private readonly EditorSession editor;
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
    private int selectedTab;
    private ButtonState currentMouseButton;
    private bool observedReplay;
    private bool replayCancelPending;
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
        : this(new EditorSession(editor), saveDirectory, report, printTimings)
    {
    }

    internal LiveExperimentEditor(EditorSession editor, string saveDirectory, Action<string> report, Action printTimings)
    {
        this.editor = editor;
        this.report = report;
        this.printTimings = printTimings;
        panel = new UiPanel(Error);
        matrix = new InteractionMatrixWidget(editor);
        Graph = new ForceGraph(editor, matrix);
        browser = new ExperimentFileBrowser(editor, saveDirectory, report, () => SuppressPlayback = true, ClearDrafts);
        panel.Toolbar.Add(new UiRow(
            new UiButton(() => "Undo", () => editor.Undo(), "Undo at the current clock. Membership undo preserves surviving motion and restores removed particles from saved state; resampling undo restores captured positions/velocities.", () => editor.CanUndo),
            new UiButton(() => "Redo", () => editor.Redo(), "Reapply an undone editor operation at the current boundary. A new edit clears redo.", () => editor.CanRedo),
            Button(() => "Files", OpenFiles, "Save named presets, exact states, initial experiments and sessions; browse local files and replay in-app.")));

        recipes = Section("Starting recipe", true);
        interactions = Section("Interactions");
        forces = Section("Forces and motion", true);
        population = Section("World and population", true);
        generation = Section("Generation");
        simulation = Section("Simulation and execution", true);
        appearance = Section("Appearance", true);
        SelectRecipe(selectedRecipe);
        BuildRecipes();
        BuildInteractions();
        BuildForces();
        BuildPopulation();
        BuildGeneration();
        BuildSimulation();
        BuildAppearance();
        panel.Toolbar.Add(new UiRow(
            new UiButton(() => "Model", () => SelectTab(0), "Recipes, forces and the directed interaction matrix.", selected: () => selectedTab == 0),
            new UiButton(() => "World", () => SelectTab(1), "World size, species populations and generation.", selected: () => selectedTab == 1),
            new UiButton(() => "View", () => SelectTab(2), "Execution, measurements and presentation.", selected: () => selectedTab == 2)));
        SelectTab(0);
        Synchronize();
    }

    /// <summary>Changes inspector pages after the panel has validated the outgoing focused field.</summary>
    private void SelectTab(int index)
    {
        selectedTab = index;
        panel.Content.Clear();
        panel.Content.AddRange(index switch
        {
            0 => [recipes, forces, interactions],
            1 => [population, generation],
            _ => [simulation, appearance]
        });
        panel.ScrollToTop();
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
        PresetInfo[] choices = PresetCatalog.All.ToArray();
        recipes.Children.Add(new UiDropdown("Recipe", choices.Select(recipe => recipe.Name + (recipe.IsExperimental ? " (experimental)" : "")).ToArray(),
            () => Array.FindIndex(choices, recipe => recipe.Kind == selectedRecipe),
            index => SelectRecipe(choices[index].Kind), "Selection previews a recipe. Use New from recipe or Apply matrix live to change the experiment."));
        recipes.Children.Add(new UiLabel(() => PresetCatalog.Get(selectedRecipe).Description, 48));
        recipes.Children.Add(new UiLabel(() =>
        {
            PresetInfo recipe = PresetCatalog.Get(selectedRecipe);
            return $"{recipe.DefaultParticleCount} particles / {recipe.DefaultSpeciesCount} species{recipePopulationSummary} / seed {editor.Generation.Seed}";
        }, 22, UiCanvas.Accent));
        recipes.Children.Add(new UiRow(Button(() => "New from recipe", StartSelectedRecipe,
            "Start the complete suggested recipe: default world, forces, timestep and populations; uniform positions, zero velocities, current seed. Establish a new reset baseline. Retain appearance, playback and execution choices."),
            new UiButton(() => "Apply matrix live", ApplySelectedRecipeMatrix,
            "Apply only the selected interaction matrix. Retain particles, populations, forces, generation settings and clock. Requires a compatible current species count; use New from recipe for the complete starting configuration.",
            () => PresetCatalog.SupportsSpeciesCount(selectedRecipe, editor.Configuration.Matrix.SpeciesCount))));
        recipes.Children.Add(new UiLabel(() => PresetCatalog.SupportsSpeciesCount(selectedRecipe, editor.Configuration.Matrix.SpeciesCount)
            ? "New resets the experiment. Matrix live keeps particles and time."
            : $"Matrix live needs compatible species; New uses {PresetCatalog.Get(selectedRecipe).DefaultSpeciesCount}.", 34));
    }

    /// <summary>Selects a catalogue entry without mutating any simulation or generation setting.</summary>
    internal void SelectRecipe(PresetKind kind)
    {
        PresetInfo recipe = PresetCatalog.Get(kind);
        selectedRecipe = kind;
        if (kind == PresetKind.CoreHalo)
        {
            int[] counts = PresetCatalog.CreatePopulations(kind, recipe.DefaultParticleCount, recipe.DefaultSpeciesCount);
            recipePopulationSummary = $" ({counts[0]} core / {counts[1]} halo)";
        }
        else
        {
            recipePopulationSummary = string.Empty;
        }
    }

    /// <summary>Starts the selected complete recipe through the same controller used by headless checks.</summary>
    internal void StartSelectedRecipe()
    {
        PresetKind kind = selectedRecipe;
        PresetInfo recipe = PresetCatalog.Get(kind);
        ReportCommand("New recipe", owner => owner.NewRecipe(kind),
            $"Started {recipe.Name}{(recipe.IsExperimental ? " (experimental)" : "")}: {recipe.DefaultParticleCount} particles, {recipe.DefaultSpeciesCount} species. New baseline at time/step zero.", () =>
            {
                ClearDrafts();
                matrix.Synchronize();
            });
        SuppressPlayback = true;
    }

    /// <summary>Applies the selected matrix without adopting its population or other recommended settings.</summary>
    internal void ApplySelectedRecipeMatrix() => Template(selectedRecipe);

    private void BuildInteractions()
    {
        interactions.Children.Add(matrix);
        interactions.Children.Add(new UiLabel(() => $"Selected {matrix.Receiver} <- {matrix.Source}   A={editor.Configuration.Matrix[matrix.Receiver, matrix.Source]:G8}", 25, UiCanvas.Accent));
        interactions.Children.Add(Number("Coefficient A", () => editor.Configuration.Matrix[matrix.Receiver, matrix.Source],
            value => editor.SetMatrixCoefficient(matrix.Receiver, matrix.Source, value, symmetry), 0, -1, 1,
            "A[receiver,source] is dimensionless, in [-1,1]. Positive outer values attract; negative repel. Core repulsion is independent of A."));
        interactions.Children.Add(new UiToggle("Link reverse direction", () => symmetry, value => symmetry = value,
            "Optional editing symmetry: the next coefficient edit also sets A[source,receiver]. The solver remains directed."));
        interactions.Children.Add(new UiRow(
            Button(() => "Random", () => Template(PresetKind.Random), "Apply a seeded random matrix live. Preserve particles, time and completed steps."),
            Button(() => "Chains", () => Template(PresetKind.Chains), "Apply the Chains matrix live without regenerating particles or resetting the clock."),
            Button(() => "Snakes", () => Template(PresetKind.Snakes), "Apply the Snakes matrix live without regenerating particles or resetting the clock.")));
        interactions.Children.Add(Button(() => "Randomize with new seed", RandomizeMatrix,
            "Choose a fresh recorded seed and change the live matrix only. Random template above intentionally uses the generation recipe's existing seed."));
        interactions.Children.Add(Button(() => "Reset matrix to zero", editor.ResetMatrix,
            "Set all directed outer coefficients to zero. The species-independent repulsive core remains."));
        interactions.Children.Add(Number("Select receiver", () => matrix.Receiver,
            value => matrix.Select(Integer(value), matrix.Source), 0, 0, 31,
            "Exact receiver row index. This selects an editor cell and does not change any rule.", integer: true, gesture: false,
            maximumValue: () => editor.Configuration.Matrix.SpeciesCount - 1));
        interactions.Children.Add(Number("Select source", () => matrix.Source,
            value => matrix.Select(matrix.Receiver, Integer(value)), 0, 0, 31,
            "Exact source column index. Rows receive forces from columns; the reverse direction may be different.", integer: true, gesture: false,
            maximumValue: () => editor.Configuration.Matrix.SpeciesCount - 1));
        interactions.Children.Add(new UiToggle("Graph as coefficient", () => Graph.ShowCoefficient, value => Graph.ShowCoefficient = value,
            "Off plots F0*g in reduced force units. On plots dimensionless g. Neither curve is a potential energy."));
        interactions.Children.Add(new UiToggle("Show reverse direction", () => Graph.ShowReverse, value => Graph.ShowReverse = value,
            "Cyan is the selected receiver <- source direction; dashed yellow is the reverse directed coefficient."));
        interactions.Children.Add(new ForceGraphWidget(Graph));
    }

    private void BuildForces()
    {
        forces.Children.Add(Number("Cutoff R [L]", () => Parameters.Cutoff, value => ParametersChange(Parameters with { Cutoff = value }),
            0.1, 0.005, 1, "Finite positive reduced length; R squared must be finite and positive. Slider is logarithmic; typed valid values are unrestricted.", log: true));
        forces.Children.Add(Number("Force F0 [F]", () => Parameters.ForceScale, value => ParametersChange(Parameters with { ForceScale = value }),
            1, 0, 10, "Finite nonnegative pair-force scale in reduced force units. Zero disables pair forces. Unit particle mass is assumed."));
        forces.Children.Add(Number("Core beta [1]", () => Parameters.RepulsionFraction, value => ParametersChange(Parameters with { RepulsionFraction = value }),
            0.3, 0.01, 0.99, "Dimensionless core fraction, strictly 0 < beta < 1. The core ends at distance beta*R."));
        forces.Children.Add(new UiToggle("Damping enabled", () => Parameters.DampingHalfLife is not null, value =>
        {
            if (Parameters.DampingHalfLife is double current)
            {
                rememberedDamping = current;
            }
            ParametersChange(Parameters with { DampingHalfLife = value ? rememberedDamping : null });
        }, "Off means a null half-life: no damping. It does not set a tiny/zero half-life. Re-enabling restores the previous enabled value."));
        forces.Children.Add(Number("Half-life [T]", () => Parameters.DampingHalfLife ?? rememberedDamping, value =>
        {
            ParametersChange(Parameters with { DampingHalfLife = value });
            rememberedDamping = value;
        }, 0.025, 0.001, 10, "Finite positive reduced time. Entering a half-life enables damping. Velocity factor per step is 2^(-dt/halfLife).", log: true));
        forces.Children.Add(new UiToggle("Soft walls", () => Parameters.WallStrength > 0, value =>
        {
            if (Parameters.WallStrength > 0)
            {
                rememberedWall = Parameters.WallStrength;
            }
            ParametersChange(Parameters with { WallStrength = value ? rememberedWall : 0 });
        }, "Off sets wall strength to zero. No reflecting or periodic boundary is introduced; finite off-world particles remain valid."));
        forces.Children.Add(Number("Wall width [L]", () => Parameters.WallWidth, value => ParametersChange(Parameters with { WallWidth = value }),
            0.05, 0.001, 0.5, "Positive layer width, at most half the smaller world dimension. Coupled world-size/width changes can be applied together under World.", log: true));
        forces.Children.Add(Number("Wall strength [F]", () => Parameters.WallStrength, value => ParametersChange(Parameters with { WallStrength = value }),
            1.44, 0, 10, "Finite nonnegative reduced force. The linear soft wall extends outside the nominal rectangle. Zero disables this force."));
    }

    private void BuildPopulation()
    {
        population.Children.Add(new UiLabel(() => "World draft: apply together. Positions and velocities stay.", 32));
        population.Children.Add(Number("World width [L]", () => width, value => { width = value; worldDraft = true; }, 1, 0.1, 10,
            "Draft finite positive width. Apply world validates width, height and wall layer together; particles are not rescaled.", log: true, gesture: false));
        population.Children.Add(Number("World height [L]", () => height, value => { height = value; worldDraft = true; }, 1, 0.1, 10,
            "Draft finite positive height. Existing off-world particles remain simulated after shrinking.", log: true, gesture: false));
        population.Children.Add(Number("Wall width [L]", () => wallWidth, value => { wallWidth = value; worldDraft = true; }, 0.05, 0.001, 0.5,
            "Coupled draft layer width; must be at most min(width,height)/2 when Apply world is pressed.", log: true, gesture: false));
        population.Children.Add(Button(() => worldDraft ? "Apply world *" : "Apply world", () =>
        {
            double requestedWidth = width, requestedHeight = height, requestedWallWidth = wallWidth;
            (Parameters with { WorldWidth = requestedWidth, WorldHeight = requestedHeight, WallWidth = requestedWallWidth }).Validate();
            ReportCommand("Apply world", owner =>
            {
                SimulationConfiguration current = owner.Configuration;
                owner.ApplyConfiguration(new SimulationConfiguration(current.Parameters with
                {
                    WorldWidth = requestedWidth, WorldHeight = requestedHeight, WallWidth = requestedWallWidth
                }, current.Matrix, current.TimeStep, current.Backend, current.WorkerCount), "Edit world");
            }, "World rectangle changed; absolute positions, velocities and clock retained.", () =>
            {
                if (width == requestedWidth && height == requestedHeight && wallWidth == requestedWallWidth) { worldDraft = false; }
            });
            SuppressPlayback = true;
        }, "Atomically apply these three world/wall drafts. Invalid combinations leave every live parameter unchanged."));
        population.Children.Add(new UiLabel(() => "Population draft: survivors keep their identities and motion.", 32));
        population.Children.Add(Number("Total particles", () => populations.Sum(), value => Distribute(Integer(value), populationSpecies),
            500, 0, 5000, "Changing this draft distributes the target total evenly. No particle changes until Apply live. Exact larger valid totals can be typed.", integer: true, gesture: false));
        population.Children.Add(Number("Species count", () => populationSpecies, value => Distribute(populations.Sum(), Integer(value)),
            7, 1, 32, "Changing this draft redistributes targets evenly. Apply live retains unaffected matrix entries; new entries are zero. Removed species remap modulo the new count.", integer: true, gesture: false));
        population.Children.Add(new UiLabel(() => populationSummary, 50, UiCanvas.Accent));
        population.Children.Add(Number("Species to edit", () => populationIndex, value =>
        {
            int index = Integer(value);
            if (index < 0 || index >= populationSpecies)
            {
                throw new ArgumentOutOfRangeException(nameof(value), $"Use 0..{populationSpecies - 1}.");
            }
            populationIndex = index;
        }, 0, 0, 31, "Select a draft species, then edit its individual target below. Selection does not change the population.", integer: true, gesture: false,
            maximumValue: () => populationSpecies - 1));
        population.Children.Add(Number("Species target", () => populations.Length == 0 ? 0 : populations[populationIndex], value =>
        {
            int count = Integer(value);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            ValidateDraftPopulation(populations, populationIndex, count);
            populations[populationIndex] = count;
            populationDraft = true;
            UpdatePopulationSummary();
        }, 0, 0, 2000, "Exact count for the selected draft species; the total updates accordingly. Zero particles and empty species are supported.", integer: true, gesture: false));
        population.Children.Add(Button(() => populationDraft ? "Apply to live population *" : "Apply to live population", () =>
        {
            int speciesCount = populationSpecies;
            int[] requested = (int[])populations.Clone();
            ReportCommand("Apply population", owner => owner.ApplyPopulation(speciesCount, requested),
                "Population applied: surviving identities/state retained; actual additions recorded in the session.", () =>
                {
                    if (populationSpecies == speciesCount && populations.AsSpan().SequenceEqual(requested)) { populationDraft = false; }
                });
            SuppressPlayback = true;
        }, "Apply all targets atomically; additions use the recipe. Undo keeps current motion for survivors and restores removed particles from saved state, at the current clock."));
        population.Children.Add(Button(() => "Use counts for generation", () =>
        {
            int[] requested = (int[])populations.Clone();
            ReportCommand("Generation counts", owner => owner.SetPopulationRecipe(requested),
                "Generation count recipe saved. Current particles are unchanged.");
        }, "Change only future generation counts. Recipe species count must match the active matrix; apply species changes explicitly first."));
        population.Children.Add(Button(() => "Discard unapplied drafts", () =>
        {
            populationDraft = worldDraft = false;
            observedRevision = -1;
            Synchronize();
        }, "Reload these unapplied fields from the current experiment without changing any live state."));
    }

    private void BuildGeneration()
    {
        generation.Children.Add(new UiLabel(() => "Generation settings apply only when particles are created or resampled.", 32));
        generation.Children.Add(new UiLabel(() => $"Recipe: {RecipeTotal()} particles / {editor.Populations.Length} species. Edit future counts under World.", 44, UiCanvas.Accent));
        generation.Children.Add(Number("Generation seed", () => editor.Generation.Seed,
            value => editor.SetGeneration(editor.Generation with { Seed = Integer(value) }), 1, 0, 10000,
            "Integer seed for future generation and random matrices. Changing it alone never regenerates the running state. Replay saves actual generated values.", integer: true));
        generation.Children.Add(new UiDropdown("Placement", ["Uniform", "Regular grid", "Central disk"],
            () => (int)editor.Generation.Placement,
            index => editor.SetGeneration(editor.Generation with { Placement = (PlacementKind)index }),
            "Placement is used only for explicit new, resampled or added particles."));
        generation.Children.Add(new UiDropdown("Velocity", ["Zero", "Common direction", "Random direction"],
            () => (int)editor.Generation.Velocity,
            index => editor.SetGeneration(editor.Generation with { Velocity = (VelocityKind)index }),
            "Speed and direction are generation settings, not live velocity overrides."));
        generation.Children.Add(Number("Initial speed [L/T]", () => editor.Generation.Speed,
            value => editor.SetGeneration(editor.Generation with { Speed = value }), 0, 0, 3,
            "Finite nonnegative reduced speed for newly generated velocities. Zero mode still creates zero velocity."));
        generation.Children.Add(Number("Direction [rad]", () => editor.Generation.DirectionRadians,
            value => editor.SetGeneration(editor.Generation with { DirectionRadians = value }), 0, -Math.PI, Math.PI,
            "Finite angle in radians for common-direction initial velocities. Slider spans -pi..pi; exact angles may be typed."));
        generation.Children.Add(Button(() => "Resample live particles", () =>
        {
            Resample();
            SuppressPlayback = true;
        }, "Explicitly generate new positions and velocities for existing particles. Preserve identities, species, matrix, time and steps; record the actual new values."));
        generation.Children.Add(new UiLabel(() => "New experiment: regenerates all particles; sets a new reset baseline at time/step zero.", 44));
        generation.Children.Add(new UiRow(
            Button(() => "New random", () => NewExperiment(PresetKind.Random), "Generate a new population and random matrix from the recipe, clearing incompatible undo/history."),
            Button(() => "New chains", () => NewExperiment(PresetKind.Chains), "Generate a new population and Chains matrix, establishing a new baseline."),
            Button(() => "New snakes", () => NewExperiment(PresetKind.Snakes), "Generate a new population and Snakes matrix, establishing a new baseline.")));
    }

    private void BuildSimulation()
    {
        simulation.Children.Add(Number("Timestep dt [T]", () => editor.Configuration.TimeStep,
            value => ConfigurationChange(timeStep: value), 1.0 / 144, 0.0001, 0.1,
            "Finite positive fixed integration timestep. Applies to future increments; elapsed simulation time is not recomputed. Clears fractional playback backlog.", log: true));
        simulation.Children.Add(Number("Playback speed [x]", () => editor.Playback.PlaybackSpeed,
            value => { editor.SetPlayback(editor.Playback.Settings with { Speed = value }); SuppressPlayback = true; }, 1, 0.05, 8,
            "Wall-clock speed only; does not alter dt or equations. Changing speed clears pending playback time.", log: true));
        simulation.Children.Add(new UiDropdown("Backend", ["Direct reference", "Uniform grid"],
            () => editor.Configuration.Backend == ForceBackend.Direct ? 0 : 1,
            index => ConfigurationChange(backend: index == 0 ? ForceBackend.Direct : ForceBackend.UniformGrid, workers: 1),
            "A backend switch selects one worker. Choose grid workers separately. Rules and completed state are retained."));
        simulation.Children.Add(Number("Workers", () => editor.Configuration.WorkerCount,
            value => ConfigurationChange(workers: Integer(value)), 1, 1, 8,
            "Explicit worker count including caller, 1..8. Direct requires exactly one; invalid combinations are rejected atomically.", integer: true));
        simulation.Children.Add(Number("Max steps / frame", () => editor.Playback.MaxStepsPerFrame,
            value => editor.SetPlayback(editor.Playback.Settings with { MaxStepsPerFrame = Integer(value) }), 8, 1, 32,
            "Positive catch-up cap. Remaining whole-step playback time is dropped and displayed. It does not bound the cost of one numerical step.", integer: true));
        simulation.Children.Add(new UiToggle("Sample diagnostics", () => editor.Playback.DiagnosticEverySteps > 0,
            SetDiagnosticsEnabled, "Independent measurements after completed steps. Turning them off changes no numerical force or trajectory."));
        simulation.Children.Add(Number("Diagnostic cadence", () => editor.Playback.DiagnosticEverySteps,
            value => SetDiagnosticCadence(Integer(value)), 144, 0, 1440,
            "Nonnegative completed-step cadence. Zero disables samples. Editing clears stale measurements; wall-clock frame rate is irrelevant.", integer: true));
        simulation.Children.Add(new UiToggle("Connectivity follows cutoff", () => editor.Playback.DiagnosticRadius is null,
            value => editor.SetPlayback(editor.Playback.Settings with { DiagnosticRadius = value ? null : Parameters.Cutoff }),
            "When enabled, connectivity measurements use the current interaction cutoff. A separate radius changes diagnostics only."));
        simulation.Children.Add(Number("Connectivity R [L]", () => editor.Playback.DiagnosticRadius ?? Parameters.Cutoff,
            value => editor.SetPlayback(editor.Playback.Settings with { DiagnosticRadius = value }), 0.1, 0.001, 2,
            "Finite positive diagnostic connection length. Entering a value selects an independent radius and clears previous samples.", log: true));
        simulation.Children.Add(new UiLabel(() => $"Requested {editor.Configuration.Backend}/{editor.Configuration.WorkerCount}; applied {editor.Publication.Configuration.Backend}/{editor.Publication.Configuration.WorkerCount}; actual {editor.Playback.LastExecution?.ActualBackend.ToString() ?? "UNSTEPPED"}/{editor.Playback.LastExecution?.ActualWorkerCount.ToString() ?? "-"}. Fallback {editor.Playback.LastExecution?.FallbackReason.ToString() ?? "NONE"}.", 78));
        simulation.Children.Add(new UiLabel(() => editor.HasPendingCommands
            ? $"Queued: {editor.PendingCommands} commands. Apply after the current completed boundary."
            : editor.IsBusy ? "CPU step in progress; controls remain available." : "Completed boundary ready.", 42, UiCanvas.Accent));
        simulation.Children.Add(Button(() => "Print viewer timings", printTimings,
            "Print the latest approximately one-second FPS, controller update, editor input/update and render submission timings to the launch console. Present is excluded."));
    }

    private void BuildAppearance()
    {
        appearance.Children.Add(new UiLabel(() => "Display settings do not change trajectories or physical lengths.", 32));
        appearance.Children.Add(Number("Particle radius [px]", () => editor.Appearance.ParticleRadius,
            value => editor.SetAppearance(editor.Appearance with { ParticleRadius = value }), 2.5, 0.5, 12,
            "Display radius in pixels. Independent of cutoff, wall width and diagnostic connectivity."));
        appearance.Children.Add(Number("UI scale [x]", () => editor.Appearance.UiScale,
            value => editor.SetAppearance(editor.Appearance with { UiScale = value }), 1, 0.75, 1.5,
            "Scale editor controls and text. World geometry continues to use one isotropic scale."));
        appearance.Children.Add(Number("Colour species", () => colourSpecies, value =>
        {
            int index = Integer(value);
            if (index < 0 || index >= editor.Configuration.Matrix.SpeciesCount)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Choose an existing species.");
            }
            colourSpecies = index;
        }, 0, 0, 31, "Select the species colour to edit. Matrix row/column labels use the same colour.", integer: true, gesture: false,
            maximumValue: () => editor.Configuration.Matrix.SpeciesCount - 1));
        appearance.Children.Add(new UiTextField("Hex colour", () => $"#{editor.Appearance.ColorForSpecies(colourSpecies):X6}", text =>
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
            string label = component == 0 ? "Red [0..255]" : component == 1 ? "Green [0..255]" : "Blue [0..255]";
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
        appearance.Children.Add(Button(() => "Reset selected species colour", () =>
        {
            editor.SetAppearance(editor.Appearance.WithSpeciesColor(colourSpecies, new AppearanceSettings().ColorForSpecies(colourSpecies)));
        }, "Restore this species' deterministic default display colour."));
        appearance.Children.Add(new UiToggle("Simulation stats", () => editor.Appearance.ShowOverlay,
            value => editor.SetAppearance(editor.Appearance with { ShowOverlay = value }), "Show the simulation state and timing in the left statistics rail."));
        appearance.Children.Add(new UiToggle("Diagnostic stats", () => editor.Appearance.ShowDiagnostics,
            value => editor.SetAppearance(editor.Appearance with { ShowDiagnostics = value }), "Show the latest sample with its own time and step. This does not enable or disable measurement."));
        appearance.Children.Add(new UiToggle("Help", () => editor.Appearance.ShowHelp,
            value => editor.SetAppearance(editor.Appearance with { ShowHelp = value }), "Open the centralized keyboard and interaction help panel."));
        appearance.Children.Add(new UiToggle("Editor visible", () => editor.Appearance.ShowEditor,
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
        populationSummary = entries + (populations.Length > 12 ? " ... use species selector" : string.Empty);
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
        editor.Pump();
        if (editor.IsBackground)
        {
            bool replaying = editor.IsReplaying;
            if (observedReplay && !replaying)
            {
                string status = editor.Publication.Error is not null
                    ? "Replay failed: " + editor.Publication.Error + ". Previous completed experiment retained."
                    : replayCancelPending ? "Replay cancelled. Previous completed experiment retained, paused."
                    : "Replay completed at the recorded terminal boundary, paused.";
                browser.SetStatus(status);
                report(status);
                if (!replayCancelPending && editor.Publication.Error is null) { ClearDrafts(); }
                replayCancelPending = false;
            }
            observedReplay = replaying;
        }
        currentMouseButton = mouse.LeftButton;
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
                replayCancelPending = editor.IsBackground;
                string status = editor.IsBackground ? "Replay cancellation queued; waiting for the current step to finish."
                    : "Replay cancelled. Previous completed experiment retained, paused.";
                browser.SetStatus(status);
                report(status);
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
        browser.Open(pointerHeld: currentMouseButton == ButtonState.Pressed);
    }

    internal void ToggleVisibility()
    {
        panel.ClearFocus();
        editor.SetAppearance(editor.Appearance with { ShowEditor = !editor.Appearance.ShowEditor });
    }

    internal void Reset()
    {
        ReportCommand("Reset experiment", owner => owner.Reset(),
            "Reset experiment: exact baseline state and rules restored at time/step zero; fresh replay timeline.", ClearDrafts);
        SuppressPlayback = true;
    }

    internal void ReloadExperiment(string path, ForceBackend? backendOverride, int? workerOverride, int? diagnosticEveryStepsOverride = null)
    {
        editor.Run("Reload experiment", owner => owner.LoadExperiment(path, backendOverride, workerOverride, diagnosticEveryStepsOverride), () =>
        {
            ClearDrafts();
            report(backendOverride is not null || workerOverride is not null
                ? "Reloaded startup experiment with explicit execution overrides, paused."
                : "Reloaded startup experiment with its saved execution settings, paused.");
        });
        SuppressPlayback = true;
        if (editor.IsBackground) { report("Reload queued; the current experiment is retained until loading succeeds."); }
    }

    internal void Template(PresetKind kind)
    {
        int seed = editor.Generation.Seed;
        PresetInfo recipe = PresetCatalog.Get(kind);
        ReportCommand("Apply matrix", owner => owner.ApplyMatrixTemplate(kind, seed),
            $"{recipe.Name}{(recipe.IsExperimental ? " (experimental)" : "")} matrix applied live. Particles, velocities, time and completed steps retained.");
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
        editor.SetPlayback(editor.Playback.Settings with
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
        ReportCommand("Randomize matrix", owner => owner.ApplyMatrixTemplate(PresetKind.Random, seed),
            $"Randomized matrix live with recorded seed {seed}. Particles and clock retained; the generation recipe is unchanged.");
    }

    private void NewExperiment(PresetKind kind)
    {
        ReportCommand("New experiment", owner => owner.NewExperiment(kind, $"{kind} experiment"),
            $"New {kind} experiment generated. New baseline at time/step zero.", ClearDrafts);
        SuppressPlayback = true;
    }

    internal void Resample() => ReportCommand("Resample", owner => owner.ResampleParticles(),
        "Resampled positions/velocities using the recipe; identities, rules and clock retained.");

    private void ReportCommand(string label, Action<ExperimentEditor> operation, string completed, Action? after = null)
    {
        if (editor.IsBackground) { report($"{label} queued; waiting for a completed simulation boundary."); }
        editor.Run(label, operation, () => { after?.Invoke(); report(completed); });
    }

    private void Error(string message) => report("Edit rejected: " + message);
    internal void DrawChrome(UiCanvas canvas) => panel.DrawChrome(canvas, "Inspector");
    internal void DrawBody(UiCanvas canvas) => panel.DrawBody(canvas);
    internal void DrawOverlay(UiCanvas canvas) => panel.DrawOverlay(canvas);
    internal void DrawBrowserChrome(UiCanvas canvas)
    {
        browser.DrawChrome(canvas);
        if (editor.IsReplaying)
        {
            Rectangle box = new(browser.Bounds.X + 20, browser.Bounds.Y + 140, browser.Bounds.Width - 40, 80);
            canvas.Fill(box, UiCanvas.Raised);
            canvas.Text($"REPLAYING {editor.ReplayCompletedStep} / {editor.ReplayTerminalStep}", box.X + 14, box.Y + 17, UiCanvas.Accent);
            canvas.Text("Esc cancels. replay uses simulation steps.", box.X + 14, box.Y + 43, UiCanvas.Muted, (box.Width - 28) / 9);
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
