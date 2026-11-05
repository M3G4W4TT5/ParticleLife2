# Live experiment editor

Launch the native editor after the [contributor build](../CONTRIBUTING.md):

```sh
dotnet run --project src/ParticleLife.MonoGame -c Release --no-build
```

The default is 500 particles, seven species and serial grid execution. The window needs a working OpenGL display. The dark translucent inspector floats beside the world in a wide window and stacks beneath it in a narrow window; the world always uses one geometric scale. Drag the inspector's left edge to resize it in a wide window. The pane icon at its upper-right corner opens or closes it, with a short slide transition; the icon stays in the same position in both states. **E** toggles the same pane. A display radius or colour change never changes a trajectory.

## Editing while particles move

Use **Particles**, **Interactions**, **World**, **Playback**, **Appearance** and **Discoveries**, click section headings to expand or collapse them, and scroll inside the inspector. CPU execution and measurement controls expand under **Playback > Advanced CPU and measurements**. Play/pause, single-step and reset icons sit inside the grid's top-left corner, becoming more solid when hovered; **Undo**, **Redo** and **Files** stay in the inspector toolbar. Hover or focus a control to read its range, units and explanation. The **?** button at the bottom left (H or F1) opens Help, the centralized keyboard and pointer reference; the help panel is hidden by default. Click the world before using simulation keyboard shortcuts; focused widgets capture their keys.

The left statistics rail has its own collapse/expand icon and slide transition. The default narrower inspector leaves enough room for a square grid to align with the pane's top and bottom at the initial window size; other aspect ratios retain the world's geometric proportions. Notifications appear at the bottom right of the available canvas, move left with an expanded inspector, and remain for five seconds before fading out. Their close button dismisses them immediately.

Drag the label/slider area of a compact numeric row to apply live values at completed simulation boundaries. Click its number for exact entry, then Enter, Tab or a click outside the field to apply. Ctrl+A selects the text; Ctrl+C/X/V copy, cut and paste. Escape cancels a draft. Numeric sliders have convenient ranges, but valid typed values may lie outside them; the visible value remains authoritative. The reset arrow beside a numeric control resets that control, independently of **Reset experiment**. Incomplete or invalid input is rejected with an explanation and retains field focus until corrected or cancelled.

Recipes, generation placement/velocity and execution backend use dropdowns. Opening a menu only previews its choices; click a choice or use arrows and Enter to select it. Escape or a click outside cancels the menu without applying an option or activating the control beneath it. Selecting a built-in recipe still requires an explicit **New from recipe** or **Apply matrix live** action.

- **Interactions:** rows are receivers and columns are sources. Select a coloured cell, edit its coefficient in `[-1,1]`, and optionally link the reverse direction for subsequent edits. Row/column page buttons and exact selectors reach larger matrices. Random, Chains and Snakes templates use the generation seed. **Randomize matrix with new seed** (M) draws and records another seed. Both actions change only the matrix, retaining particles and the clock. Zero matrix removes outer attraction/repulsion; the species-independent repulsive core remains.
- **Built-in recipes:** select a recipe to see its suggested species count and behaviour. **New from recipe** uses the current seed with the recipe's default rules, population mix, uniform placement and zero velocities, establishing a new baseline at time/step zero. It keeps execution backend/workers, appearance and playback settings. **Apply matrix live** changes only the matrix, preserving current particles, populations, parameters and clock; it is available only for a compatible species count. Biased ribbons is labelled experimental. See the [recipe guide](recipes.md) for coefficients and supported counts.
- **Forces and motion:** lengths use reduced `L`; force scale and wall strength use reduced force `M L/T²`; half-life uses reduced time `T`. Damping off means a null half-life. **World boundary** explicitly selects soft walls, open, reflecting or periodic rules. Soft-wall strength is dormant in other modes. Global `R/beta` or directed core/cutoff tables set interaction ranges; zero pair core removes core repulsion. See [worlds and directed ranges](world-boundaries-and-ranges.md) for supported lengths, live mapping and selected/bulk radius controls.
- **World and population:** width, height and wall width form one **Apply world** draft. Soft/open worlds retain absolute motion; periodic/reflecting worlds explicitly map positions and normal velocities, recording actual pre-map undo. Population targets apply together. Optional density preservation resizes the world and maps survivors according to its boundary; unsupported periodic cutoffs reject the whole operation. Saving generation counts alone leaves particles untouched.
- **Generation recipe:** choose a seed, uniform/grid/disk placement, zero/common/random velocity direction, speed and angle. These settings affect explicit additions, resampling or new experiments. Changing a seed alone does not move particles. **Resample** changes actual current positions/velocities at the current clock. **New experiment** deliberately generates a fresh population and establishes a new reset baseline.
- **Simulation and execution:** numerical `dt` changes future integration increments. Playback speed changes requested simulation time per wall second. They are separate. Direct requires one worker; grid accepts one through eight. The **View** tab distinguishes requested and actual execution and fallback; a separate vertical rail on the left shows status without covering the grid. Catch-up cap, diagnostic enable/cadence and connectivity radius are independent controls.
- **Appearance:** particle radius is a display quantity, independent of force cutoff or connectivity radius. Species RGB colours, UI scale and overlay/help/diagnostic visibility are presentation choices.

The force graph uses the implemented radial law for the selected directed pair. Cyan is the selected direction and dashed yellow is its reverse when enabled. Positive outer values attract the receiver toward the source; negative values repel. The shaded core ends at `beta * R`, and interaction is zero at/after `R`. Exactly coincident particles contribute zero because direction is undefined; the nonzero-distance core has a repulsive limit. The graph can show either dimensionless `g` or reduced radial force `F0*g`. It is not a potential-energy plot or a validated molecular model.

## Population and undo semantics

The coloured live population bars show exact counts and percentages. Click a row to select its species target, a colour chip to open that species' colour controls, or **- / +** to adjust its live count by one percent of the current total (at least one particle). **Equalize live populations** distributes the existing total evenly. These actions retain every possible surviving identity and its current motion; generation supplies shortfalls.

## Camera, generators and direct tools

Scroll over the canvas to zoom around the pointer; drag with the middle or right button to pan. **V** or **World > Camera > Fit world** restores the fitted nominal rectangle. Exact zoom/centre controls and optional smoothing are in the same section. Centre coordinates are fractions of world width and height, including off-world views. Smooth motion affects only presentation; direct tools invert the view actually displayed. Particle radius can stay in screen pixels or use a reduced-world-unit display radius that follows zoom. Neither size mode changes interaction or wall ranges.

The directed matrix supports rectangle dragging, **Ctrl+click** toggling and **Shift+click/arrows** range extension. Click a receiver-row or source-column header to select it; Ctrl adds another header selection. **Shift+header** opens that species' display colour picker. Bulk set, scale, seeded randomize and zero operate on selected directed cells, optionally including reverse entries. Scaling clamps results to `[-1,1]`. **Copy full matrix** exports exact TSV values; **Paste full matrix live** validates a matching square TSV/CSV matrix before applying it. Ctrl+A/C/V provides the same operations while the matrix has focus. Rows remain receivers and columns remain sources.

Under **Particles > Future recipe and live generators**, position and velocity recipes affect future additions/new experiments. **Positions only live** changes only current positions; **Velocities only live** changes only current velocities. They can target all particles or one species. Position choices are uniform, grid, disk, ring, line, spiral, stripes, clusters and separated species groups. Velocity choices are zero, a common direction, random directions, radial inward/outward and tangential motion. Live species reassignment independently supports random/equal/spatial bands, a selected species, rotate and flip. A fraction selects exactly the rounded fraction of shuffled identities; actual results are recorded. Future generation counts stay separate from these live reassignments. Matrix generation and display palette presets have their own actions.

Choose a tool under **Particles > Canvas brushes**, then left-drag the canvas. Move grabs identities at stroke start; add creates fresh identities; paint changes species; erase removes particles. Attract, repel and directional force apply velocity impulses of `intensity * fixed dt` per actual applied sample. They also work while paused, without advancing simulation time. Circle/square footprint, world radius, intensity, direction, addition count, assigned species and affected-species filter are explicit controls. Middle/right panning remains available. Escape stops a gesture; Ctrl+Z undoes its applied changes.

Each stroke is one undo action. Undo restores only affected identities to their first actual edit's motion/membership; other surviving particles retain current motion. Every actual applied sample still belongs to replay, including samples subsequently undone. Unapplied pointer previews coalesce while simulation is busy; a move retains displacement from the last actually applied point. The current session representation stores a complete actual structural state per applied sample. Long strokes over large populations therefore consume substantial memory and produce large session files; history is not silently truncated.

## Population identity and reset rules

When species are removed, assignments map by `oldSpecies % newSpeciesCount`. The first requested particles of each resulting species survive in storage order with their exact identities, positions and velocities. Shortfalls are appended in species order using the current generation recipe and new increasing identities. Removed identities are not reused within a timeline. Matrix resizing retains the top-left entries; new entries are zero. Changing the total distributes targets evenly, with any remainder assigned to the lowest species indices.

One slider gesture is one undo action. Undoing a parameter change restores its prior value at the **current** boundary; intervening dynamics are not rewound. Every intermediate applied value still belongs to the scientific session, including values later undone. A new edit after undo discards redo even if a drag eventually returns to its starting value.

Population undo preserves current motion for surviving identities, restores removed identities with their saved removal-boundary motion, and removes additions. Explicit resample undo restores saved coordinates and velocities at the current clock. Redo uses stored actual values, never a regenerated seed. These structural operations can allocate memory; ordinary settled simulation steps retain reusable buffers.

**Reset experiment** restores the defined baseline state and configuration at time/step zero, clears undo/redo, and starts a fresh replay timeline. Live edits and saving do not change that baseline. Loading an experiment or explicitly creating a new one establishes a new baseline. Reset retains presentation, playback and placement/velocity settings, but restores the population recipe to the baseline's actual species counts.

## Save and load in the app

Open **Files** (or press **S** with world focus). The folder defaults to `artifacts/viewer`; `--save-dir` changes it. Browse folders, use **Parent**, or enter a folder path. Enter a descriptive save name without path separators. Existing main or companion files are never overwritten; use another name after a collision.

| Save action | Output | Meaning |
| --- | --- | --- |
| Save preset | `name.preset.json` | Exact current rules/matrix, numerical/execution settings, population recipe and presentation/playback choices. It does not capture an evolved pattern. |
| Save from here | `name.experiment.json` | Exact current positions, velocities, identities, species and configuration as a new experiment at time/step zero. Origin metadata retains source time/step and baseline provenance. |
| Save initial | `name.initial.json` | Exact reset baseline, including its original matrix and initial conditions. |
| Save session | `name.session.json` | Exact baseline, every applied trajectory-affecting event, actual structural payloads and the terminal completed-step boundary. |

Saving never changes the completed state or clock. Experiments and sessions also receive a `.viewer.json` companion for presentation, playback and future generation settings. Keep companions with their matching files to restore those choices; the scientific file alone still runs headlessly. A mismatched or malformed companion is rejected by the viewer. Presets include their own viewer settings.

A startup experiment path and **F5** reload restore the same companion settings as **Load experiment**. Explicit startup `--backend`, `--workers` and `--sample-every` options override the corresponding saved choices on startup and F5 reload. Without an explicit cadence option, the saved diagnostic cadence is retained. Startup runs unless `--paused` is supplied; in-app loading and F5 reload finish paused. An invalid startup file or companion leaves a generated fallback paused.

Select a JSON file in the list, then choose its intended action:

- **Load experiment** loads an initial/from-here/legacy experiment paused at time/step zero.
- **Replay session** replays through the saved terminal boundary in bounded batches and then pauses. Progress is visible and Escape cancels. It retains the old valid experiment until replay completes successfully. Continue editing afterwards to append to the loaded timeline; Reset starts again from that session's original baseline.
- **Preset live** applies compatible species dimensions while keeping particles, clock and playback status. It also loads the generation recipe, but does not generate particles.
- **Preset new** explicitly generates a new population from the recipe and establishes a new paused baseline.

The [included preset files](../examples/presets) provide the eight additional built-in recipes as editable examples. Browse that folder and select **Preset new** to try one. Preset files describe how to generate a population; they do not contain actual particle positions. File-based generation and the built-in generator can produce different initial arrangements from the same seed. Save **initial** or **from here** when the exact starting state matters.

Invalid files, reports used as inputs, incompatible presets and numerical replay failures show an error and retain the current valid experiment. A load performs no unintended extra numerical step. Sessions include multiple edits at the same boundary, edits before the first step, and edits after the last step.

## Replay without graphics

Run a current-state or initial experiment normally:

```sh
dotnet run --project src/ParticleLife.Cli -c Release --no-build -- run artifacts/viewer/example.experiment.json --steps 720 --sample-every 144 --output artifacts/example-run.json
```

Replay the included short edited fixture, or substitute a saved viewer session:

```sh
dotnet run --project src/ParticleLife.Cli -c Release --no-build -- validate-session examples/sessions/live-edits.session.json
dotnet run --project src/ParticleLife.Cli -c Release --no-build -- replay examples/sessions/live-edits.session.json --output artifacts/edited-run.json
dotnet run --project src/ParticleLife.Cli -c Release --no-build -- replay artifacts/viewer/example.session.json --continue-steps 720 --sample-every 144 --output artifacts/continued-run.json
```

`validate-session` validates the file contract without integrating. Replay additionally checks structural payload clocks against actual accumulated time and verifies that numerical steps complete. It uses the same boundary engine as the viewer. Output reports retain source bytes' SHA-256, the complete source session, execution segments, fallback counts, measurements, runtime metadata and final state. Reports are not accepted as experiments or sessions.

Recorded backend/worker choices are honoured unless `--backend direct|grid` or `--workers N` explicitly overrides them. Overrides remain visible in the report. Structural events contain full actual states; even with an override, those recorded snapshots apply at their boundaries. Such a replay is therefore not an uninterrupted alternative-backend trajectory through structural edits. `--continue-steps` advances only after all terminal edits, using the terminal configuration. Replay is indexed by simulation steps, not rendered frame timing; cross-runtime bitwise identity is not promised.

The scientific model remains phenomenological and generally dissipative/nonreciprocal. Dense interactions and some diagnostics remain quadratic. Catch-up limits bound the number of steps per frame, not the cost of a single step, generation operation or file parse. Performance measurement and platform verification are described in [the editor verification report](performance-live-editor.md). M5 release work remains deferred.
