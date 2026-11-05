# Particle Life 2 consolidated visual review

Status: **consolidated review pending**. This checklist belongs to the P1–P7 update. Individual implementation and hardware evidence below does not mark the end-to-end cases passed.

Collect and refine cases during implementation. Run the full review once the integrated 2D features are ready, following [the implementation assignment](implementation-prompt.md#consolidated-visual-review). Perform earlier visual checks only to resolve a concrete implementation blocker. After the consolidated review, repeat affected cases following fixes instead of restarting every case.

## Run record

- Integration branch and tested commit: pending
- Build/package and launch command: pending
- OS, CPU, GPU/device, driver/API and display scale: pending
- Saved fixtures, population, world dimensions, interaction ranges, timestep and playback settings: pending
- CPU worker/precision and GPU precision settings: pending
- Window sizes: normal, maximized, narrow/tall and supported minimum
- Evidence directory: `artifacts/implementation/visual-review/`
- Reviewer, date and outstanding manual checks: pending

Give each case an exact setup and repeatable actions before execution. Use Pending, Pass, Fail or Blocked, with a concise observed result and evidence reference. If a case requires both CPU and GPU, record separate outcomes. Record post-fix checks with their new commit; keep still-applicable earlier evidence identifiable. A simulated input event is not proof that a physical interaction worked if the tool did not deliver it.

## Cases to refine during implementation

| ID | Area and required actions | Expected outcome | Coverage | Status |
| --- | --- | --- | --- | --- |
| V01 | Launch app/package; dismiss splash; pause/resume, single-step and reset | Clean startup, no accidental action or catch-up burst; correct step/reset behaviour | CPU and GPU; paused/running | Pending |
| V02 | Resize, maximize and use narrow/minimum layouts; change UI scale | Readable controls, reachable menus/dialogs, consistent world geometry and pointer mapping | CPU and GPU; window sizes above | Pending |
| V03 | Type values, use Tab/Enter/Escape and clipboard; try invalid drafts | Exact editing works; errors are clear; focused inputs do not trigger simulation shortcuts | Shared UI; physical-key coverage recorded | Pending |
| V04 | Pan/zoom/fit/reset; toggle smoothing and display-size modes | Camera is usable and particles remain selectable; display changes do not alter motion | CPU and GPU | Pending |
| V05 | Edit single/multiple matrix cells, rows/columns, linked reverse pairs; copy/paste | Correct directed entries change; bulk actions, values, force graph and undo agree | CPU and GPU; asymmetric matrix | Pending |
| V06 | Adjust population bars/counts/percentages, equalize and change species | Counts and surviving state follow documented semantics; controls remain responsive | CPU and GPU; growth/shrink | Pending |
| V07 | Independently regenerate positions, velocities, species, rules and palette | Only the indicated component changes; seed/provenance and Reset/Resample/New distinctions are clear | CPU and GPU; representative generators | Pending |
| V08 | Move, add, erase, attract, repel and apply directional brushes | Brush position/radius/species/intensity agree with the view; edits work while playing and paused | CPU and GPU; zoomed/panned | Pending |
| V09 | Undo/redo scalar, matrix, population and brush gestures | Grouped undo behaves as documented; surviving identities/clock remain coherent | CPU and GPU | Pending |
| V10 | Switch soft/open/reflecting/periodic boundaries; inspect edges/corners | Boundary behaviour and seam interactions are consistent; no particle loss or duplicate visible interaction | CPU and GPU; small periodic grids | Pending |
| V11 | Edit global/per-pair core and cutoff radii; randomize rules/radii selectively | Curve/diagram/value agree; unsupported ranges are explained; unaffected pairs stay unchanged | CPU and GPU; heterogeneous ranges | Pending |
| V12 | Change CPU workers and CPU/GPU device choice during a session | UI shows actual execution; transfer preserves intended state/time; failed selection retains valid state | Both directions; running/paused | Pending |
| V13 | Run CPU-only with unavailable/disabled GPU; exercise supported device-failure path | CPU remains usable; failure/continuation messages accurately describe recoverable state | Available hardware/controlled failure route | Pending |
| V14 | Explore large uniform and evolved clustered fixtures; drag controls while running | Input remains usable; FPS, step rate and achieved playback are distinct; no silent rule/population reduction | CPU and GPU; measured feasible workloads | Pending |
| V15 | Enable heatmaps/diagnostics at different cadences | Overlays are legible; sample age is clear; optional work does not silently alter simulation settings | CPU and GPU | Pending |
| V16 | Adjust palettes, opacity, trails, velocity shading, blend and glow | Controls visibly match their effect; dense patterns remain readable; quality choices work | CPU and GPU; dense scene | Pending |
| V17 | Select a region/pattern to track; pan away, recenter and stop tracking | Follow/indicator behaviour is predictable and does not modify the simulation | CPU and GPU; moving pattern | Pending |
| V18 | Save preset/current/initial/session; search/filter/favourite/rename discoveries | Correct item types and thumbnails are findable; save protection and feedback work | CPU and GPU; library | Pending |
| V19 | Load legacy files; import/export selective preset components and species alignment | Old meaning is preserved; intended components apply; incompatible/malformed input keeps valid state | CPU and GPU where supported | Pending |
| V20 | Restore captures, resume, re-simulate actions and inspect recording limits | Restore and re-simulation are clearly distinguished; recording/checkpoint status is honest | CPU and GPU; cross-backend case | Pending |
| V21 | Produce screenshot and short GIF/video; inspect resulting files | Correct framing/content and usable output; capture does not change timestep or contaminate input | CPU and GPU; available capture formats | Pending |
| V22 | Close/reopen, load a saved discovery and exercise packaged launch | Resources shut down cleanly; save/resume and first-run guidance work | Supported release package | Pending |

## Findings and follow-up checks

### P7 guidance, accessibility and packaged preview (pending)

These cases remain **Pending** until the consolidated native review, repeated on CPU and the available GPU backend after integration.

- V01/V22: launch the extracted Windows preview using `Start-Viewer.cmd` from a writable path containing spaces. Inspect branding/icon/splash and early dismissal. Open Guide/H/F1; follow its first experiment without a console or source checkout. Confirm the default save folder is beside the launcher and a saved discovery reloads after closing.
- V02/V03: at 1360 × 900, 700 × 950 and 420 × 340, use only Tab/Shift+Tab, Enter, Escape and Page Up/Down to reach every inspector page, open/close Help, read the guide and activate Files. Check visible focus and scroll-into-view. With canvas focus, Ctrl+plus/minus changes scale and Ctrl+0 restores 1x; focused numeric/text fields retain their own keys. Larger UI scale must still allow a route back to the scale control.
- V03/V19: reject an invalid number and a malformed load, let the notification fade, then inspect Help > Latest feedback. Correct/cancel the draft and confirm valid state retained. Verify no simulation shortcut fires through keyboard focus, and losing window focus still pauses.
- V16: select Economy, Balanced and Luminous manually. Confirm the announced effects/overlays change; individually edit glow or opacity and observe Custom. Palette, velocity-colour choice, camera, timestep, rules, workers, hardware, precision and population must remain unchanged. Undo the preset at the current clock.
- V10/V11: load `examples/experiments/periodic-seam.json` and `directed-ranges.json` paused. Inspect seam interactions, both directed curves and reflecting edges; save/reload the intended state. These small examples do not establish capacity.
- V20/V22: from the extracted package, run CPU CLI help and the periodic example with a new output filename; verify an existing output is protected. On a capture-enabled package, explicitly restore a captured session and separately request CPU action re-simulation, checking source/effective provenance. Native GPU availability and practical capacity remain separate evidence.

### P6 observation and capture cases (pending)

Repeat these cases on CPU and GPU after integration, with a two-species asymmetric pattern and a practical large evolved fixture. They remain **Pending**.

- V15: enable population and spatial-bin maps, change cadence from 0.1 to 2 seconds and toggle sampled speed statistics. Check the source step/time, receipt age, outside count and cost. Disable observations and tracking together and confirm sampling/readback stops. On GPU verify bounded histogram metadata reads rather than full particle captures.
- V16: compare opacity 0/0.4/1, both blends and all four velocity shading modes. Enable trails, pause for ten seconds, then resume; the pause must not erase trails through elapsed wall time. Pan/zoom/recolour to clear stale trails, vary glow strength/spread and disable both effects. Numerical timestep and count must remain unchanged.
- V17: pick a moving region, follow it across a periodic seam, pan/zoom away, recenter, stop, and select an empty or too-small region. The indicator and lost-contact message must agree with the sampled bins; the camera never changes particle values.
- V21: save PNG and a four-second 720px/10fps GIF, resize during recording, and finish a second recording early. Inspect both files with an independent image viewer/decoder for orientation, colour, framing, animation duration and letterboxing. Compare displayed skipped-frame/readback-cost counters. Close during a recording and confirm either a complete published GIF or a clear failure, with no partial file presented as successful. Reject an over-budget quality request without starting it.

### P3 concrete pending cases

All cases below remain **Pending** until the consolidated review. Start with the saved two-species experiment paused, then repeat representative running cases with the larger saved grid workload, on CPU and GPU after integration.

- V04: wheel-zoom around a labelled particle, middle/right-pan, V-fit, exact centre/zoom, smoothing on/off, and screen/world display radius. Repeat in normal, tall and minimum windows; the pointer footprint must match displayed geometry and saved/reloaded camera targets.
- V05: drag a rectangle, Ctrl-toggle a cell, Shift-extend using arrows, select two receiver rows and a source column, then set/scale/randomize/zero with reverse linking. Copy/paste exact TSV and reject NaN, out-of-range, missing-cell and mismatched-dimension input without partial edits. Shift-click each header colour and inspect the directed force graph and undo.
- V06: compare population bar counts/percentages with totals, click +/- and equalize, then undo after several steps. Verify surviving IDs retain current motion; open a colour picker from a bar chip.
- V07: apply each position layout, position-only and species-filtered velocity-only generation; partial rotate/flip and spatial species reassignment; independent matrix and palette recipes. Save/replay actual results and verify unchanged components.
- V08–V09: while paused and playing, use move/add/paint/erase/attract/repel/directional tools with circle/square shapes, filters and changed intensity. Pan/zoom first. Hold a stroke through a busy step, stop with Escape/focus loss, undo once, redo and replay. Confirm unrelated identities retain motion, rejected samples do not enlarge undo, and queued pointer previews do not lose total move displacement. Release Move after a small sub-threshold pointer change and after a quick release-frame displacement; the particles must reach the final pointer position, while releasing other tools must not add an extra impulse/population sample. Record session-memory/file cost for a bounded long stroke; do not infer compact storage.

### Concrete P2 render and ownership cases (pending)

- Use the saved P1 50k fixed-density input with CPU grid, 24 workers and diagnostics disabled. While running, pan/resize and drag a force value, then pause and single-step. Record input response, graphics FPS, simulation steps/s and achieved speed separately. Confirm count never changes and an eventual pause completes after the in-flight step.
- While paused, use F9 before and after a palette/radius/camera edit. A camera or radius change must not upload another full particle buffer; a palette change may upload only palette data. Resume and confirm each new displayed completed sequence uploads once.
- With particles on all four canvas edges, resize and pan so circles overlap the edge with their centres outside. Circles must clip cleanly without disappearing early. Inspect both direct backbuffer and the scene target beneath translucent panels; orientation must match the input coordinates.
- Increase display radius beyond the native point-size limit to exercise the bulk-quad fallback. Confirm readable UI and correct clipping after the fallback, then restore a small radius. Record the actual fallback reason and render path from F9.
- Change population from 10k to 50k with Preserve density enabled. Confirm world area grows fivefold, cutoff remains unchanged and undo restores the prior membership/world as one operation. Save and reload the applied values. A zero-population density-preservation request must explain why a scale cannot be computed.

No consolidated review findings yet. Record case ID, observed issue, responsible fix, fix commit and focused recheck result here. Keep blocked/manual cases explicit; do not mark the review complete until required cases are resolved or the user has agreed to a stated exception.

### Necessary early P1 interoperability check

The isolated `prototypes/GpuInterop` smoke was run on 5 November 2026 to resolve whether a compute buffer can be drawn by the existing MonoGame context. It used 4,096 points and 120 fixed translation steps on the NVIDIA RTX 5080 Laptop GPU / OpenGL 4.6. All original and restored particle-centre pixel checks passed, the exact capture hashes matched, and normal frames performed no particle readback. Sol inspected the saved framebuffer and observed the coloured grid plus magenta/cyan MonoGame markers. Build, CPU-only startup (no native graphics modules) and formatting checks passed. This is functional interoperability evidence, not the full P5 simulation or a performance limit.

Evidence lives under the P1 worktree's ignored `artifacts/implementation/p1-gpu-smoke-1/` and `p1-gpu-checks/`; details and commands are in [the GPU report](gpu-integration.md). Direct live CUA observation was attempted during the bounded post-pass window but the tool returned `Computer Use app approval timed out`. This part remains **Pending** for the final native review; saved framebuffer inspection does not establish keyboard or live-window interaction. No V01–V22 case is marked passed by this early check.
