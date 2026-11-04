# M4 spatial-search performance

The M4 comparison measures the serial direct and exact uniform-grid backends on identical saved inputs, with unchanged model equations, double precision and damped semi-implicit Euler integration. The [M2/M3 baseline](performance-baseline.md) remains historical context; it is not the denominator of a current speedup.

## Reproduce

Build and verify the pinned Release solution using [CONTRIBUTING.md](../CONTRIBUTING.md). Stop competing benchmarks, builds, tests and viewer processes before measuring. Run from the repository root without a debugger:

```powershell
dotnet run --project benchmarks/ParticleLife.Benchmarks -c Release --no-build -- compare artifacts/benchmarks/m4 31 all 3
$env:PARTICLELIFE_BENCHMARK_INPUTS = (Resolve-Path artifacts/benchmarks/m4).Path
dotnet run --project benchmarks/ParticleLife.Benchmarks -c Release --no-build -- bdn --filter '*uniform-fixed-500*' '*uniform-fixed-10000*' '*uniform-density-10000*' --job short --warmupCount 6 --iterationCount 15 --artifacts BenchmarkDotNet.Artifacts/m4
```

`compare` arguments are output directory, samples per repetition, workload selection and repetition count. Selection accepts `quick`, `all`, a case name or a saved experiment path. Existing input files are loaded without regeneration or rewriting. A fresh directory generates complete inputs with actual coordinates, velocities, species, identities, matrix, parameters and generator metadata; use retained files for controlled comparisons across code/runtime versions. `latency` retains the historical direct-runner interface.

`all` covers 500, 2,000 and 10,000 particles in a fixed unit-square domain, fixed density (side `sqrt(N/500)`), a dense cluster, a narrow band and a boundary/off-world/overlap case. Four small fixed-domain cases (2, 16, 64 and 128 particles) locate search overhead. Seed is 1729; the saved files are authoritative. The default parameters are `R=0.1`, `F0=1`, `beta=0.3`, damping half-life `0.025`, wall width `0.05`, wall strength `1.44`, `dt=1/144` and seven species. Cluster positions span `[0.475,0.525]^2`; band positions span `[0,1] x [0.49,0.51]`; robustness positions span `[-0.1,1.1]^2` with one deliberately duplicated position.

## Measurement boundaries

Both complete paths warm for at least 64 steps per backend and one elapsed second per workload. Each static sample resets to the same saved input outside timing. Three repetitions of 31 samples alternate direct/grid order per sample and repetition. Nearest-rank p50/p95, mean, standard deviation, extrema and every raw sample are retained. These repetitions share one process; they do not independently sample thermal or operating-system conditions.

A complete step includes validation, index rebuild, pair and wall force evaluation, integration, time checks and publication. Separately sampled stages use 15 samples per repetition. Grid construction includes state validation; grid interaction reads that unchanged prepared input and validates output buffers. Direct interaction includes state validation. Integration includes its public validation. Stage medians need not sum to the complete-step median. Internal prepared-index APIs are available only to tests/benchmarks; hosts always call the complete step.

The evolving case uses the saved `uniform-fixed-2000` input, resets only between repetitions, and times 120 consecutive steps per backend. Position comparisons, cell-migration counting and raw-result recording occur outside timed boundaries. Short trajectory differences are reported separately from initial-state local force/next-state differences; chaotic long-term divergence is not interpreted as a local search error.

Allocation deltas use `GC.GetAllocatedBytesForCurrentThread`; the solver is serial. Construction, first index establishment and growth from a quarter population are single separately labelled measurements. Index capacity telemetry reports retained arrays and dictionary slots; estimated payload bytes exclude object headers, padding and GC/process overhead. Capacity is retained on shrink. Clearing dictionary storage costs depend on that retained capacity.

Diagnostics run separately after numerical timings, with one warm-up and five samples. Their p95 is therefore only the largest of five observations. They remain quadratic and allocate. No rendering is included. Solver latency alone cannot establish viewer FPS or input responsiveness.

BenchmarkDotNet uses the same files and both backend parameters, with reset outside each timed invocation. Iteration setup forces one invocation per iteration, so small workloads can fall below its recommended iteration duration. Its memory diagnoser is an independent allocation check; wide confidence intervals are reported without claiming a tight timing budget. No timing percentage is enforced in CI.

## Reference machine and recorded build

Measured on 2026-11-04 at clean commit `ea674417e7ec1bcc20142ad21750bf9179c10717`, after the full verification suite and with no competing build, test, benchmark or viewer. The final documentation commit does not change the measured executable logic.

- Release, SDK **10.0.302**, .NET **10.0.10**, x64 RyuJIT, BenchmarkDotNet **0.15.8**.
- Windows 11 **10.0.26300.9711**, Intel Core Ultra 9 **275HX**, 24 logical processors; **one simulation thread**, workstation GC.
- Paired runner: Balanced power plan (`381b4222-f694-41f0-9685-ff5bb260df2e`), Normal priority, 10 MHz stopwatch. Captured DOTNET/COMPlus tiering, PGO, ReadyToRun and GC overrides were unset. Thermal state and ordinary OS background load were not controlled.
- Core assembly SHA-256: `7FDF23A9B9833B88A9E66F03C2C2E7E03CB59609DB028C441CDF6A30151CD273`.
- Benchmark assembly SHA-256: `533A612A0CC591732B357EB30E87FF494CC87111E348959684CE707D2DD2F0CC`.

## Complete-step latency

Each cell is **p50 / p95 in milliseconds**, pooled from 93 steps per backend. A direct/grid ratio greater than one favours the grid. No fallback occurred in these ordinary-scale workloads; actual execution was grid in every grid sample. Boundary cases include negative/off-world positions and one overlap.

| Saved case | Direct p50 / p95 (ms) | Grid p50 / p95 (ms) | p50 direct/grid |
| --- | ---: | ---: | ---: |
| uniform-fixed-2 | 0.0000 / 0.0001 | 0.0001 / 0.0002 | unresolved |
| uniform-fixed-16 | 0.0005 / 0.0005 | 0.0009 / 0.0009 | 0.556x |
| uniform-fixed-64 | 0.0061 / 0.0075 | 0.0057 / 0.0068 | 1.070x |
| uniform-fixed-128 | 0.0262 / 0.0379 | 0.0202 / 0.0263 | 1.297x |
| uniform-fixed-500 | 0.8002 / 0.9548 | 0.3798 / 0.4082 | 2.107x |
| uniform-density-500 | 0.7987 / 0.8927 | 0.3876 / 0.4198 | 2.061x |
| cluster-500 | 3.2209 / 3.3505 | 4.4448 / 5.0921 | 0.725x |
| band-500 | 1.1954 / 1.3161 | 1.1915 / 1.3220 | 1.003x |
| boundary-overlap-500 | 0.6936 / 0.7842 | 0.3076 / 0.3396 | 2.255x |
| uniform-fixed-2000 | 13.9092 / 14.5066 | 5.4759 / 5.7777 | 2.540x |
| uniform-density-2000 | 8.6789 / 8.9255 | 1.7227 / 1.8434 | 5.038x |
| cluster-2000 | 51.7860 / 53.6174 | 71.2528 / 73.1685 | 0.727x |
| band-2000 | 21.0785 / 22.5890 | 19.5101 / 20.2844 | 1.080x |
| boundary-overlap-2000 | 11.9066 / 13.1835 | 3.9660 / 4.2291 | 3.002x |
| uniform-fixed-10000 | 344.3305 / 357.8437 | 133.5622 / 142.8202 | 2.578x |
| uniform-density-10000 | 156.6643 / 159.5297 | 9.2011 / 9.8970 | 17.027x |
| cluster-10000 | 1327.7365 / 1510.0500 | 1808.5642 / 1895.8005 | 0.734x |
| band-10000 | 542.6277 / 557.7700 | 508.8945 / 518.2930 | 1.066x |
| boundary-overlap-10000 | 306.4928 / 315.9897 | 94.0830 / 97.5641 | 3.258x |

The two-particle median is below the timer resolution; its raw zero is not a zero-cost step or a usable speedup. At 16 particles grid overhead dominates, and 64 particles is close enough that these observations do not establish a universal crossover. Fixed-density uniform work benefits most: the 10,000-particle grid inspects 669,236 candidates instead of 99,990,000. Increasing density in the fixed domain still grows local work quadratically. In the dense cluster every pair is relevant and grid traversal is 36–38% slower; direct remains selectable for that case. Bands show little benefit.

Variability is retained, not trimmed, in the paired runner. For fixed-domain 2,000, the three grid medians were 5.4738, 5.4697 and 5.4852 ms, but individual repetition p95s were 5.6136, 6.8484 and 6.2615 ms. Fixed-density 10,000 repetition p95s were 9.8970, 10.1833 and 9.4817 ms. Pooled p95s are neither hard maxima nor independent-run confidence bounds.

The evolving 2,000-particle case measured **13.9763 / 15.1163 ms direct** and **5.4770 / 6.3579 ms grid**, a 2.552x median ratio across 360 steps each. It crossed 3,666 particle/cell boundaries across three reset-and-replay runs, occupied 64–90 cells and inspected 461,234–474,642 candidates per step. All repeated steps allocated zero bytes. Maximum position and velocity vector differences during the short trajectories were `1.065e-14` and `1.228e-13` in reduced units. This is local/short-horizon evidence; long chaotic trajectories need not remain close.

### Stage costs and diagnostic overhead

These are separately sampled **p50 milliseconds** (45 samples per numerical stage); raw reports also contain p95, dispersion and every sample. Interaction includes soft walls. Integration is shown for both force results. Diagnostics have only five samples and are outside step timing.

| Case | Direct interaction | Grid build | Grid interaction | Integration direct / grid | Diagnostics |
| --- | ---: | ---: | ---: | ---: | ---: |
| uniform-fixed-500 | 0.8145 | 0.0049 | 0.3746 | 0.0010 / 0.0009 | 1.1195 |
| uniform-density-500 | 0.8137 | 0.0048 | 0.3820 | 0.0009 / 0.0009 | 1.2278 |
| cluster-500 | 3.2271 | 0.0044 | 4.4281 | 0.0010 / 0.0011 | 1.1929 |
| band-500 | 1.2149 | 0.0044 | 1.2139 | 0.0009 / 0.0009 | 0.7159 |
| boundary-overlap-500 | 0.7095 | 0.0061 | 0.3008 | 0.0010 / 0.0009 | 1.1754 |
| uniform-fixed-2000 | 13.9602 | 0.0182 | 5.4558 | 0.0050 / 0.0045 | 18.1982 |
| uniform-density-2000 | 8.6543 | 0.0207 | 1.6656 | 0.0046 / 0.0038 | 17.6762 |
| cluster-2000 | 53.0804 | 0.0183 | 71.5093 | 0.0052 / 0.0051 | 19.1464 |
| band-2000 | 21.3167 | 0.0176 | 19.4204 | 0.0044 / 0.0044 | 11.8984 |
| boundary-overlap-2000 | 11.9354 | 0.0261 | 3.9546 | 0.0039 / 0.0037 | 18.1567 |
| uniform-fixed-10000 | 344.9281 | 0.0846 | 130.3054 | 0.0283 / 0.0289 | 455.9292 |
| uniform-density-10000 | 156.3276 | 0.1080 | 8.8654 | 0.0282 / 0.0204 | 437.7181 |
| cluster-10000 | 1345.8697 | 0.0828 | 1818.7666 | 0.0286 / 0.0298 | 475.7627 |
| band-10000 | 550.4316 | 0.0875 | 500.1287 | 0.0292 / 0.0288 | 314.6865 |
| boundary-overlap-10000 | 308.6817 | 0.1174 | 94.6757 | 0.0302 / 0.0283 | 460.3245 |

Pair traversal dominates these costs; index construction is small even at 10,000 particles. This supports delivering the measured serial implementation without adding parallel scheduling in M4. At fixed-density 10,000, diagnostics alone take **437.7181 / 438.3485 ms p50/p95** and allocate **80,128 bytes per sample**, versus a 9.2011 ms grid step. Fixed-domain 2,000 diagnostics take 18.1982 / 21.2067 ms. Even an infrequent synchronous sample can delay input; solver improvement does not establish an interactive frame rate. The existing 144-completed-step cadence and toggle remain unchanged.

### Reference-machine capacity targets

Use these rounded **core-step p95 budgets** for the named saved workloads on this machine/build, with diagnostics and rendering excluded:

| Workload | Particle count | Grid step budget | Observed evidence |
| --- | ---: | ---: | --- |
| Uniform fixed/domain-density or boundary/overlap | 500 | 0.6 ms | Largest pooled p95 0.4198 ms |
| Fixed-domain uniform, including the 120-step evolution | 2,000 | 7 ms | Pooled p95 5.7777 / 6.3579 ms; static repetition p95 up to 6.8484 ms |
| Fixed-density uniform | 10,000 | 12 ms | Pooled p95 9.8970 ms; repetition p95 up to 10.1833 ms |

These are measured planning targets, not CI assertions or guarantees for arbitrary emergent states. The 10,000-particle fixed-density target exceeds the 6.944 ms wall-time budget needed for real-time `dt=1/144` playback. Fixed-domain 10,000 needs about 143 ms at p95; the dense cluster needs about 1.90 seconds. Bands and dense clusters do not inherit the uniform budgets. High density, changing distributions, diagnostics and rendering can all reduce responsiveness.

## Correctness and allocations

The full suite passes **243 tests**, retaining the original mathematical regressions. New deterministic tests independently enumerate accepted neighbour sets and multiplicity, force hash collisions, and cover faces/corners, strict cutoff boundaries, off-world/negative states, asymmetric matrices, dense cells, overlaps, binary scale extremes and subnormal separation. Movement, stable-identity reorder, growth/shrink/empty reset and experiment replacement are exercised. Failed force or integration steps retain the prior published state, statistics and execution metadata.

Set membership, multiplicity and accepted/overlap counts are exact assertions. For ordinary reduced-unit force sums over at most 513 particles and 120-step order-one state comparisons, tests use `1e-12` absolute plus `1e-12` relative tolerances to admit accumulation-order roundoff. Independent analytic extreme/subnormal tests retain their scale-specific checks; tiny expected forces are not accepted solely by a loose ordinary-scale tolerance. Fourteen actual CLI/viewer-controller parity cases cover seven scenarios through both backends, with legacy schema-1 meaning, saved selection and explicit override tests.

The table reports maximum vector-norm differences against direct enumeration in reduced units. Direct always inspects `N(N-1)` non-self candidates. Both backends agree exactly on accepted and overlap counts; each boundary input has one unordered overlap, other inputs have none. Counts refer to geometric acceptance even for zero coefficients. Relative error divides each difference norm by the larger of the two force norms (zero if both vanish). Relative force error across all cases is at most `5.350e-13`; absolute error grows with the number and magnitude of terms, as the dense case shows.

| Case | Grid candidates | Accepted, both | Max force error | Max next position / velocity error |
| --- | ---: | ---: | ---: | ---: |
| uniform-fixed-500 | 28,844 | 7,038 | 1.132e-15 | 0.000e+00 / 7.152e-18 |
| uniform-density-500 | 28,844 | 7,038 | 1.132e-15 | 0.000e+00 / 7.152e-18 |
| cluster-500 | 249,500 | 249,500 | 1.123e-13 | 1.110e-16 / 7.550e-16 |
| band-500 | 83,930 | 46,844 | 8.793e-15 | 0.000e+00 / 6.397e-17 |
| boundary-overlap-500 | 20,224 | 5,030 | 7.022e-16 | 0.000e+00 / 4.091e-18 |
| uniform-fixed-2000 | 473,942 | 115,874 | 5.330e-15 | 6.939e-18 / 3.471e-17 |
| uniform-density-2000 | 130,298 | 29,816 | 1.132e-15 | 0.000e+00 / 8.183e-18 |
| cluster-2000 | 3,998,000 | 3,998,000 | 1.104e-12 | 1.570e-16 / 7.708e-15 |
| band-2000 | 1,377,140 | 760,504 | 7.118e-14 | 1.110e-16 / 4.728e-16 |
| boundary-overlap-2000 | 333,088 | 81,496 | 3.878e-15 | 1.110e-16 / 2.678e-17 |
| uniform-fixed-10000 | 11,778,340 | 2,863,176 | 3.286e-14 | 1.110e-16 / 2.290e-16 |
| uniform-density-10000 | 669,236 | 152,378 | 1.780e-15 | 0.000e+00 / 1.214e-17 |
| cluster-10000 | 99,990,000 | 99,990,000 | 1.844e-11 | 8.899e-16 / 1.281e-13 |
| band-10000 | 34,260,892 | 18,863,466 | 7.721e-13 | 1.241e-16 / 5.362e-15 |
| boundary-overlap-10000 | 8,238,452 | 2,015,948 | 2.114e-14 | 2.220e-16 / 1.460e-16 |

All 19 paired workloads report **zero managed allocations** for warmed complete steps, index construction, interaction and integration. Tests also spread particles from one cell into more than 32 cells over repeated complete steps, cover coordinate-range fallback correctness and verify zero allocations for warmed accumulation-range retries. The coordinate guard falls back to direct when reliable indexing is unavailable; non-finite grid-order sums retry direct cancellation order. Failed direct retries remain atomic. These fallback checks are separate from the ordinary-scale performance tables; fallback latency is not promised to improve on direct. Later integration overflow is not retried, and finite accumulation roundoff remains possible.

### Index memory and growth

All measured cutoffs use cell width 0.125. Slots reserve for particle capacity, not current occupancy, so a cluster can spread without allocating. Capacities are retained on shrink; dictionary clearing then scales with retained capacity. Payload estimates use `20 * particleCapacity + 36 * cellCapacity` bytes for the current .NET x64 layout and exclude headers/padding; they are not total simulation or process memory.

| Particles | Dictionary slots | Occupied cells: fixed / density / cluster / band / boundary | Estimated retained index bytes |
| ---: | ---: | --- | ---: |
| 500 | 521 | 64 / 64 / 4 / 16 / 100 | 28,756 |
| 2,000 | 2,333 | 64 / 256 / 4 / 16 / 100 | 123,988 |
| 10,000 | 10,103 | 64 / 1,296 / 4 / 16 / 100 | 563,708 |

For fixed-density 10,000, the separately observed setup/growth costs below are **single measurements after shared code warm-up**, not percentiles. Construction owns simulation state buffers; the first grid step establishes index capacity. Growth from 2,500 starts with 2,801 slots and 150,836 estimated index bytes, reaching the 10,000 row above. Full reports retain setup/growth measurements for every workload.

| Operation | Direct ms / allocated bytes | Grid ms / allocated bytes |
| --- | ---: | ---: |
| Construct simulation | 0.1464 / 920,480 | 0.1496 / 920,776 |
| First complete step | 155.7326 / 0 | 9.2362 / 563,808 |
| Reset from 2,500 to 10,000 | 0.1609 / 920,168 | 0.1480 / 920,168 |
| First complete step after growth | 157.1020 / 0 | 9.4203 / 563,808 |

## BenchmarkDotNet confirmation

All six saved-input cases completed in separate benchmark processes. BenchmarkDotNet changed the power plan to **High performance** and restored Balanced afterward. Its process/JIT warm-up history and plan differ from the paired runner, so these means are a separate confirmation, not values to mix into the paired latency ratios or budgets. MemoryDiagnoser reports **zero bytes per operation** in every case.

| Case | Backend | Mean (ms) | 99.9% CI half-width (ms) | Standard deviation (ms) |
| --- | --- | ---: | ---: | ---: |
| uniform-fixed-500 | Direct | 0.8473 | 0.03131 | 0.02928 |
| uniform-fixed-500 | Grid | 0.2619 | 0.00546 | 0.00484 |
| uniform-fixed-10000 | Direct | 353.1198 | 17.22425 | 16.11157 |
| uniform-fixed-10000 | Grid | 109.3462 | 0.85195 | 0.71142 |
| uniform-density-10000 | Direct | 178.1572 | 9.86697 | 9.22957 |
| uniform-density-10000 | Grid | 7.1936 | 0.14396 | 0.12761 |

BenchmarkDotNet warned that the two 500-particle cases and grid fixed-density 10,000 had iterations below its recommended 100 ms duration. One timed invocation preserves identical single-step inputs and excludes reset cost. It removed one high outlier for grid density, two for grid fixed-domain 10,000 and one for grid 500; raw logs retain them. The paired runner removes no observations. Differing harness outcomes and confidence intervals limit claims of a universal speedup.

## Host and native verification

Locked restore, formatting, Release solution build, all 243 tests, display-free viewer help/MonoGame compatibility, XML warning probes and all 52 CLI smoke commands pass. The optimized backend is selected through actual CLI and viewer workflows, not just an evaluator microbenchmark. Core/generator APIs retain direct defaults; newly generated host experiments choose grid; schema-1 files retain direct meaning. Schema 2 separates model, integrator and neighbour-search versions, and run reports retain saved/overridden/actual choices and cumulative fallback reasons.

The Windows DesktopGL viewer was exercised on the same build with an NVIDIA GeForce RTX 5080 Laptop GPU: default 500 grid, legacy direct fixture, explicit grid override and a native-saved grid file. Requested/actual search was observed after stepping. Reset, resample, matrix edit, all presets, distinct saves, valid/invalid reload, scheduled diagnostics and wide/tall geometry were checked. Saved values won over `--species 50000`; the saved grid input replayed through the CLI for 120 steps with no fallback. All four launches exited successfully. The native record describes each observed check and saved-file hashes.

Limitations: Linux/macOS native graphics were not tested. Only Right-arrow was successfully exercised as a keyboard shortcut; other commands used clickable controls. The minimum 420x340 layout was not separately rechecked. The native session is not a controlled FPS benchmark. Display-free scheduling tests cover pause/focus/catch-up contracts; the recorded native checks should not be read as a new exhaustive keyboard/focus audit.

## Retained evidence and input manifest

Generated evidence is retained locally and ignored by Git; it is not a build/test dependency:

- `artifacts/benchmarks/m4/comparison.json`: environment, all 19 cases, full samples and evolving run; `*.experiment.comparison.json` contains individual cases.
- `artifacts/benchmarks/m4/*.experiment.json`: exact inputs listed below. Thirteen historical inputs were reused byte-for-byte, six were generated for small/extra boundary cases. Timing denominators are the rerun direct path, never historical M2 numbers.
- `artifacts/benchmarks/m4/console.log`, `bdn-console.log`, `session.json` and `summarize.py`: logs, BDN launch environment and independent input-hash/percentile/count/allocation validation. `comparison.json` retains its own paired-run environment; `session.json` describes the later BDN launch.
- `BenchmarkDotNet.Artifacts/m4/`: full process logs and CSV/HTML/Markdown reports.
- `artifacts/m4/native-verification.json`, native logs and viewer/override saves; `artifacts/m4/native-saved-grid-replay.json` contains the actual CLI replay. The principal saved grid input hash is `DD23750A65C29A900CADCFF5EC4C6B177EB1B8C54CCD772966CD09D93CBC89E3`.
- `artifacts/m4/final-verification.json` and `final-*.log`: exit codes and logs from the final eight documented verification commands.

Each case below names `<case>.experiment.json` under the retained M4 directory. These hashes identify complete input bytes, including parameters, seed metadata and actual initial conditions. Regenerating from a seed on another runtime is not a substitute for these files.

| Case | Input SHA-256 |
| --- | --- |
| uniform-fixed-2 | `1B8F690EECCECD94FB5CBABC08EFD60636860A048ED5ECA6531A352F8AEDCC10` |
| uniform-fixed-16 | `15770BF9C6D07C2FD8EC7F70202DDEBA80E3335FA79CBA7238D594F69E163AB5` |
| uniform-fixed-64 | `512E21D2209EFFA65077A482001376FB63AE8A4BD39CB3DACE08B5D73A854B44` |
| uniform-fixed-128 | `9AAA2297B3C63C7609A6CCDC2F10E82D983280921008E86CA9CF78DF4062AD80` |
| uniform-fixed-500 | `DAF94FE6932B7D100DD9D60FFB8A78C357B284B461F60EE8DC33BDF85226EF36` |
| uniform-density-500 | `138E906CF60B56E52D582D6D3B44747EFEBC5EE63302E821A7144A530EE63EAC` |
| cluster-500 | `42476B135E48C19CEA97CD1C5787A1D35A7CDBF933D2EE5AFADE4578103E9523` |
| band-500 | `27970013401BDDE1C703F38B7F25C09F1C098852576CE8AF2A6AE6BAEFB51778` |
| boundary-overlap-500 | `185B5E6ACB6E293A71A8A94C756C03EE86763A993160F05EBCB9383B6582B1B0` |
| uniform-fixed-2000 | `69EC581FAE90B2D3808653CD826A52465276576580CD8743A7CB42E023B59FC0` |
| uniform-density-2000 | `B088D368593FEC8CAFA234276C7891DEB7B7DB97E695711C5759E7474CFF1E7C` |
| cluster-2000 | `F3E62D59A2AE5DE6ACDEC4F6B604DBA6A513038A76B9A88C5FFC059C17CF1F36` |
| band-2000 | `DCC964B2B376132C903BA94EF89B8605C86EF53895418F7345D876A5DC07B423` |
| boundary-overlap-2000 | `94E59CD03F83F43580291A3EF2231B385CCA5F9BA2AEFFF453FC135B20EE7472` |
| uniform-fixed-10000 | `CC5D789B40C7A687F2DE723377F25079105E939B4071B5EAE08558E87461A4E1` |
| uniform-density-10000 | `EBD89F3413543AD1C9E467F6A25E7452FBCE90F3FD541E0BDD540D5FE73319D0` |
| cluster-10000 | `2D416AB8C163FED20DA5540347CAB9F75746BE560A9D20CF7A5924E4B14D9A9D` |
| band-10000 | `0679C7F9E5C6D2CEDF20B9D6B6002A40343D5FB4FD4C29AFC08C3B6A63EC633D` |
| boundary-overlap-10000 | `D840AA758247C2FA61C1CFCEEA6284A1497DFD4D6038B6EC24C8932D5EE9A4EB` |
