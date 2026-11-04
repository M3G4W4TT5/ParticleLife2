# Particle Life 2 

_Exploring emergent behaviour from simple interactions._

Particle Life 2 is a continuation of [Particle Life](https://github.com/M3G4W4TT5/ParticleLife). The goal is to create a lightweight, modifiable particle simulation for exploring how local interactions produce collective behaviour: particles forming patterns, clustering, separating and moving together.

The initial focus is on two-dimensional simulations, with an engine-independent C# core and MonoGame for rendering. Models and numerical methods should be explicit and testable, with attention to performance, readable code and scientific explanations in C# XML documentation.

The interactive viewer and headless runner share the same simulation. They include directed species interactions, soft walls, damping, random/chain/snake presets and saved experiments with their complete initial conditions. The serial solver uses double precision and advances independently of rendering.

## Explore the simulation

Install the .NET SDK pinned in [`global.json`](global.json), then run:

```sh
dotnet restore ParticleLife.slnx --locked-mode
dotnet build ParticleLife.slnx -c Release --no-restore
dotnet run --project src/ParticleLife.MonoGame -c Release --no-build
```

The viewer starts with 500 particles, seven species and seed 1. Load a saved experiment, optionally paused:

```sh
dotnet run --project src/ParticleLife.MonoGame -c Release --no-build -- examples/experiments/two-species.json --paused
```

Use `--help` for startup options, including `--preset random|chains|snakes`, `--particles`, `--species`, `--seed`, `--sample-every` and `--save-dir`. DesktopGL needs a working OpenGL display. Resize to at least 420 × 340 pixels for readable controls. Resizing preserves world geometry; off-world particles remain in the simulation.

Click **HELP** for clickable controls, or click the playback badge to pause and resume.

| Key | Action |
| --- | --- |
| Space | Pause or resume. Losing window focus also pauses. |
| N / Right | Advance exactly one step, then remain paused. |
| R | Reset the current experiment to its exact initial state, matrix, time and step count. |
| I | Resample uniform positions and zero velocities; keep identities, species, matrix and model. |
| M | Generate a new random matrix; keep the exact initial state and model. |
| 1 / 2 / 3 | Select random / chains / snakes; regenerate state and matrix with the current counts and model. |
| S | Save the current initial experiment to a new file under `artifacts/viewer`. |
| F5 | Reload the startup experiment path. |
| D | Toggle sampled diagnostics. |
| H / F1 | Show or hide controls. |
| F9 | Print the latest timing summary to the console. |
| Esc | Close controls; otherwise quit. |

Resampling, matrix changes and preset selection restart at time and step zero, preserve the timestep, and record new seeds and actual generated values. Reset restores those saved values without generating anything. Saving preserves the initial experiment for CLI replay; it does not save the evolved state as a checkpoint, and existing files are never overwritten. Invalid reloads retain the current valid experiment and pause playback.

Playback uses the experiment's fixed timestep, with at most eight steps per frame. Excess playback time is dropped and shown in the display rather than increasing the timestep. Diagnostics are sampled every 144 completed steps by default and show their measurement time. The direct solver is quadratic; large populations can be slow even for a single step.

## Run without graphics

Create and replay an experiment with a saved random matrix and initial state:

```sh
dotnet run --project src/ParticleLife.Cli -c Release --no-build -- create --preset random --particles 500 --species 7 --seed 42 --output artifacts/random.json
dotnet run --project src/ParticleLife.Cli -c Release --no-build -- run artifacts/random.json --steps 720 --sample-every 144 --output artifacts/random-run.json
```

Use `chains` or `snakes` for the other presets, and `--help` for commands. Output files must have new names. Experiments are editable JSON; run reports preserve the input, final state, measurements and runtime metadata. A seed alone does not guarantee identical results across runtimes.

This is a phenomenological model for exploring emergence. Soft walls confine rather than reflect particles, and directed interactions and damping generally do not conserve momentum or energy.

See the [architecture and migration plan](docs/architecture-and-migration.md), [contributor guidance](CONTRIBUTING.md) and [reference performance measurements](docs/performance-baseline.md) for the numerical conventions, checks and current limits.
