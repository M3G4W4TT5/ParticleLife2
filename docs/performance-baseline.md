# Serial reference performance

The M2 benchmark uses the direct, directed all-pairs evaluator with double-precision state and the specified damped semi-implicit Euler step. It establishes a reference for M4; it does not establish an interactive particle-count budget.

The [M4 comparison](performance-m4.md) reruns direct and grid on the same build and saved inputs. This page retains the original M2/M3 measurements and their provenance; its timings are historical context.

## Reproduce

After the documented Release build, run from the repository root without a debugger:

```powershell
dotnet run --project benchmarks/ParticleLife.Benchmarks -c Release --no-build -- latency artifacts/benchmarks/reproduction 31 all
dotnet run --project benchmarks/ParticleLife.Benchmarks -c Release --no-build -- latency artifacts/benchmarks/replay 31 artifacts/benchmarks/reference/uniform-fixed-500.experiment.json
$env:PARTICLELIFE_BENCHMARK_INPUTS = (Resolve-Path artifacts/benchmarks/reproduction).Path
dotnet run --project benchmarks/ParticleLife.Benchmarks -c Release --no-build -- bdn --filter '*uniform-fixed-500*' '*uniform-fixed-10000*' --job short
```

`quick` samples 500-particle uniform, dense-cluster, narrow-band and boundary/overlap cases. `all` adds 2,000 and 10,000 particles and fixed-density domains. A single case name or saved experiment path can replace `all`; run the executable without arguments to list names. The replay command requires the retained baseline input; on another checkout, supply a saved input from the run being compared. Use saved values when comparing implementations, avoiding dependence on future random-generator behaviour. BenchmarkDotNet accepts its own filters and job settings after `bdn`.

BenchmarkDotNet saves inputs and session metadata under `artifacts/benchmarks/bdn-inputs`, outside its temporary build folders. Existing inputs are loaded on subsequent runs. Set `PARTICLELIFE_BENCHMARK_INPUTS` to an absolute directory containing previously saved case files to compare the same values; use a fresh directory to deliberately generate new inputs.

The latency runner saves each full experiment beside `latency.json`, including actual coordinates, velocities, species, stable identities, directed matrix, reduced-unit parameters and generator information. The report records input hashes, core and benchmark assembly hashes, Git commit and dirty status, runtime/SDK, OS, CPU, build configuration and one solver thread. Keep those inputs with raw measurements when comparing runs. Generated benchmark files are ignored by Git.

## Method and interpretation

Every timed step starts from the same initial state; resets occur outside the measured region. The runner warms each case for at least 64 steps and one second, recording the actual warm-up count. This gives tiered compilation time to settle but is not a guarantee against all JIT or scheduler effects. It then records complete-step latency and separately times force evaluation (including soft walls) and integration. Percentiles use nearest ranks. Stage timings include public API validation; they need not sum exactly to whole-step timings. There is no index-build stage in the direct reference.

Allocation deltas use `GC.GetAllocatedBytesForCurrentThread` around each measured call. BenchmarkDotNet provides an independent memory-diagnoser run. Its iteration setup restores initial state outside timing and forces a single invocation per iteration; short cases can therefore have wide confidence intervals.

Diagnostics are measured separately, with at most five samples per case. They use a cluster distance equal to the interaction cutoff and include the quadratic connected-component calculation. Their costs are excluded from core step timings. Rendering is absent. Candidate and accepted pairs are directed; coincident-pair counts are unordered. The boundary case retains off-world particles and one deliberately coincident pair.

Uniform fixed-domain cases use `[0,1]^2`. Fixed-density cases use side length `sqrt(N/500)`, keeping 500 particles per reduced area. Dense clusters lie inside `[0.475,0.525]^2`, so every distinct pair is within the 0.1 cutoff. Bands occupy `[0,1] x [0.49,0.51]`. All cases use seven species, seed 1729, and otherwise the initial model defaults. The saved values are authoritative; the seed alone is not a cross-runtime reproducibility guarantee.

The reported numerical difference compares separately evaluated public stages with `Simulation.Step` on identical input. It checks stage composition and buffer wiring, not the force law independently. Mathematical tests provide that evidence. No optimized solver exists yet, so this is not an optimization speedup or error comparison.

## Recorded results

Measured on 4 November 2026 at clean commit `65fd5d9b2d4e5acf1b7f5851ceb140e787d5602a`, after the integrated Release build and correctness checks. The reference machine was an Intel Core Ultra 9 275HX with 24 logical processors, running Windows 11 build `10.0.26300.9711`, .NET SDK `10.0.302` and .NET runtime `10.0.10`, x64 RyuJIT. The solver used one thread, workstation GC and default tiered compilation. Stopwatch frequency was 10 MHz. The runtime's OS description reports `Microsoft Windows 10.0.26300`; the fuller Windows version above comes from BenchmarkDotNet.

The latency run used 31 measured calls per numerical operation and five separate diagnostic samples per case; its power-plan setting was not captured. Warm-up counts ranged from 64 to 736. All cases used `R=0.1`, `F0=1`, `beta=0.3`, damping half-life `0.025`, wall width `0.05`, wall strength `1.44`, timestep `1/144`, seven species and generator seed 1729. Fixed-density square sides were 1, 2 and `sqrt(20)` for 500, 2,000 and 10,000 particles respectively; other cases used a unit-square nominal world.

### Complete steps and pair counts

Times are milliseconds per step. Counts refer to the unchanged initial state of each sample. Every candidate is a distinct ordered pair; accepted pairs have positive separation strictly below the cutoff.

| Case | Step p50 (ms) | Step p95 (ms) | Candidate pairs | Accepted pairs |
| --- | ---: | ---: | ---: | ---: |
| uniform-fixed-500 | 0.6708 | 0.7136 | 249,500 | 7,038 |
| uniform-density-500 | 0.6820 | 0.7131 | 249,500 | 7,038 |
| cluster-500 | 2.2056 | 2.3953 | 249,500 | 249,500 |
| band-500 | 0.8975 | 1.0100 | 249,500 | 46,844 |
| uniform-fixed-2000 | 10.7949 | 12.0745 | 3,998,000 | 115,874 |
| uniform-density-2000 | 6.5669 | 7.2630 | 3,998,000 | 29,816 |
| cluster-2000 | 37.6164 | 38.5578 | 3,998,000 | 3,998,000 |
| band-2000 | 15.3676 | 22.0466 | 3,998,000 | 760,504 |
| uniform-fixed-10000 | 281.0220 | 383.5787 | 99,990,000 | 2,863,176 |
| uniform-density-10000 | 108.5678 | 112.4539 | 99,990,000 | 152,378 |
| cluster-10000 | 1157.7573 | 1250.4154 | 99,990,000 | 99,990,000 |
| band-10000 | 541.2981 | 631.7732 | 99,990,000 | 18,863,466 |
| boundary-overlap-500 | 0.6748 | 0.6846 | 249,500 | 5,030 |

`boundary-overlap-500` recorded exactly one unordered coincident pair; all other cases recorded zero. The two 500-particle uniform cases have identical numerical inputs and different case metadata. Their timing difference is measurement variation, not a different workload or an optimization.

### Separately sampled stages and diagnostics

Each cell gives p50 / p95 in milliseconds. Force timing includes both pair and wall evaluation. Diagnostics use inclusive cluster distance `0.1`; their five-sample p95 is the largest observed sample and is only a coarse indication of variation.

| Case | Forces (ms) | Integration (ms) | Diagnostics (ms) |
| --- | ---: | ---: | ---: |
| uniform-fixed-500 | 0.6691 / 0.7115 | 0.0011 / 0.0050 | 1.2046 / 1.2660 |
| uniform-density-500 | 0.6789 / 0.7046 | 0.0011 / 0.0014 | 1.2276 / 1.2421 |
| cluster-500 | 2.2677 / 2.4183 | 0.0012 / 0.0140 | 1.1824 / 1.2154 |
| band-500 | 0.8799 / 0.9668 | 0.0012 / 0.0019 | 0.7511 / 0.7850 |
| uniform-fixed-2000 | 10.7117 / 12.3404 | 0.0053 / 0.0075 | 19.1580 / 22.8300 |
| uniform-density-2000 | 6.3808 / 6.9270 | 0.0048 / 0.0134 | 17.8196 / 20.2836 |
| cluster-2000 | 39.1131 / 39.9352 | 0.0059 / 0.0105 | 20.9419 / 24.3068 |
| band-2000 | 15.8884 / 21.4411 | 0.0059 / 0.0080 | 15.2519 / 15.4189 |
| uniform-fixed-10000 | 276.7756 / 365.9188 | 0.0319 / 0.0380 | 471.0436 / 471.1975 |
| uniform-density-10000 | 107.2690 / 111.8467 | 0.0308 / 0.0349 | 448.8721 / 452.5127 |
| cluster-10000 | 1196.4102 / 1304.7161 | 0.0290 / 0.0496 | 683.5915 / 735.4543 |
| band-10000 | 538.6627 / 623.4019 | 0.0257 / 0.0310 | 454.2636 / 480.3675 |
| boundary-overlap-500 | 0.6682 / 0.6737 | 0.0011 / 0.0013 | 1.4586 / 1.4674 |

Every sampled complete step, force evaluation and integration allocated **0 managed bytes** on the calling thread, across all 13 cases. Diagnostics deliberately allocated 4,128 bytes per sample at 500 particles, 16,128 at 2,000 and 80,128 at 10,000. Each case's maximum position and velocity differences between separate stages and `Simulation.Step` were exactly zero in this runtime. This is a stage-composition check, not evidence of cross-runtime bitwise identity or an independent force-law comparison.

Force evaluation dominates these serial workloads. Diagnostics can cost more than a step, which supports sampling them explicitly outside the numerical loop. The large dense case takes over one second per reference step on this machine; no interactive capacity is claimed for it. There is no spatial index, parallel solver or measured speedup in this baseline.

### BenchmarkDotNet confirmation

BenchmarkDotNet `0.15.8` used the same saved `uniform-fixed-500` and `uniform-fixed-10000` inputs through `PARTICLELIFE_BENCHMARK_INPUTS=artifacts/benchmarks/reference` (resolved to an absolute path). Its ShortRun performed one launch, three warm-up iterations and three measured iterations, with one invocation and no unrolling per iteration. The memory diagnoser reported no managed allocation for either case.

| Case | Mean (ms) | Reported error (ms) | Standard deviation (ms) | Managed bytes/op |
| --- | ---: | ---: | ---: | ---: |
| uniform-fixed-500 | 0.6928 | 0.09642 | 0.00528 | 0 |
| uniform-fixed-10000 | 286.3458 | 226.48066 | 12.41417 | 0 |

The reported error is the half-width of BenchmarkDotNet's 99.9% confidence interval. With only three measurements these intervals are wide, especially for 10,000 particles. BenchmarkDotNet also warned that the 500-particle iteration was shorter than its recommended minimum. These runs confirm execution and allocation behaviour; they are insufficient to establish a tight timing budget.

BenchmarkDotNet temporarily selected the High performance power plan and its log confirmed restoration of Balanced afterward. The latency runner did not capture power-plan state, so the two timing series should not be treated as a controlled before/after comparison. Machine load, JIT tiering, thermal behaviour, power management, sampling count and operating-system scheduling limit timing claims; no shared-CI timing threshold is enforced. These are repeated single-step initial-state workloads, not long-run emergence statistics or rendering measurements.

### Retained inputs and raw evidence

The local ignored directory `artifacts/benchmarks/reference/` holds `latency.json`, BenchmarkDotNet `session.json`, and all 13 complete `.experiment.json` inputs. Raw BenchmarkDotNet logs and reports remain under ignored `BenchmarkDotNet.Artifacts/`. These generated files are not source dependencies or committed results. Retain or explicitly transfer this evidence when comparing another runtime or implementation; a fresh checkout does not contain the large benchmark inputs. The hashes below verify the exact input files, including metadata and whitespace, and were checked against the retained files.

The measured core assembly SHA-256 was `CF77E9303B634A89DC531C51D6044D8290C27FDDE41791CB5110DECAA6C4E839`; the benchmark assembly SHA-256 was `95FFA96D8B988E83C3D36CBF055A7EA5D84A3CC8ACF6DC6FBBD75E5F933BB0CE`.

Append `.experiment.json` to each case name for its input filename:

| Case | Input SHA-256 |
| --- | --- |
| uniform-fixed-500 | `DAF94FE6932B7D100DD9D60FFB8A78C357B284B461F60EE8DC33BDF85226EF36` |
| uniform-density-500 | `138E906CF60B56E52D582D6D3B44747EFEBC5EE63302E821A7144A530EE63EAC` |
| cluster-500 | `42476B135E48C19CEA97CD1C5787A1D35A7CDBF933D2EE5AFADE4578103E9523` |
| band-500 | `27970013401BDDE1C703F38B7F25C09F1C098852576CE8AF2A6AE6BAEFB51778` |
| uniform-fixed-2000 | `69EC581FAE90B2D3808653CD826A52465276576580CD8743A7CB42E023B59FC0` |
| uniform-density-2000 | `B088D368593FEC8CAFA234276C7891DEB7B7DB97E695711C5759E7474CFF1E7C` |
| cluster-2000 | `F3E62D59A2AE5DE6ACDEC4F6B604DBA6A513038A76B9A88C5FFC059C17CF1F36` |
| band-2000 | `DCC964B2B376132C903BA94EF89B8605C86EF53895418F7345D876A5DC07B423` |
| uniform-fixed-10000 | `CC5D789B40C7A687F2DE723377F25079105E939B4071B5EAE08558E87461A4E1` |
| uniform-density-10000 | `EBD89F3413543AD1C9E467F6A25E7452FBCE90F3FD541E0BDD540D5FE73319D0` |
| cluster-10000 | `2D416AB8C163FED20DA5540347CAB9F75746BE560A9D20CF7A5924E4B14D9A9D` |
| band-10000 | `0679C7F9E5C6D2CEDF20B9D6B6002A40343D5FB4FD4C29AFC08C3B6A63EC633D` |
| boundary-overlap-500 | `185B5E6ACB6E293A71A8A94C756C03EE86763A993160F05EBCB9383B6582B1B0` |

## M3 interactive observation

The MonoGame viewer was exercised on 4 November 2026 with the source committed as `4c0a9409ea3270b29f523c966d88f9c391745d6c`. The Release binaries were built immediately before that commit; the retained hashes below identify those measured binaries. The machine used an Intel Core Ultra 9 275HX (24 logical processors), NVIDIA GeForce RTX 5080 Laptop GPU, Windows 11 Home build 26300, SDK 10.0.302 and .NET runtime 10.0.10. DesktopGL 3.8.6 used vertical synchronization in a 1280 × 800 client window. The simulation remained serial.

The input was the default 500-particle, seven-species random experiment with seed 1, unit-square world, cutoff 0.1, timestep 1/144 and all other migration defaults. Its complete initial values were saved through the actual viewer, loaded again through its startup argument, and replayed successfully through the CLI for 120 steps. The saved input SHA-256 is `F39ADA28798A3F6A95200C1F811DEDCE335D3ED67BB760C9F4209F4FD8F56B25`. Diagnostics sampled every 144 completed steps. Playback targets one reduced time unit per wall second and caps catch-up at eight fixed steps per frame.

Three manually requested timing summaries each describe the latest approximately one-second interval. Update timing surrounds controller advancement, including scheduled diagnostics; it excludes input handling, drawing, GPU work and presentation. FPS includes presentation pacing. The help overlay was visible during collection.

| Completed step when requested | FPS | Mean controller update (ms) | Maximum controller update (ms) | Total dropped playback (s) |
| ---: | ---: | ---: | ---: | ---: |
| 3,511 | 144.0 | 0.660 | 1.878 | 0 |
| 6,295 | 143.1 | 0.686 | 1.992 | 0 |
| 9,771 | 144.0 | 0.736 | 1.791 | 0 |

These observations support responsiveness for this particular evolving 500-particle workload on this machine. They are not latency percentiles, an isolated rendering benchmark, an allocation measurement or a real-time capacity guarantee. Power plan, thermal state and background load were not controlled. The M2 large-population limits still apply: an eight-step cap cannot interrupt one expensive quadratic step or diagnostic. No solver optimization or numerical-model change was made in M3.

Native checks covered initial rendering, startup from a saved experiment, invalid reload retaining the current completed state, successful reload/reset to time and step zero, clearing stale measurements, three preset selections, diagnostic updates/toggling, and separate state-resample and matrix-change saves. Comparing the actual saved files confirmed that state resampling retained matrix values and matrix randomization retained initial particle values. Wide, compact and tall window shapes preserved isotropic geometry, particle count and paused state. The viewer closed without stderr output.

The Right-arrow keyboard step advanced exactly once. Other commands were exercised through clickable help rows and the playback badge: the Windows automation helper's letter/nonextended key injection did not deliver SDL key events, so those physical keyboard shortcuts were not independently verified by that helper. Linux CI builds the viewer and runs display-free tests; Linux/macOS native graphics startup remains unverified.

The seven automated CLI/controller parity cases matched identities after 120 completed steps with zero measured position, velocity and time differences on this runtime, using `1e-12` absolute plus `1e-12` relative tolerances. Those tests exercise the actual controller used by the window, including irregular frames, pause/resume, reset and single-step. They establish host parity for the reviewed direct solver, not cross-runtime bitwise identity.

Local ignored `artifacts/m3/` retains the input, CLI replay report, native logs and `native-verification.json` metadata. The three experiments used to check save/resample/matrix semantics are retained under ignored `artifacts/viewer/`. None is a source or build dependency. Measured binary SHA-256 values:

| Assembly | SHA-256 |
| --- | --- |
| ParticleLife.MonoGame | `24DD5408CAE9ACB18310E64DE273EE21F1FF9B302A41B8A9867F74E13383734D` |
| ParticleLife.Viewer | `A638459CED8FEF8BACFFAAE2F1B6C467336679765D6CE559E965A4D17B639177` |
| ParticleLife.Core | `D020ECBA0F58630552D8A52C93D0873DE7E7B19BCA07F038A4C74F27EE14D7A1` |
