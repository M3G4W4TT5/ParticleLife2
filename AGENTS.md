# Particle Life 2

Particle Life 2 explores emergence through scientifically explicit particle models.

Use [CONTRIBUTING.md](CONTRIBUTING.md) for C# conventions, verification and Git workflow. Consult the [architecture and migration plan](docs/architecture-and-migration.md) when changing model equations, numerical methods or architectural boundaries. Keep evolving plans and commands in those documents.

## Design and scientific correctness

- Keep the simulation core independent of graphics, input devices, game engines and rendering clocks. Hosts consume completed simulation state.
- Separate state, models, numerical methods, experiments and presentation. Introduce abstractions for meaningful responsibilities or concrete alternatives.
- Evaluate each numerical stage from a consistent input state. Make buffer ownership, mutation and observation lifetimes explicit.
- State units, assumptions and valid parameter ranges. Distinguish coefficients, forces, accelerations and potential energies; scientific labels require supporting equations and evidence.
- Test mathematical and state contracts with justified tolerances. Apply conservation checks only where the model supports them. Compare optimized evaluation with a simple reference implementation.
- Preserve actual initial conditions, parameters, seeds and relevant runtime metadata for reproducible experiments. A seed alone is insufficient.

## Performance and documentation

- Prefer contiguous state and reusable buffers. Target zero managed allocations in warmed simulation steps at stable capacity; keep I/O and reporting outside numerical hot loops.
- Profile before adding specialized complexity. Report optimization results with comparable workloads, environment details, allocations and numerical error.
- Use C# XML comments as the authoritative API and algorithm documentation, including important internal numerics. Explain equations, units, assumptions and ownership alongside the code.
- Keep the README concise, user-facing and in the author's voice. Keep agent work logs, internal reasoning and private research links out of it.

## Working practice

- Make frequent, cohesive, verified commits; use short-lived topic branches for implementation. Keep generated outputs and local historical review material out of source commits and build dependencies.
- You may delegate bounded tasks to sub-agents when useful. Coordinate file ownership, review their changes and verify the integrated result.
