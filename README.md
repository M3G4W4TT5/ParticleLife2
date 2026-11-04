# Particle Life 2 

_Exploring emergent behaviour from simple interactions._

Particle Life 2 is a continuation of [Particle Life](https://github.com/M3G4W4TT5/ParticleLife). The goal is to create a lightweight, modifiable particle simulation for exploring how local interactions produce collective behaviour: particles forming patterns, clustering, separating and moving together.

The initial focus is on two-dimensional simulations, with an engine-independent C# core and MonoGame for rendering. Models and numerical methods should be explicit and testable, with attention to performance, readable code and scientific explanations in C# XML documentation.

The headless simulation is working. It includes directed species interactions, soft walls, damping, random/chain/snake presets and saved experiments with their complete initial conditions. The serial solver uses double precision and advances independently of rendering. The interactive MonoGame viewer is next.

## Run an experiment

Install the .NET SDK pinned in [`global.json`](global.json), then run:

```sh
dotnet restore ParticleLife.slnx --locked-mode
dotnet build ParticleLife.slnx -c Release --no-restore
dotnet test --solution ParticleLife.slnx -c Release --no-build
dotnet run --project src/ParticleLife.Cli -c Release --no-build -- run examples/experiments/two-species.json --steps 720 --output artifacts/two-species-run.json
```

Create a larger experiment with a saved random matrix and initial state:

```sh
dotnet run --project src/ParticleLife.Cli -c Release --no-build -- create --preset random --particles 500 --species 7 --seed 42 --output artifacts/random.json
dotnet run --project src/ParticleLife.Cli -c Release --no-build -- run artifacts/random.json --steps 720 --sample-every 144 --output artifacts/random-run.json
```

Use `chains` or `snakes` for the other presets, and `--help` for commands. Output files must have new names. Experiments are editable JSON; run reports preserve the input, final state, measurements and runtime metadata. A seed alone does not guarantee identical results across runtimes.

This is a phenomenological model for exploring emergence. Soft walls confine rather than reflect particles, and directed interactions and damping generally do not conserve momentum or energy.

See the [architecture and migration plan](docs/architecture-and-migration.md), [contributor guidance](CONTRIBUTING.md) and [reference performance measurements](docs/performance-baseline.md) for the numerical conventions, checks and current limits.
