# P7 capacity and presentation

Capacity depends on density and the patterns that form, not just particle count. These measurements keep the actual saved population, world, rules and timestep. CPU results use binary64 and 24 grid workers; GPU results use the separately described binary32 implementation. A capture restores its stored values; switching arithmetic or replaying GPU actions does not promise the same subsequent trajectory.

## Workloads and measurement boundaries

The P1 fixed-density and tiled-pattern ladders were advanced for **120 actual full-population steps** on CPU before capture. The captured source clock is step 120, time 0.8333333333333314. Each resulting experiment stores the actual completed values rebased to step/time zero, with its source clock and CPU execution provenance retained. The tiled parents began as repeated evolved motifs; the additional 120 steps evolve the entire larger world. This is short emergence, not a claim that every scene reached a mature state.

The heterogeneous ladder deliberately changes the model: receiver row 0 keeps cutoff R, other rows use 0.1 R, retaining the same core fraction, coefficients, positions and world. It is a distinct workload. Dense cluster cases retain their concentrated initial placement and are evolved using the same procedure. The million-particle fixed-density cases are stretch measurements; no missing dense million-particle fixture was silently substituted.

For the matched CPU screen, each fresh process warms identical saved inputs for at least **128 reset/step calls and 0.5 seconds**, resets once more, then measures seven continuing steps. No reset occurs between samples. All 16 saved starts reproduced the same final particle hashes as their original CPU continuation, with unchanged input file hashes and no direct fallback. Nearest-rank p95 of seven samples is their maximum: these are exploratory capacity screens, not reliable tail-latency estimates.

An earlier eight-call warmup produced material fresh-process timing variation at 250k. Increasing the call floor reduced that variation: global fixed-density p50 changed from 52.82 ms to 16.87 ms, consistent with the 16.86 ms original continuation after 120 evolution steps. This suggests warmup sensitivity; it does not prove a particular runtime-tiering cause. One earlier process-wide allocation delta of 49,200 bytes remains unattributed. The repeated screen measured **zero process-wide, caller and persistent-worker bytes in every step series**; the additional counters establish this result without assigning the earlier delta to the solver.

## CPU results

Measured 5 November 2026 on an Intel Core Ultra 9 275HX, 24 logical processors, about 31.46 GiB RAM, Windows build 26300, Balanced power plan, .NET SDK 10.0.302/runtime 10.0.10. Measurements ran without competing builds, tests, native viewers or benchmarks. The timestep was 1/144 reduced time units. Achieved speed is simulated time per measured wall second, excluding separately recorded progress-file I/O. It includes loop/counter overhead and is distinct from the quotient of timestep and median step time. Rendering, UI and scheduled diagnostics are excluded.

| Scene | Particles | Step p50 / p95 (ms) | Achieved speed |
| --- | ---: | ---: | ---: |
| Fixed density | 10,000 | 0.681 / 0.848 | 9.16× |
| Fixed density | 50,000 | 3.190 / 3.473 | 2.12× |
| Fixed density | 100,000 | 5.926 / 6.394 | 1.15× |
| Fixed density | 250,000 | 16.872 / 17.168 | 0.41× |
| Fixed density, stretch | 1,000,000 | 87.359 / 88.268 | 0.08× |
| Heterogeneous ranges | 10,000 | 0.388 / 0.436 | 16.46× |
| Heterogeneous ranges | 50,000 | 1.566 / 1.824 | 4.29× |
| Heterogeneous ranges | 100,000 | 3.278 / 3.554 | 2.04× |
| Heterogeneous ranges | 250,000 | 9.366 / 10.631 | 0.72× |
| Heterogeneous, stretch | 1,000,000 | 49.359 / 52.516 | 0.14× |
| Tiled pattern, then globally evolved | 10,000 | 0.583 / 0.647 | 11.03× |
| Tiled pattern, then globally evolved | 50,000 | 2.117 / 2.258 | 3.17× |
| Tiled pattern, then globally evolved | 100,000 | 4.199 / 4.299 | 1.65× |
| Tiled pattern, then globally evolved | 250,000 | 11.399 / 12.623 | 0.59× |
| Concentrated cluster | 10,000 | 11.172 / 11.956 | 0.65× |
| Concentrated cluster | 50,000 | 42.008 / 45.269 | 0.16× |

Sparse and tiled 100k cases sustained the requested simulation speed in this CPU screen; the dense 10k cluster did not. This is why a single particle-count limit would misdescribe the application. The million-particle runs completed with all particles and rules retained, at reduced achieved speed. Their process working sets were approximately 846 MiB for global ranges and 1,128 MiB for heterogeneous ranges. Working set includes loaded JSON/state, runtime, measurement and managed heap residency; it is not the solver's retained-buffer footprint or a monotonic capacity estimate.

The measured benchmark SHA-256 was `28D02A3957B12F5AB791ED3F3630E1426F022158547DDFA357901B224250B25A`; core SHA-256 was `A4CFDE935EF1F641E7244C6C948C4C3C6524AA036DD42557DAEEC65DC1FB16B4`. The warmup/attribution source was committed as `6ca20ff`. Raw envelopes, per-sample allocation attribution, source/final particle hashes, deadlines and exact command arguments are under the P7 worktree's ignored `artifacts/implementation/p7-capacity/`; `warm128-summary.csv` and `common-start-hashes.csv` summarize them. All 16 original evolution screens and both matched rescreen sets completed within their stated deadlines; timeout results are never converted to timings.

## Reproduction

Build once before the exclusive measurement window. Use fresh destinations for preparation, reports and captures. The supervisor owns only its child process and records an honest timeout if the deadline expires.

```powershell
dotnet build benchmarks/ParticleLife.Benchmarks/ParticleLife.Benchmarks.csproj -c Release --no-restore
dotnet benchmarks/ParticleLife.Benchmarks/bin/Release/net10.0/ParticleLife.Benchmarks.dll prepare-p7 PATH-TO-P1-INPUTS artifacts/benchmarks/p7/inputs ladder both
dotnet benchmarks/ParticleLife.Benchmarks/bin/Release/net10.0/ParticleLife.Benchmarks.dll measure-p7-cpu INPUT.json EVOLUTION-REPORT.json 24 120 7 180 COMMON-START.json
dotnet benchmarks/ParticleLife.Benchmarks/bin/Release/net10.0/ParticleLife.Benchmarks.dll measure-p7-cpu COMMON-START.json MATCHED-CPU-REPORT.json 24 0 7 180
```

Prepare the separate P1 stretch manifest with selection `stretch` for the million-particle cases. A fresh run may measure different timing while retaining the same saved start and CPU continuation. Do not infer native FPS or input latency from these commands.

## GPU results

The same sixteen saved CPU-evolved inputs were explicitly converted to binary32 particle values and run on an NVIDIA GeForce RTX 5080 Laptop GPU, OpenGL 4.6 / NVIDIA 621.01, on the same host. IDs, species, population and source files were retained. Reports include every actual float model scalar/rule and the conversion error; the global million-particle input's maximum position conversion error was about 1.91e-6 reduced length units. CPU and GPU evaluate the same supported model with different arithmetic and accumulation order; this comparison does not assert identical continuations.

Each fresh GPU process warmed the identical converted input for at least 128 complete steps and 0.5 seconds, then reset once and measured seven continuing steps. Warm resets use production replacement, including fresh device buffers. Normal game drawing and Present were suppressed; the harness polled zero-timeout fences without display pacing. Wall step durations include submission, driver work and polling. Achieved speed also includes collection of delayed timestamp results. CPU polling overlaps GPU work, so their durations cannot be added. The native editor has its own input/draw/present schedule and CPU pump budget.

| Scene | Particles | Wall step p50 / p95 (ms) | GPU integration p50 (ms) | Unpaced achieved speed |
| --- | ---: | ---: | ---: | ---: |
| Fixed density | 10,000 | 0.265 / 0.524 | 0.105 | 20.02× |
| Fixed density | 50,000 | 0.413 / 1.454 | 0.227 | 11.09× |
| Fixed density | 100,000 | 0.689 / 1.455 | 0.478 | 8.39× |
| Fixed density | 250,000 | 1.626 / 2.750 | 1.182 | 3.70× |
| Fixed density, stretch | 1,000,000 | 6.783 / 7.971 | 5.163 | 0.996× |
| Heterogeneous ranges | 10,000 | 0.251 / 0.890 | 0.095 | 17.79× |
| Heterogeneous ranges | 50,000 | 0.393 / 0.632 | 0.227 | 15.26× |
| Heterogeneous ranges | 100,000 | 0.722 / 2.113 | 0.477 | 7.26× |
| Heterogeneous ranges | 250,000 | 1.560 / 2.518 | 1.186 | 3.79× |
| Heterogeneous, stretch | 1,000,000 | 6.649 / 8.375 | 5.358 | 1.000× |
| Tiled pattern, then globally evolved | 10,000 | 0.254 / 0.346 | 0.089 | 22.47× |
| Tiled pattern, then globally evolved | 50,000 | 0.311 / 0.477 | 0.144 | 18.89× |
| Tiled pattern, then globally evolved | 100,000 | 0.525 / 1.499 | 0.285 | 9.03× |
| Tiled pattern, then globally evolved | 250,000 | 0.949 / 2.225 | 0.633 | 5.39× |
| Concentrated cluster | 10,000 | 6.953 / 7.518 | 6.788 | 0.972× |
| Concentrated cluster | 50,000 | 12.730 / 13.481 | 12.485 | 0.540× |

All cases completed within the 180-second host / 200-second supervisor budgets. Every timed command group had complete query coverage; every measured step/poll series recorded zero process-wide managed allocations and zero full particle-state readbacks. Allocation deltas exclude subsequent query collection/report objects. The million-particle global grid-build median was 0.181 ms, separate from its 5.163 ms force/integration median. The heterogeneous GPU path still uses the conservative maximum-cutoff grid, so its large-population gain differs from the CPU receiver-row optimization.

The million-particle cases are approximately real-time in this unpaced screen, with no display/input allowance. The heterogeneous value rounds to 1.000× but was 0.999864×. They establish a completed stretch measurement, not a promise of sustained native real-time interaction. Dense structures remain expensive at much lower counts. The retained dispatch policy and the earlier dense warmup timeout are explained in [the GPU scheduling decision](gpu-integration.md#p7-receiver-dispatch-decision); the timeout is not a timing sample. The selected scheduling revision is `22110cb`; the measured native assembly SHA-256 is `6909BD993443904324711912F8BB9E769E1D10AD2DC44A20D891A0B51060B9FD`. Final changes after measurement were XML comments only.

### Resident drawing, observations and explicit storage

Render-only samples reused each final resident state in a **1280 × 720** target with a fitted isotropic camera, opaque species colours, normal blending and no trails, glow or heatmaps. All particles were submitted, with zero particle uploads or state captures. Each radius had one warm draw and seven retained draws. GPU timestamps cover draw commands, excluding clear, target unbind, readback and Present; they are not FPS measurements.

| Scene | Particles | Draw GPU p50, radius 1 / 4 / 12 px (ms) | Retained 128² observation wall p50 (ms) |
| --- | ---: | ---: | ---: |
| Fixed density | 10,000 | 0.006 / 0.011 / 0.037 | 0.139 |
| Fixed density | 250,000 | 0.113 / 0.177 / 0.831 | 0.241 |
| Fixed density, stretch | 1,000,000 | 0.536 / 0.789 / 3.720 | 0.645 |
| Concentrated cluster | 50,000 | 0.025 / 0.030 / 0.105 | 0.152 |

Larger screen discs increase overdraw without changing interactions; visual quality and camera framing therefore need separate budgets. Optional observations read **65,564 bytes** per 128² sample, preserving source step/time and outside counts. Seven retained samples followed one cold sample. Lazy shader/resource setup is separate: the cold observation took 4.00 ms in the global million-particle run, and 34.52 ms in the first 10k run. Sampling is off unless requested by overlays, statistics or tracking; cadence is a visible user choice.

Requested resident simulation/observation buffer payloads were 1,410,640 bytes at 10k, 31,308,136 bytes at 250k and 125,033,352 bytes at 1M (about 119 MiB). These counts exclude driver overhead, programs/query objects, the renderer and presentation targets. The million-particle initial conversion took 46.89 ms; program/workspace construction, packing/upload and its completion fence took another 69.74 ms. These are explicit setup costs, not ordinary steps.

An explicit terminal capture then read 32 bytes per particle. At 1M global particles it read 32,000,000 bytes and completed in 36.29 ms. The canonical captured-session JSON embeds actual initial and terminal values: that file was **480,622,708 bytes**, with 3,394.44 ms serialization and 494.09 ms writing measured separately. Whole-process working set after serialization was 7,390,957,568 bytes, including loaded source/state, serialization objects/strings, runtime and heap residency; it is not device memory or steady solver storage. Capturing and exporting million-particle recordings is therefore a substantially heavier operation than resident playback. The library's bounded share/import and recording budgets still apply; a completed stretch benchmark does not waive them. The separate [50k brush-storage measurement](discovery-and-recording.md) covers compact history and prepared file operations.

Raw results, input/capture hashes, commands, watchdog records, each timing/allocation sample and explicit phase boundaries are retained in the tuning worktree's ignored `artifacts/implementation/p7-gpu-capacity-512m/`. Reproduce a selected common input after building the native project, with no competing measurements/builds/viewers:

```powershell
dotnet build src/ParticleLife.MonoGame/ParticleLife.MonoGame.csproj -c Release --no-restore
dotnet src/ParticleLife.MonoGame/bin/Release/net10.0/ParticleLife.MonoGame.dll --gpu-capacity ABSOLUTE-COMMON-START.json EXPECTED-SHA256 FRESH-OUTPUT-DIRECTORY
```

## Native presentation acceptance

All sixteen matched CPU/GPU capacity cases above are complete. The consolidated native review is in progress, with CPU 50k idle windows and one clean running input-proxy window recorded. Economy and Luminous idle windows reached 144 graphics FPS; the substantially evolved running Economy scene reached about 0.136× playback and an input-proxy p95 of 6.874 ms across 1,024 retained samples. The earlier user pan assessment was uncertain because of a focus problem; later Preview 08 physical checks pass as scoped below. Paused and mixed resize windows do not establish a running quality comparison. GPU 100k idle windows now also reach 144 graphics FPS, with Economy 27 steps/s (0.187×) and Luminous 34 steps/s (0.236×). Their evolved endpoints differ and input n=1 is the Print click only, so these are neither a matched quality speedup comparison nor physical pan evidence. Preview 08 passes the focused same-clock CPU display-cache recheck; later representative dense/live-edit functional checks are recorded in the visual checklist; their mixed unreset timing prints are not benchmarks. Saved particle state remains exact; prior GPU100k idle timing is not invalidated by that affected CPU rendering path. The [visual review](visual-review-checklist.md) records actual CPU/GPU UI, frame intervals, draw/Present and input-proxy measurements separately. No solver or isolated resident-draw result above establishes those outcomes.

For a native window, settle the workload, backend, quality, camera and window size, reset with F10, collect at least ten seconds, then print with F9. The source also offers **Playback > CPU search and measurements > Reset timing window / Print viewer timings**; Preview 06 button observations establish Print/Reset operation; Preview 07 passes the focused input-focus and visible-feedback recheck; Preview 08 physical running-pan results are recorded below; later representative dense/live-edit functional checks are recorded in the checklist. Each bounded distribution retains up to 1,024 samples. Record frame intervals, draw, Present CPU wait and observed-input update-to-present p50/p95/counts, plus graphics FPS, steps/s, achieved playback and dropped time. Collect idle and dragging separately for each quality/backend. The printed context is the current publication/display state, not historical metadata for all samples; reset after setup changes. The input proxy excludes OS delivery, scanout and subsequent numerical command application.

The GPU 100k windows use binary32, dt 1/144, seven species, diagnostics/observations/tracking off, checkpoint cadence 600, no media capture, 1359 × 901 backbuffer / 825 × 825 canvas and radius 2.5 screen pixels. After timing reset, each ran idle for at least twelve seconds. Frame p50/p95/max were 6.944/6.978/33.092 ms (Economy) and 6.944/6.983/29.332 ms (Luminous), each with 1,024 retained samples; draw p95 was 0.664/0.835 ms and Present CPU wait p95 2.174/2.119 ms. Exact logged clocks are step 10,355/time 71.909722222209908 and step 13,318/time 92.486111111094118; screenshots show later publications. All 100,000 particles were submitted with zero upload and no fallback. Cumulative full-state reads (17/43) include the checkpoint policy and prior lifecycle; they are not per-window transfer deltas. Detailed context and limitations are in `artifacts/implementation/visual-review/V14-gpu100k-07-report.json` and `.txt`. Later Preview 08 physical running-pan results are recorded below.

Native GPU playback pumps ready stages and completed steps within a bounded 2 ms update budget (default cap eight completed steps), alongside drawing and Present pacing; it is not limited to one stage per frame. The unpaced solver runner suppresses drawing/Present and polls continuously. These native windows do not separate evolved force cost from scheduling gaps, so that causal split remains unmeasured. Printed zero candidate/accepted counts are CPU counters, not a measurement of GPU force work.

A later manual GPU50k dense Economy report records 143.8 FPS / 22 steps/s (0.153×), frame p95 10.059 ms across 830 samples and observed-input proxy p95 10.034 ms across 278 samples, at logged step 699/time 4.8541666666667416 and 1360 × 900. Mixed focus/resize/history and fewer than ten seconds exclude it from clean fixed-size acceptance. The user confirms shared middle/right pan, focus and visible timing feedback on that actual scene; later CPU50k Luminous and GPU100k physical running-pan results are recorded below.

### Preview 08 physical running-pan observations

The user confirms CPU50k Luminous and GPU100k Economy/Luminous panning responsive at fixed 1637 × 900 (canvas 1103 × 824). CPU used binary64/grid24; GPU used binary32 with saved CPU grid24 and checkpoint cadence 600. All reports use seven species, dt 1/144, requested speed 1, radius 2.5 screen pixels, UI scale 1 and diagnostics/observations/tracking off. These are current-scene native observations, separate from the unpaced solver screens above.

| Logged UTC / execution | Completed step / time | FPS / steps per second / playback | Frame p50 / p95 / max ms | Input proxy p50 / p95 / max ms |
| --- | --- | --- | --- | --- |
| 12:07:58.6963064 / CPU50k Luminous | 6,421 / 44.590277777771668 | 144 / 121.1 / 0.841× | 6.945 / 7.003 / 161.795 | 6.782 / 6.913 / 7.084 |
| 12:19:54.6347590 / GPU100k Economy | 4,100 / 28.472222222219777 | 144 / 145 / 1.007× | 6.944 / 6.971 / 23.855 | 6.821 / 6.891 / 23.767 |
| 12:20:59.1798894 / GPU100k Luminous | 9,490 / 65.902777777766829 | 144 / 29.9 / 0.207× | 6.944 / 6.962 / 7.117 | 6.830 / 6.892 / 7.005 |

Each frame/input distribution retains 1,024 samples. CPU collection follows an observed final Reset and the user's approximately twenty-second running pan; the 161.795 ms frame maximum remains part of that result. GPU reports are **rolling retained tails after user-confirmed twenty-second pans**: no new Reset notice was observed, and 1,024 frames at 144 FPS span less than the full interaction. They are not fresh-reset windows. Draw/Present CPU-wait p95 are respectively 1.258/5.599 ms (CPU), 0.741/4.499 ms (GPU Economy) and 0.766/2.058 ms (GPU Luminous); cumulative dropped playback is 0.993056 / 0.006944 / 10.673611 seconds. Source: `artifacts/implementation/visual-review/native-11-preview08.console.log` and the native ledger. Later paused screenshots do not replace these running logged clocks.

Four earlier scientific edits in the GPU history are unclassified until their saved artifact is inspected. Do not assume they changed or preserved the physical configuration. Evolved endpoints and retained histories differ, so the quality settings do not establish a causal speedup/cost comparison. The input metric excludes OS delivery, scanout and later queued-command completion; user responsiveness confirmation is separate from that application proxy. These observations do not complete V14 or the whole native review; dense/live-edit and remaining explicit cases stay pending.
