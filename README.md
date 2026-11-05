# Particle Life 2 

![Particle Life 2 simulation preview](opt_preview.gif)

_Exploring emergent behaviour from simple interactions._

Particle Life 2 is a continuation of [Particle Life](https://github.com/M3G4W4TT5/ParticleLife). The goal is to create a lightweight, modifiable particle simulation for exploring how local interactions produce collective behaviour: particles forming patterns, clustering, separating and moving together.

The initial focus is on two-dimensional simulations, with an engine-independent C# core and MonoGame for rendering. Models and numerical methods should be explicit and testable, with attention to performance, readable code and scientific explanations in C# XML documentation.

The interactive editor and headless runner share the same simulation. Change directed species interactions, forces and populations while particles move, inspect their force curves, and save complete experiments or replayable editing sessions. The solver uses double precision and advances independently of rendering. An exact uniform grid accelerates neighbour discovery, with the direct all-pairs solver available for comparison.

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

Use `--help` for startup options, including `--preset`, `--particles`, `--species`, `--seed`, `--backend direct|grid`, `--workers`, `--sample-every` and `--save-dir`. Grid search defaults to one worker; `--workers 4` explicitly selects parallel evaluation (1–8 workers). Saved experiments retain their backend and workers; older files remain serial and schema-1 files use direct search. DesktopGL needs a working OpenGL display. Resizing preserves world geometry; off-world particles remain in the simulation.

Try **Built-in recipes** for Chains, Snakes, Core and halo, Alternating bands, Ribbons, Cyclic pursuit, Chase cycles, Species islands and experimental Biased ribbons. The [recipe guide](docs/recipes.md) explains the numbers and includes command-line examples.

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

This is a phenomenological model for exploring emergence. Soft walls confine rather than reflect particles, and directed interactions and damping generally do not conserve momentum or energy.

See the [architecture and migration plan](docs/architecture-and-migration.md), [contributor guidance](CONTRIBUTING.md) and [CPU performance comparison](docs/performance-m4-1.md) for the numerical conventions, checks and current limits.
