# Exploring Particle Life 2

Start with the default 500 particles or choose a recipe in **Discoveries > Starting recipe**. **New from recipe** creates its recommended population and a fresh baseline. **Apply matrix live** keeps the current particles and clock, and needs compatible species counts.

Press **H / F1**, or choose **Guide** beside Undo, Redo and Files, for the in-app quickstart. The guide stays available after the splash; dismissing the splash does not activate a control or advance a paused experiment.

## Make one change at a time

1. Press **Space** to pause. **N** or **Right** completes one step and stays paused.
2. Open **Interactions**, select a receiver row and source column, and change the directed coefficient. Positive outer values attract; negative values repel. The reverse direction can differ. Inspect the force curve for the selected pair.
3. Resume, observe the change, then use **Ctrl+Z** to undo the edit at the current clock. Undo does not rewind numerical time.
4. Wheel over the canvas to zoom around the pointer; middle/right drag pans. **V** fits the nominal world. **E** hides or restores the inspector.
5. In **Particles > Brushes**, choose a tool, radius and species filter, then drag on the canvas. **Escape** ends a gesture; **Ctrl+Z** undoes the applied edits as a group.

Numeric fields accept exact values. Click a value, type and press **Enter**; **Escape** cancels the draft. **Tab / Shift+Tab** moves between controls and scrolls them into view. An invalid draft must be corrected or cancelled before a following action can proceed. Focused controls capture simulation shortcuts.

## Reset, Resample or New

| Action | Result |
| --- | --- |
| **Reset / R** | Restores the exact baseline state and rules at time/step zero. |
| **Resample / I** | Explicitly replaces current positions and velocities from the future generation recipe, retaining the current clock. It can be undone. |
| **Apply matrix live / 1–3** | Changes compatible interaction coefficients while retaining state and time. |
| **New from recipe** | Starts that complete recipe with a new population and zero-clock baseline. |
| **Independent live generators** | Change only their selected position, velocity or species component; the applied values are recorded. |

Changing the future seed, placement or velocity recipe alone does not move current particles. Live world/boundary changes have their own explicit mapping behavior; see [worlds and ranges](world-boundaries-and-ranges.md).

## Keep a discovery

Open **Files / S / O** or **Discoveries > Browse saved discoveries**. New output names protect existing files.

| Save | Use it for |
| --- | --- |
| **Save preset** | Reusable rules, generation recipe, palette and presentation choices. |
| **Save pattern** | Actual completed particle values and settings, rebased to a new zero-time origin with source clock and execution metadata. |
| **Save initial** | The exact Reset baseline. |
| **Save session** | Applied actions through the terminal completed boundary, including terminal edits. GPU recordings also retain actual captured boundaries. |

Keep `.viewer.json` companions beside the scientific file; they restore display, generation and playback choices. Add descriptions, tags and favourites in the library. Share creates a local artifact; it does not publish online. PNG/GIF captures are pictures, not state inputs. [Discovery and capture details](discovery-library.md) explain the separate workflows and limits.

Restoring a recorded capture means loading its actual stored values. Re-simulating actions runs the recorded edits again and can follow a different trajectory, especially across CPU/GPU or precision changes. The CPU-only CLI requires `--restore-capture` or `--resimulate-on-cpu` for capture-backed sessions. `--continue-steps N` advances after the restored/replayed terminal boundary while retaining its clock.

## Readability and speed

Use **Appearance > Display quality** to choose Economy, Balanced or Luminous manually. Economy disables trails, glow, heatmaps and sampled speed statistics. Balanced adds subtle glow; Luminous adds short trails and stronger additive glow. Every value remains editable; Custom indicates a different combination. Palette, velocity colour/reference speed, camera and particle-size choices remain independent. Tracking and solver diagnostics have their own controls.

Quality presets never choose a solver, change precision or reduce population. CPU worker counts, hardware selection, numerical timestep and achieved simulation speed are separate choices. Begin with a small scene, change one setting and compare graphics FPS with simulation steps/s and achieved playback. Large dense clusters can be substantially more expensive than sparse worlds with the same population; no preset guarantees 60 FPS or a particle-count capacity.

With canvas focus, **Ctrl+plus/minus** adjusts UI scale and **Ctrl+0** restores 1x. Appearance also accepts exact UI scale. The app fits controls to small windows; its minimum requested size is **420 × 340**. On short panels the toolbar scrolls with the content. **Page Up / Page Down** scrolls Help, or an inspector with keyboard focus/pointer over it. Enlarge the window or hide the inspector for a larger canvas.

Validation, loading and execution errors appear in the notification and launch console; the most recent message remains under **Help > Latest feedback** after the toast fades. A failed operation retains the last valid completed state. Losing window focus pauses playback; resume explicitly when ready.

For source builds and checks use [CONTRIBUTING](../CONTRIBUTING.md). For a Windows package use the [private preview instructions](portable-preview.md). Native acceptance and practical capacity are recorded in the separate [visual checklist](visual-review-checklist.md) and performance reports; this guide does not establish those results.
