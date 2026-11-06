# Particle Life 2 

![Particle Life 2 simulation preview](opt_preview.gif)

_Exploring emergent behaviour from simple interactions._

Particle Life 2 is a continuation of [Particle Life](https://github.com/M3G4W4TT5/ParticleLife). The goal is to make it easy to create and explore emergent particle behaviour: patterns forming, clustering, separating and moving together. It is a creative sandbox, with saving and replay to help revisit interesting discoveries.

The app is two-dimensional, with an engine-independent C# core and MonoGame for rendering. The focus is on large populations, responsive exploration and understandable rules. The CPU remains available for desktop and headless use; compatible OpenGL compute hardware also supports device-resident GPU simulation. 3D is a later phase.

Change directed interactions, core/cutoff ranges and populations while particles move, inspect their force curves, pan and zoom, or use brushes to perturb a pattern. Soft-wall, open, reflecting and periodic worlds are explicit choices. Save interesting states in the discovery library, reuse selected preset components, and capture PNG images or short GIFs. Display effects and manual quality presets do not change particle rules.

## Explore the simulation

Install the .NET SDK pinned in [`global.json`](global.json), then run:

```sh
dotnet restore ParticleLife.slnx --locked-mode
dotnet build ParticleLife.slnx -c Release --no-restore
dotnet run --project src/ParticleLife.MonoGame -c Release --no-build
```

The viewer starts with 500 particles, seven species, seed 1 and grid search. Load a saved experiment, optionally paused:

```sh
dotnet run --project src/ParticleLife.MonoGame -c Release --no-build -- examples/experiments/two-species.json --paused
```

Press **H / F1** or choose **Guide** for the quickstart. **Space** pauses, **N** single-steps, **V** fits the world and **E** hides the inspector. The [exploration guide](docs/quickstart.md) explains Reset, Resample, New, brushes, files and keyboard controls.

Use `--help` for startup options. CPU grid search defaults to one worker; `--workers 4` explicitly selects parallel evaluation (1–64 workers). Saved experiments retain their choices; old schema-1 files keep direct search. The CPU uses double precision and a fixed timestep independently of rendering, with an exact uniform grid and direct reference available. GPU arithmetic and ordering can produce different trajectories. DesktopGL needs a working OpenGL display even when CPU simulation is selected.

In **Discoveries > Starting recipe**, try Chains, Snakes, Core and halo, Alternating bands, Ribbons, Cyclic pursuit, Chase cycles, Species islands and experimental Biased ribbons. The [recipe guide](docs/recipes.md) explains the numbers and includes command-line examples.

For an unsigned Windows x64 private preview with its own runtime, use the [packaging instructions](docs/portable-preview.md). The package includes the viewer, CPU CLI, guides, small examples and font license. It does not require an installed SDK to run.

## Run without graphics

Create and replay an experiment with a saved random matrix and initial state:

```sh
dotnet run --project src/ParticleLife.Cli -c Release --no-build -- create --preset random --particles 500 --species 7 --seed 42 --output artifacts/random.json
dotnet run --project src/ParticleLife.Cli -c Release --no-build -- run artifacts/random.json --steps 720 --sample-every 144 --output artifacts/random-run.json
```

Use any [built-in recipe](docs/recipes.md) with `--preset`, and `--help` for commands. New experiments use grid search; add `--backend direct` to `create` or `run` to select the reference. Output files must have new names. Experiments are editable JSON; run reports preserve the input, effective backend, fallback counts, final state, measurements and runtime metadata. A seed alone does not guarantee identical results across runtimes.

Replay an edited session through its saved terminal boundary, including timestep and population changes:

```sh
dotnet run --project src/ParticleLife.Cli -c Release --no-build -- replay examples/sessions/live-edits.session.json --output artifacts/session-run.json
```

Use `--continue-steps N` to continue afterwards. Replay follows simulation-step boundaries; rendered frame timing is not part of the scientific timeline. Reports preserve explicit execution overrides separately from recorded choices.

Capture-backed sessions require an explicit choice: `replay capture.json --restore-capture` restores actual recorded values and clock on CPU; `--resimulate-on-cpu` runs the embedded actions again. Widening stored float values is exact; reproducing a GPU trajectory on CPU is a different promise. Saved captures, action replay and cross-backend similarity are distinct features.

This is a phenomenological model for exploring emergence. Directed interactions and damping generally do not conserve momentum or energy. Sparse worlds and dense clusters have different costs; particle count alone is not a performance guarantee.

See the [small examples](examples/README.md), [discovery guide](docs/discovery-library.md), [worlds and ranges](docs/world-boundaries-and-ranges.md), [legacy mode dispositions](docs/legacy-modes.md) and [contributor guidance](CONTRIBUTING.md). The [architecture plan](docs/architecture-and-migration.md) and [performance reports](docs/performance-p2.md) describe the implementation and measured limits.
