# Live experiment editor measurements

These measurements separate unchanged numerical stepping, graphics-free editing and native presentation. They use saved actual inputs, not newly generated seed equivalents. The feature does not change the Particle Life force curve, integration rule, grid enumeration or accumulation order.

## Environment and comparison boundaries

The 4 November 2026 comparison used Release builds, .NET SDK 10.0.302, .NET 10.0.10, Windows 10.0.26300, an Intel Core Ultra 9 275HX with 24 logical processors, workstation GC and the Balanced power plan. Timing processes ran without concurrent builds, tests or native viewers. The baseline came from the archived `2dd8eb949712f2196e445680e37f38547a971b10` source; the current measurements identify a development build based on `c9a2b92`. Assembly hashes identify the actual measured binaries independently of the dirty working-tree flag.

The existing `compare-m41` harness measured each saved case in a separate process for each implementation. Each backend has 30 complete-step samples: 15 samples across two repetitions in the same process. Backend order rotates by sample/repetition, initialization/reset is outside timing, and each path warms for at least eight calls and 0.5 seconds. Reported p50/p95 use nearest rank without trimming. Allocation counters are outside timed boundaries and cover the entire process, including persistent workers.

These are paired saved workloads, not repeated interleaved baseline/current process trials. They establish observed costs and allocation/error results on this machine. They do not isolate a causal regression from machine conditions or establish a universal latency bound.

| Saved input | Population / world | SHA-256 |
| --- | --- | --- |
| `uniform-fixed-500` | 500 / 1 × 1 | `DAF94FE6932B7D100DD9D60FFB8A78C357B284B461F60EE8DC33BDF85226EF36` |
| `uniform-fixed-2000` | 2,000 / 1 × 1 | `69EC581FAE90B2D3808653CD826A52465276576580CD8743A7CB42E023B59FC0` |
| `uniform-density-10000` | 10,000 / sqrt(20) × sqrt(20) | `EBD89F3413543AD1C9E467F6A25E7452FBCE90F3FD541E0BDD540D5FE73319D0` |

All three have seven species, cutoff 0.1 and timestep 1/144 in reduced units. Every baseline/current input hash was checked against the same saved file. The baseline core assembly SHA-256 is `FFE600F828C009F145E3925823F57F96B94DB83A561AA2936642B37C57817673`; the current comparison core is `8D75CA55A52DCF7440C8085598663B0669A16CEC41BA70C6B4BAF4DCCBCA51C4`. Full environments and raw samples remain under ignored `artifacts/live-editor/baseline-*` and `artifacts/live-editor/current-*`.

## Unchanged complete numerical steps

Values below are **p50 / p95 milliseconds**. Every sample in this table allocated **zero process-wide managed bytes**; reset/construction, diagnostics, rendering and serialization are excluded.

| Input | Backend | Baseline | Current |
| --- | --- | ---: | ---: |
| 500 fixed world | Direct | 0.8216 / 0.9295 | 0.9389 / 1.0817 |
| 500 fixed world | Grid, 1 worker | 0.2748 / 0.3095 | 0.2928 / 0.3478 |
| 500 fixed world | Grid, 2 workers | 0.1521 / 0.1837 | 0.1599 / 0.2136 |
| 500 fixed world | Grid, 4 workers | 0.1094 / 0.1324 | 0.1195 / 0.1656 |
| 500 fixed world | Grid, 8 workers | 0.0946 / 0.1772 | 0.1043 / 0.1698 |
| 2,000 fixed world | Direct | 13.8816 / 15.5717 | 14.6438 / 16.2671 |
| 2,000 fixed world | Grid, 1 worker | 3.3453 / 3.7711 | 3.3418 / 3.8780 |
| 2,000 fixed world | Grid, 2 workers | 1.7051 / 1.9444 | 1.7970 / 2.1533 |
| 2,000 fixed world | Grid, 4 workers | 0.9329 / 1.1511 | 0.9715 / 1.0923 |
| 2,000 fixed world | Grid, 8 workers | 0.5716 / 0.7041 | 0.6248 / 0.8212 |
| 10,000 fixed density | Direct | 168.6710 / 175.5052 | 191.6848 / 199.5796 |
| 10,000 fixed density | Grid, 1 worker | 6.0307 / 6.3886 | 6.6264 / 8.1752 |
| 10,000 fixed density | Grid, 2 workers | 3.0919 / 3.3113 | 3.3294 / 3.6967 |
| 10,000 fixed density | Grid, 4 workers | 1.6387 / 1.8549 | 1.7430 / 2.2523 |
| 10,000 fixed density | Grid, 8 workers | 1.0306 / 1.3249 | 1.0905 / 1.5023 |

Current medians range from 0.1% lower to 14.3% higher than their baseline observations. Four-worker grid medians increase by 9.2%, 4.1% and 6.4% respectively. These measurements do not support claiming an editor speedup or zero timing overhead. The fixed-density 10,000 serial p95 exceeds the 6.944 ms available per 1/144 step; four-worker p95 remains below it. Native input, drawing, presentation and diagnostics are additional costs, and a catch-up cap does not bound the duration of one dense step.

An independent calculation reproduced every p50/p95 from the raw complete-step samples and checked **900 zero-allocation samples** across the two implementations. Direct/grid accepted counts agree exactly; grid candidate/accepted counts are 28,844/7,038, 473,942/115,874 and 669,236/152,378 respectively. No measured step used fallback. Every reported current force and next-state error summary equals its matching baseline summary exactly. Across these cases the maximum grid-versus-direct absolute vector differences are **5.33023e-15 force**, **6.93889e-18 position**, and **3.47053e-17 velocity**, in their respective reduced units. These are local comparisons, not long chaotic trajectory or cross-runtime bitwise guarantees.

## Graphics-free controller and live editing

The `editor` harness constructs an actual `PlaybackController` and `ExperimentEditor`, uses grid workers one/four, and measures 101 samples per path. Every path warms for at least 64 calls and 0.5 seconds. It excludes diagnostics, rendering and file I/O. Scalar editing alternates between two already constructed configurations differing only by force scale 0.001, retains actual scientific events and groups the gesture into one undo action. Therefore edit timing includes validation, reconfiguration and history, but excludes candidate construction and UI parsing/layout.

Schema-2 controller reports prime one complete step after each baseline reset outside measurement, then time the second step. Each measured second step starts from identical once-advanced saved values with established evaluator capacity. This is a different precise input boundary from the core table's saved initial-state step, so subtracting their medians would not isolate controller overhead. Edit-only warmup discards history every 1,024 calls outside measured samples, preventing hundreds of thousands of discarded events from dominating retained memory.

The evolving drag and settled phases continue dynamics independently; their particle configurations differ across worker/process warmups. Treat those as actual evolving workloads, not same-state comparisons or a causal estimate of dragging overhead. Population growth/shrink are individual observations, including actual payload capture and undo/history allocation, not percentiles.

The initial schema-1 reports in `editor-500/` and `editor-10000/` accidentally timed the first step of a newly constructed simulation after every reset. Their `steadyController` allocations were evaluator setup, so those series are excluded from warmed-step claims. Their continuously evolved settled series did allocate zero, but the corrected schema-2 reports supersede them for the controlled measurements.

Corrected schema-2 observations are below, in **p50 / p95 milliseconds**. The two steady columns each allocated zero bytes in every sample: **808 process-wide zero-allocation samples** in total. The measured controller core hash matches the current comparison above; the viewer assembly SHA-256 is `9229515F561D309287CB361A4C496ADDC9AC092AC6AEAFB15AA8EA5F92255636`.

| Input / workers | Primed static controller | Rule edit only | Evolving edit + step | Evolving settled controller |
| --- | ---: | ---: | ---: | ---: |
| 500 / 1 | 0.2554 / 0.2744 | 0.0006 / 0.0012 | 0.2706 / 0.2867 | 0.2787 / 0.3026 |
| 500 / 4 | 0.0655 / 0.0774 | 0.0005 / 0.0010 | 0.0997 / 0.1567 | 0.1441 / 0.1712 |
| 10,000 / 1 | 6.5626 / 6.8943 | 0.0078 / 0.0082 | 6.6110 / 6.9598 | 6.5231 / 6.8863 |
| 10,000 / 4 | 1.6901 / 1.8541 | 0.0081 / 0.0088 | 1.7556 / 1.8490 | 1.7607 / 1.8220 |

Rule-only edits allocated 440 bytes per sample with one worker and 384 bytes with four workers, except one 500/one-worker sample that allocated 16,848 bytes as retained event-list capacity grew. Each evolving edit-and-step sample allocated 384 bytes, attributable to the actual edit/history path rather than stable stepping. Scalar edit cost grows with population because the core validates candidate configuration against the existing state's species; it does not copy particle state or replace an unchanged worker pool. The 10,000-particle edit-only p95 was 8.2–8.8 microseconds here. This isolated preconstructed scalar candidate does not bound population changes, matrix construction, worker switching, text parsing or native widget costs.

| Input / workers | Growth operation | Growth ms / bytes | Shrink ms / bytes |
| --- | --- | ---: | ---: |
| 500 / 1 | 500 → 625 | 3.8139 / 164,200 | 0.0787 / 98,152 |
| 500 / 4 | 500 → 625 | 0.0939 / 162,352 | 0.0395 / 98,152 |
| 10,000 / 1 | 10,000 → 12,500 | 11.4513 / 3,228,520 | 1.4366 / 1,975,000 |
| 10,000 / 4 | 10,000 → 12,500 | 1.8665 / 3,228,128 | 0.7291 / 1,965,424 |

Shrink returns to the original total using an even per-species distribution. These four pairs are single observations; the first structural call in each process can include JIT and lazy setup, and serial runs occur before four-worker runs. Their difference is not evidence of parallel generation speedup. The operation includes full actual structural snapshots for undo/replay, member selection/remapping and addition generation; it excludes the next numerical step and its possible capacity growth. Stable state-only/worker-switch allocation and replay contracts are additionally covered by tests.

The corrected raw reports are `artifacts/live-editor/editor-500-v2/editor.json` and `editor-10000-v2/editor.json`. Their nearest-rank summaries were independently recomputed from raw samples. Warmup counts are recorded: notably the 500/four-worker drag completed 5,701 warmup steps while the 10,000/four-worker drag completed 282, explaining why their settled geometry cannot serve as a matched-state comparison.

## Native presentation

Windows native checks used the NVIDIA GeForce RTX 5080 Laptop GPU with DesktopGL and vertical synchronization enabled. The default 500-particle window was exercised while playing and paused: directed matrix selection, exact numeric entry, invalid-draft rejection, live slider/template changes, population shrink/undo/redo, all four saves and their load actions, session replay, reset, focus loss/regain and editor visibility. Window sizes included the initial 1360 × 900 client area, a tall layout, the 420 × 340 minimum client area, and a maximized desktop. A typed UI scale of 1.25 enlarged controls while preserving the world's single geometric scale. The minimum layout necessarily leaves a small world and scrolls the toolbar/content within the compact panel; a larger window is more useful for inspecting patterns.

The native file workflow saved a one-step, four-event session containing coefficient and population edits, structural undo and redo. It also saved the corresponding exact 20-particle current-state experiment. Actual CLI replay of that UI-produced session matched every captured identity, species, position and velocity exactly on this runtime; its terminal time/step matched the capture's source metadata. A duplicate save failed without overwriting either file. Evidence is retained in `artifacts/live-editor/input-trace/`, including the four scientific/preset files, companions, replay report and native log.

Native input testing exposed and fixed short-chord modifier loss, text/key ordering, same-frame focus transitions and an obscured replay-completion message. The final input path captures modifiers at receipt, routes ordered key/text events through the same widgets, and prevents consumed input from reaching simulation shortcuts. Single-step, text/clipboard entry, mouse commit, focused-hotkey isolation and the mouse undo/redo actions were observed in the native window. The automation provider delivered no SDL events for its Enter/Tab commands; keyboard undo was also not established through that provider. Those particular physical-key interactions remain a manual platform check. The graphics-free compatibility executable tests the actual widget/panel routes for ordered text/Enter, invalid Tab/blur, blocked Apply and focus gained/lost within one input batch. Linux native graphics/input and other GPU/display configurations were not exercised.

The final local contributor gates passed: locked restore, formatting verification, Release solution build with zero warnings/errors, **388 tests**, native help, graphics-free MonoGame/widget compatibility, all three XML documentation warning probes, and **66 CLI smoke commands**. The test total comprises 248 core and 140 viewer tests. Verification logs are in `artifacts/live-editor/verification/`; the final CLI smoke reports are in `artifacts/cli-check/6019c755331940fda91de5058485e47c/`. Independent numerical/replay and native integration reviews were followed by fixes and these integrated checks.

Timing summaries cover approximately one wall second. Controller-update time includes scheduling, any catch-up and scheduled diagnostics; editor-update time covers widgets/actions; render submission excludes present/vsync. Samples at different evolved states are observations, not matched-workload causal overhead estimates. A retained edited summary identifies a bucket containing actual scientific edits, so a later Timings/F9 request can report that bucket without relying on automation reaction latency. Raw logs remain under ignored `artifacts/live-editor/`.

The following single-window summaries use a 1360 × 900 client area, display radius 2.5 pixels, playback speed 1, timestep 1/144 and diagnostics every 144 completed steps. The 500-particle case is the default seven-species random experiment, seed 1, grid/one worker. The 10,000-particle case loads the exact fixed-density input above with explicit `--backend grid --workers 4`. All times are milliseconds; columns report means except the labelled controller maximum.

| Workload / condition | Endpoint step | FPS | Controller mean / max | Editor mean | Render submission mean |
| --- | ---: | ---: | ---: | ---: | ---: |
| 500, visible idle | 1,682 | 144.0 | 0.295 / 2.315 | 0.009 | 0.230 |
| 500, visible drag bucket | 2,589 | 144.0 | 0.294 / 0.786 | 0.036 | 0.239 |
| 500, hidden idle | 8,696 | 144.0 | 0.328 / 0.713 | 0.004 | 0.065 |
| 10,000, visible idle | 2,572 | 127.1 | 1.980 / 16.968 | 0.006 | 4.963 |
| 10,000, visible drag bucket | 4,313 | 144.0 | 2.348 / 7.437 | 0.034 | 4.416 |
| 10,000, hidden idle | 8,191 | 140.3 | 6.149 / 17.016 | 0.004 | 0.913 |

Each automated drag bucket contains one applied coefficient value; this is a short native gesture, not a sustained high-frequency pointer stress test. The graphics-free evolving-edit harness above separately exercises repeated changes between completed steps. Recorded drag endpoints are 19:43:12 UTC (500) and 19:46:01 UTC (10,000). The logs are `native-final500/stdout.log` and `native-10000-grid/stdout.log` under `artifacts/live-editor/`; the measured native/core/viewer assembly hashes are retained in `native-10000/assemblies.json`.

Total dropped playback time was 0.097222 seconds in the shown 500-particle samples and 1.25 seconds in the shown 10,000-particle samples. Those are accumulated totals including startup and earlier frames, not the loss within each bucket. No sample reported numerical fallback. The 10,000-particle hidden sample has substantially more developed clustering: its last completed step accepted 1,743,090 directed interactions versus 266,274 in the earlier visible idle sample. Its higher controller cost cannot be attributed to hiding controls. Hiding also changes world viewport size, and render submission can include graphics-driver backpressure; the rendering columns are not an isolated UI GPU benchmark. Responsiveness in these bounded runs does not establish a safe limit for dense 10,000-particle populations, long sessions, all-pairs diagnostics, file parsing or structural generation.

## Reproduction

Build and run the [contributor gates](../CONTRIBUTING.md) first, then close native viewers and stop competing builds/tests. Use new output directories; output files use exclusive creation. With the retained M4.1 input files:

```powershell
dotnet run --project benchmarks/ParticleLife.Benchmarks -c Release --no-build -- compare-m41 artifacts/live-editor/current-uniform-fixed-500 artifacts/benchmarks/m4-1/inputs uniform-fixed-500 15 2
dotnet run --project benchmarks/ParticleLife.Benchmarks -c Release --no-build -- compare-m41 artifacts/live-editor/current-uniform-fixed-2000 artifacts/benchmarks/m4-1/inputs uniform-fixed-2000 15 2
dotnet run --project benchmarks/ParticleLife.Benchmarks -c Release --no-build -- compare-m41 artifacts/live-editor/current-uniform-density-10000 artifacts/benchmarks/m4-1/inputs uniform-density-10000 15 2
dotnet run --project benchmarks/ParticleLife.Benchmarks -c Release --no-build -- editor artifacts/live-editor/editor-500-v2 artifacts/benchmarks/m4-1/inputs/uniform-fixed-500.experiment.json 101
dotnet run --project benchmarks/ParticleLife.Benchmarks -c Release --no-build -- editor artifacts/live-editor/editor-10000-v2 artifacts/benchmarks/m4-1/inputs/uniform-density-10000.experiment.json 101
```

The archived baseline's `compare-m41` command consumes the identical input paths. See [M4.1 input preparation](performance-m4-1.md#reproduce-and-retained-evidence) for rebuilding actual input files when retained artifacts are absent. Repeated processes should use distinct output directories; copying these exact output names over existing evidence will fail safely.

For native observations, run the normal editor and the larger saved workload separately:

```powershell
dotnet run --project src/ParticleLife.MonoGame -c Release --no-build
dotnet run --project src/ParticleLife.MonoGame -c Release --no-build -- artifacts/benchmarks/m4-1/inputs/uniform-density-10000.experiment.json --backend grid --workers 4
```

Use **Timings** or F9 while visible/hidden, then drag a coefficient and request timings again. The last edited summary retains its own endpoint and configuration metadata. Wait for a completed bucket after the gesture. Keep builds, tests and other viewers stopped during measurement. M5 release and further solver optimization remain deferred.
