# Live experiment editor

Launch the native editor after the [contributor build](../CONTRIBUTING.md):

```sh
dotnet run --project src/ParticleLife.MonoGame -c Release --no-build
```

The default is 500 particles, seven species and serial grid execution. The window needs a working OpenGL display. Controls sit beside the world in a wide window and stack in a narrow window; the world always uses one geometric scale. **E** hides the editor. A display radius or colour change never changes a trajectory.

## Editing while particles move

Click section headings to expand or collapse them and scroll inside the panel. **Pause**, **Step**, **Reset**, **Undo**, **Redo** and **Files** stay near the top. Hover or focus a control to read its range, units and explanation. Tab moves between controls. Click the world before using simulation keyboard shortcuts; focused widgets capture their keys.

Drag a slider to apply live values at completed simulation boundaries. Click its number for exact entry, then Enter, Tab or a click outside the field to apply. Ctrl+A selects the text; Ctrl+C/X/V copy, cut and paste. Escape cancels a draft. Numeric sliders have convenient ranges, but valid typed values may lie outside them; the visible value remains authoritative. The **R** beside a numeric control resets that control, independently of **Reset experiment**. Incomplete or invalid input is rejected with an explanation and retains field focus until corrected or cancelled.

- **Interactions:** rows are receivers and columns are sources. Select a coloured cell, edit its coefficient in `[-1,1]`, and optionally link the reverse direction for subsequent edits. Row/column page buttons and exact selectors reach larger matrices. Random, Chains and Snakes templates use the generation seed. **Randomize matrix with new seed** (M) draws and records another seed. Both actions change only the matrix, retaining particles and the clock. Zero matrix removes outer attraction/repulsion; the species-independent repulsive core remains.
- **Forces and motion:** cutoff and wall width use reduced length `L`; force scale and wall strength use reduced force `M L/T²`; half-life uses reduced time `T`; the core fraction is dimensionless. Damping off means a null half-life. Walls off means zero wall strength, with no reflecting or periodic boundary added. Finite off-world particles stay in the simulation.
- **World and population:** width, height and wall width are a coupled draft with one **Apply world** action. It changes the nominal rectangle while preserving absolute particle coordinates and velocities. Total/species counts and individual species targets form another draft; **Apply to live population** applies them together. Saving counts as a generation recipe alone leaves current particles untouched.
- **Generation recipe:** choose a seed, uniform/grid/disk placement, zero/common/random velocity direction, speed and angle. These settings affect explicit additions, resampling or new experiments. Changing a seed alone does not move particles. **Resample** changes actual current positions/velocities at the current clock. **New experiment** deliberately generates a fresh population and establishes a new reset baseline.
- **Simulation and execution:** numerical `dt` changes future integration increments. Playback speed changes requested simulation time per wall second. They are separate. Direct requires one worker; grid accepts one through eight. The overlay distinguishes requested and actual execution and fallback. Catch-up cap, diagnostic enable/cadence and connectivity radius are independent controls.
- **Appearance:** particle radius is a display quantity, independent of force cutoff or connectivity radius. Species RGB colours, UI scale and overlay/help/diagnostic visibility are presentation choices.

The force graph uses the implemented radial law for the selected directed pair. Cyan is the selected direction and dashed yellow is its reverse when enabled. Positive outer values attract the receiver toward the source; negative values repel. The shaded core ends at `beta * R`, and interaction is zero at/after `R`. Exactly coincident particles contribute zero because direction is undefined; the nonzero-distance core has a repulsive limit. The graph can show either dimensionless `g` or reduced radial force `F0*g`. It is not a potential-energy plot or a validated molecular model.

## Population and undo semantics

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
