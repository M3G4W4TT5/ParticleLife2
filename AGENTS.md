# Particle Life 2

Particle Life 2 is a creative sandbox for exploring emergent particle behaviour. Prioritize interesting patterns, responsive exploration and large populations, with reproducibility as a useful product feature.

Use [CONTRIBUTING.md](CONTRIBUTING.md) for C# conventions, verification and Git workflow. Consult the [architecture and migration plan](docs/architecture-and-migration.md) when changing model equations, numerical methods or architectural boundaries. Keep evolving plans and commands in those documents.

## Design and simulation behaviour

- Keep shared model/data contracts and CPU simulation independent of graphics, input devices, game engines and rendering clocks. GPU execution and rendering adapters may share device buffers behind an explicit ownership boundary. Retain selectable CPU and GPU execution as the application evolves.
- Separate state, models, numerical methods, experiments and presentation. Introduce abstractions for meaningful responsibilities or concrete alternatives.
- Evaluate each numerical stage from a consistent input state. Make buffer ownership, mutation and observation lifetimes explicit.
- Explain implemented rules, parameter meanings and supported ranges. This is an exploratory particle-life application, not a scientifically validated physical model; publication-grade validation is not a feature prerequisite.
- Test useful behaviour and state contracts. Compare optimized paths with a simple reference on representative cases. Single precision, approximate arithmetic and artistic rules are welcome when their effects and performance are understood; do not silently change saved model meaning or discard neighbours.
- Preserve actual initial conditions, parameters, seeds, applied edits and relevant backend/precision/version metadata. State capture, repeatable replay on a supported configuration and cross-backend similarity are different promises; do not imply identical trajectories across hardware.

## Performance and documentation

- Prefer contiguous state and reusable buffers. Target zero managed allocations in warmed simulation steps at stable capacity; keep I/O and reporting outside numerical hot loops.
- Profile before adding specialized complexity. Report optimization results with comparable workloads, environment details, allocations and a proportionate check of changed behaviour. Measure simulation, rendering and responsiveness separately.
- Use C# XML comments as the authoritative API and algorithm documentation, including important internal numerics. Explain equations, units, assumptions and ownership alongside the code.
- Keep the README concise, user-facing and in the author's voice. Keep agent work logs, internal reasoning and private research links out of it.

## Working practice

- Make frequent, cohesive, verified commits; use short-lived topic branches for implementation. Keep generated outputs and local historical review material out of source commits and build dependencies.
- You may delegate bounded tasks to sub-agents when useful. Coordinate file ownership, review their changes and verify the integrated result.
- Follow the phased roadmap in the architecture plan. 3D is a planned later phase; keep immediate implementation focused on 2D without building a dimension-generic framework in advance.
