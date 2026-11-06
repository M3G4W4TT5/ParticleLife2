# Worlds and directed interaction ranges

The 2D model supports four explicit world choices. These are artistic particle rules; the world size is independent of the window and the visible particle radius.

| World | Position and force behavior |
| --- | --- |
| Soft walls | The existing linear inward wall layer continues outside the nominal rectangle. Particles are not clipped. |
| Open | No wall force; positions can leave the rectangle. |
| Reflecting | After each complete integration stage, positions reflect at the edges and the normal velocity changes sign. Multiple crossings in a step are supported. |
| Periodic | Positions wrap after integration. Pair forces use one minimum-image displacement across edges and corners. |

Periodic cutoffs must be at most half the shortest world extent. Exactly half-extent displacement ties keep their directed sign; interactions at the cutoff are excluded. Positions at the upper periodic edge wrap to zero. Reflecting positions lie in the closed rectangle; velocity at an exact edge points inward. Reflections preserve speed apart from the model's ordinary force and damping step.

Choosing a periodic or reflecting world live, or resizing such a world, maps actual positions and velocities as one explicit structural edit. The saved event contains the mapped values and retains identities and the simulation clock. Undo restores saved pre-mapping motion only for remapped identities; other surviving identities retain their current motion. Redo restores the saved mapped motion for those same identities. Both retain the current clock. Complete live presets use the same mapping and undo policy. Switching to soft walls or open space retains absolute coordinates. The core's initial-state loader preserves stored values; it does not silently rewrite a capture while parsing it.

## Global and directed ranges

Range edits support selected directed cells and linked reverse pairs. Switching to pair mode copies the current global values; switching back restores the global controls and is undoable. Selective randomization changes rules, radii or both. Rules-only edits retain all radii, radius-only edits retain all coefficients, and replay stores generated values rather than regenerating them from a seed.

Choose the boundary in **World > World geometry**. The explanation beside it states what a live change maps. In **Interactions > Directed core and cutoff radii**, choose global or pair mode. The selected receiver/source direction's actual lengths are shown separately from coupled core/cutoff drafts. Apply both drafts to the selected pair or the matrix's selected cells; the reverse-link toggle also applies their transposes. Unapplied drafts remain available when selection changes; **Use selected actual radii** discards them. Invalid core/cutoff combinations and unsupported periodic lengths reject the whole action.

The same section independently randomizes selected rules, radii or both using the recorded generation seed. Radius generation has explicit minimum/maximum cutoffs and a maximum core fraction; zero generates zero cores. Rules-only generation ignores those radius drafts. The force graph uses each direction's own physical core/cutoff knots on one distance axis. Cyan is receiver <- source; dashed yellow is its reverse. The two radius bars show the repulsive core and outer support independently. Global R/beta remain saved defaults while pair mode is active, including for new species pairs; invalid periodic defaults must be corrected before switching to global mode or adding species.

Global mode retains the original cutoff `R` and core fraction `beta`. Its effective core is `beta * R`, and its original arithmetic path remains the default for old files. Per-pair mode stores an independent core radius and cutoff for every receiver-row/source-column pair, alongside the directed coefficient. Reversing a pair can change all three values.

Each cutoff must be finite and positive with a representable positive square. Each core must satisfy `0 <= core < cutoff`; a positive core must remain representable relative to its cutoff. Zero core removes short-range core repulsion. A zero coefficient still retains its nonzero core repulsion. Exactly overlapping particles have no defined direction and contribute zero without deleting either particle.

Below a positive core `c`, the radial coefficient is `r/c - 1`. Between `c` and cutoff `R`, a triangle rises from zero to the directed coefficient at `(c+R)/2`, then falls to zero at `R`. The force scale multiplies that coefficient and the unit displacement towards the source. The code's XML documentation and the shared `ParticleLifeForce` functions define the implemented curve; it is not a potential-energy graph.

Converting global settings to pair tables explicitly stores `c = beta * R` for every pair. Returning to global mode explicitly removes the tables and uses the retained global controls. Coefficient-only editing, zeroing and matrix templates retain pair radii. Population/species resizing retains the top-left rules and ranges; new pair entries use the global core/cutoff. Loading or creating a complete recipe can establish a different complete configuration.

The CPU grid keeps cells sized from the largest stored cutoff, including pairs with zero coefficients. For nonperiodic pair tables with differing receiver-row maxima, conservative fractional bounds restrict each query to a rectangle within the original nine cells, preserving accepted contribution order. It never caps cell occupancy or drops neighbours. Global/equal-row selection is unchanged. Periodic cells divide each world axis evenly; a conservative extra ring protects floating-point face mapping, and wrapped visits are deduplicated even when an axis has only two cells. A direct reference independently enumerates all ordered pairs using the same directed geometry and curve.

## Direct tools and density changes

Newly generated and directly edited particle values map through periodic/reflecting boundaries before their actual structural payload is recorded. Loading a capture still preserves its stored values exactly; an explicit tool edit may therefore map an edited particle from an off-world capture. Reflecting mapping changes crossed normal velocities as well as coordinates. Undo retains actual pre-map motion rather than trying to invert a fold after later numerical steps.

Periodic brush footprints and attraction/repulsion use minimum-image displacement across seams and corners. Move also uses the shortest directed displacement between actually applied pointer samples; it retains no hidden winding count for coalesced multi-world moves. The footprint preview repeats across periodic seams. Each affected identity is edited once per sample, regardless of visible copies. Applied actual payloads, gesture grouping and failed-sample rejection retain the normal exploration contracts.

**Preserve density: resize world** scales both extents by the square root of the population ratio. Soft/open survivors retain absolute motion. Periodic/reflecting survivors map into the resized world; undo restores pre-map motion for remapped identities while other surviving identities keep current motion. Added particles use the new geometry. Cutoffs do not shrink silently: a periodic resize whose active cutoff exceeds half the new shorter extent rejects atomically.

## Heterogeneous range profiling

The bounded comparison command saves all four exact inputs before timing: 4,000 particles, six species, uniform/clustered placement, and either equal cutoffs or one long-range receiver row with five short rows. It compares production maximum-cutoff evaluation with a packed control, coarse row-specific cell rejection and a finer row-specific stencil. Every probe checks accepted/overlap counts and forces against the direct evaluator, and reports warmed full validation/index/interaction timings and managed allocations. These ordinary-range nonperiodic probes are not selectable simulation modes.

```sh
dotnet run --project benchmarks/ParticleLife.Benchmarks -c Release --no-build -- profile-p4-ranges artifacts/p4-range-profile 5
```

Run only in a coordinated quiet window, using a fresh destination. Native viewers, builds and tests must be stopped. The [measured evaluation](performance-p4-ranges.md) retains the coarse-row rectangle and rejects quarter-width production cells for sparse regressions. It records complete-step comparisons, direct checks, allocations, controls and binary/input hashes; fewer candidates alone are not a speedup claim.

## Saved meaning and verification

New experiments write schema 3 and model `particle-life/2`, including the explicit boundary and either pair tables or an explicit global-range selection. Schemas 1 and 2 still load as their original soft-wall/global-range model; schema 1 keeps direct search. Old files are not reinterpreted as periodic or per-pair experiments. New sessions and presets write schema 2; schema-1 sessions/presets retain their original configuration meaning. Missing, duplicate, unknown and unsupported fields fail validation before replacing a live experiment.

Recorded structural edits replay actual state values rather than regenerating a seed. Replaying a saved sequence on the same supported CPU configuration remains distinct from switching arithmetic or hardware. Long trajectories can diverge after an explicit model/range conversion or a different backend, even when short comparisons agree closely.

Focused checks cover seam/corner forces, small wrapped grids without duplicate candidates, asymmetric cutoffs, zero cores, nonzero cores with zero coefficients, multi-crossing integration, live remapping and undo, range retention during matrix/population edits, strict format round-trips and legacy meanings. Native controls and their consolidated visual acceptance are recorded separately in [the visual checklist](visual-review-checklist.md); this model/persistence guide alone does not establish those UI results.
