# Worlds and directed interaction ranges

The 2D model supports four explicit world choices. These are artistic particle rules; the world size is independent of the window and the visible particle radius.

| World | Position and force behavior |
| --- | --- |
| Soft walls | The existing linear inward wall layer continues outside the nominal rectangle. Particles are not clipped. |
| Open | No wall force; positions can leave the rectangle. |
| Reflecting | After each complete integration stage, positions reflect at the edges and the normal velocity changes sign. Multiple crossings in a step are supported. |
| Periodic | Positions wrap after integration. Pair forces use one minimum-image displacement across edges and corners. |

Periodic cutoffs must be at most half the shortest world extent. Exactly half-extent displacement ties keep their directed sign; interactions at the cutoff are excluded. Positions at the upper periodic edge wrap to zero. Reflecting positions lie in the closed rectangle; velocity at an exact edge points inward. Reflections preserve speed apart from the model's ordinary force and damping step.

Choosing a periodic or reflecting world live, or resizing such a world, maps actual positions and velocities as one explicit structural edit. The saved event contains the mapped values and retains identities and the simulation clock. Undo restores the saved pre-mapping state at the current clock; it does not rewind elapsed time. Switching to soft walls or open space retains absolute coordinates. The core's initial-state loader preserves stored values; it does not silently rewrite a capture while parsing it.

## Global and directed ranges

Global mode retains the original cutoff `R` and core fraction `beta`. Its effective core is `beta * R`, and its original arithmetic path remains the default for old files. Per-pair mode stores an independent core radius and cutoff for every receiver-row/source-column pair, alongside the directed coefficient. Reversing a pair can change all three values.

Each cutoff must be finite and positive with a representable positive square. Each core must satisfy `0 <= core < cutoff`; a positive core must remain representable relative to its cutoff. Zero core removes short-range core repulsion. A zero coefficient still retains its nonzero core repulsion. Exactly overlapping particles have no defined direction and contribute zero without deleting either particle.

Below a positive core `c`, the radial coefficient is `r/c - 1`. Between `c` and cutoff `R`, a triangle rises from zero to the directed coefficient at `(c+R)/2`, then falls to zero at `R`. The force scale multiplies that coefficient and the unit displacement towards the source. The code's XML documentation and the shared `ParticleLifeForce` functions define the implemented curve; it is not a potential-energy graph.

Converting global settings to pair tables explicitly stores `c = beta * R` for every pair. Returning to global mode explicitly removes the tables and uses the retained global controls. Coefficient-only editing, zeroing and matrix templates retain pair radii. Population/species resizing retains the top-left rules and ranges; new pair entries use the global core/cutoff. Loading or creating a complete recipe can establish a different complete configuration.

The CPU grid uses the largest stored cutoff conservatively, including pairs with zero coefficients. It never caps cell occupancy or drops neighbours. Periodic cells divide each world axis evenly. A conservative extra cell ring protects floating-point face mapping, and wrapped visits are deduplicated even when an axis has only two cells. A direct reference independently enumerates all ordered pairs using the same directed geometry and curve.

## Saved meaning and verification

New experiments write schema 3 and model `particle-life/2`, including the explicit boundary and either pair tables or an explicit global-range selection. Schemas 1 and 2 still load as their original soft-wall/global-range model; schema 1 keeps direct search. Old files are not reinterpreted as periodic or per-pair experiments. New sessions and presets write schema 2; schema-1 sessions/presets retain their original configuration meaning. Missing, duplicate, unknown and unsupported fields fail validation before replacing a live experiment.

Recorded structural edits replay actual state values rather than regenerating a seed. Replaying a saved sequence on the same supported CPU configuration remains distinct from switching arithmetic or hardware. Long trajectories can diverge after an explicit model/range conversion or a different backend, even when short comparisons agree closely.

Focused checks cover seam/corner forces, small wrapped grids without duplicate candidates, asymmetric cutoffs, zero cores, nonzero cores with zero coefficients, multi-crossing integration, live remapping and undo, range retention during matrix/population edits, strict format round-trips and legacy meanings. Native controls and their consolidated visual acceptance are recorded separately in [the visual checklist](visual-review-checklist.md); this model/persistence guide alone does not establish those UI results.
