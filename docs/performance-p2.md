# P2 CPU choices and measured candidate evaluation

P2 retains CPU binary64 simulation and the independent direct reference. Uniform-grid receiver workers are an explicit saved selection from 1 through 64, including the caller; direct requires one. A higher count is not inherently faster. Headless defaults remain serial. The native inspector exposes CPU execution, neighbour search, requested/applied/actual workers, large population choices and exact totals.

The population draft offers 500, 10k, 50k, 100k, 250k and an explicitly labelled 1M stretch choice. **Preserve density: resize world** applies population and world extents together, scaling both extents by `sqrt(new count / old count)` without changing the cutoff. Surviving particles retain absolute positions and velocities; additions use the resized world. This preserves nominal count/area rather than local morphology. Both counts must be nonzero. A shrinking world clamps the wall layer to half its shorter extent if needed, and the resulting values, actual membership and generated particles are one saved undoable event.

## Measurement commands

Build the Release benchmark once, then stop builds, tests, native viewers and other benchmarks for the entire measurement window. Reuse P1's hashed inputs under `artifacts/worktree-evidence/p1/artifacts/benchmarks/p1/`; do not regenerate a different random state for an optimization comparison. Each command writes a fresh result file. An external supervisor must enforce a stated wall deadline on each child, terminate its process tree on expiry and retain an honest timeout record; a timeout is not a latency sample.

```powershell
dotnet restore benchmarks/ParticleLife.Benchmarks/ParticleLife.Benchmarks.csproj --locked-mode
dotnet build benchmarks/ParticleLife.Benchmarks/ParticleLife.Benchmarks.csproj -c Release --no-restore
dotnet run --project benchmarks/ParticleLife.Benchmarks -c Release --no-build -- prepare-p2 artifacts/benchmarks/p2/derived PATH-TO-P1/evolved-tiled-10000.json
dotnet run --project benchmarks/ParticleLife.Benchmarks -c Release --no-build -- measure-p2 artifacts/benchmarks/p2/density10k-w12.json PATH-TO-P1/uniform-density-10000.json 12 evolving 21
dotnet run --project benchmarks/ParticleLife.Benchmarks -c Release --no-build -- profile-p2 artifacts/benchmarks/p2/candidates-evolved.json PATH-TO-P1/evolved-tiled-10000.json 15
```

`measure-p2` runs one resolved worker choice per process. Warmup resets to the identical saved input outside timing and requires at least eight calls and 0.5 seconds, then evolving measurements restart at that same saved input. Complete steps, index build/packing and prepared interaction have distinct timing boundaries. Process-wide precise allocation counters include background workers. Stage medians do not necessarily add to a complete-step median. Static samples reset before every timed step; evolving samples retain each completed state. No rendering or diagnostics cost is included.

`prepare-p2` creates three matched cutoff derivatives immediately below/at/above 0.125 and a captured state after 120 completed grid steps, retaining source path/hash and actual values. `profile-p2` limits probes to 10k particles and cutoff 1e-6..1e6. All candidates use the same saved state. It compares the packed AoS layout, component arrays, ordinary-range reciprocal constants, vector geometry filtering with scalar ordered accumulation, float arithmetic, a float reciprocal-square-root estimate refined once in double, half/quarter cells and selective circle-to-cell stencils. Deliberately approximate probes are benchmark candidates only, not selectable or saved production modes. Direct force/count comparisons report changed accepted membership and maximum/RMS force component errors.

## Results and decisions

The exclusive 5 November 2026 window completed all 32 child processes in 194 seconds, with no timeout or failure. The measured source was `53d687b788a206968213a1eb80f13ae888f15a2f` plus the uncommitted P2 changes recorded in the supervisor. Core assembly SHA-256 was `67B993A85B278C9A95F3BC30216CA7730EE2CC88ABB8339FC5D8943366658466`; benchmark assembly SHA-256 was `E1392703E641F02794365CB79BF65FF48C223250685B21B8CC003947C5EDAE8C`. The machine was Intel Core Ultra 9 275HX, 24 logical processors, Windows build 26300, .NET SDK 10.0.302 and runtime 10.0.10, Balanced power plan, Normal process priority, workstation GC and no recorded runtime overrides. No competing builds, tests, viewers or benchmarks ran. The raw reports are under `artifacts/worktree-evidence/p2-tuning/artifacts/benchmarks/p2/screen-20261105-161058/`; `supervisor.json` records exact arguments, budgets, assembly/input hashes and source dirtiness. `worker-summary.csv` and `profile-summary.csv` summarize the per-child reports.

### Explicit CPU workers

Each ordinary row has 21 samples following at least eight calls and 0.5 seconds of static warmup. The band has nine static samples. Evolving measurements start again at the original saved values after warmup; they advance 21 steps, rather than inheriting different warmup trajectories at each worker count. Entries are nearest-rank **p50 / p95 milliseconds**. These are solver results, excluding diagnostics, presentation and input. One short process per workload/mode/count does not establish a universal optimal count or robust tail estimate.

| Workers | 50k fixed density, static | 50k fixed density, evolving | 10k tiled evolved, evolving | 10k concentrated band, static |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 32.046 / 34.836 | 30.787 / 31.989 | 4.803 / 5.134 | 303.601 / 342.023 |
| 4 | 10.039 / 10.811 | 10.095 / 10.774 | 1.249 / 1.408 | 75.893 / 76.970 |
| 8 | 5.789 / 6.204 | 5.741 / 6.038 | 0.725 / 0.778 | 43.464 / 44.565 |
| 12 | 4.367 / 4.602 | 4.214 / 4.643 | 0.567 / 0.621 | 30.443 / 31.106 |
| 16 | 3.402 / 3.550 | 3.470 / 3.748 | 0.457 / 0.516 | 24.042 / 24.515 |
| 24 | 2.898 / 3.099 | 2.902 / 3.200 | 0.404 / 0.438 | 19.338 / 20.876 |

All 24 step series reported **zero process-wide managed allocation**. The 50k evolving workload retained identical total candidates (72,555,150) and accepted interactions (16,401,128) at every worker count; the tiled workload likewise retained 14,171,450 candidates and 3,543,140 accepted. Worker scheduling does not divide or reorder a receiver's sum. The 50k achieved simulation speeds were 0.224×, 0.688×, 1.193×, 1.632×, 1.974× and 2.324× respectively. The tiled workload ranged from 1.429× with one worker to 16.134× with 24. These rates exclude rendering and do not establish native responsiveness.

Counts beyond eight are worthwhile on this machine and these workloads; retain the explicit 1–64 range and discoverable manual selection. Retain serial defaults for headless operation and existing saved files. Twenty-four is a useful manual starting point on this tested 24-logical-processor machine: it was fastest among the tested counts, including the concentrated band. Smaller populations and other CPUs may favour fewer workers. The benchmark did not measure 32/48/64 workers or establish oversubscription as beneficial.

The serial build/pack stage is 0.732 ms versus 30.250 ms prepared interaction at 50k/one worker. At 24 workers those separate medians are 0.698 ms and 2.056 ms, versus a 2.902 ms complete-step median. Thus the serial build is already approximately one quarter of the 24-worker step, while it is only about 2.4% of serial interaction. It is a meaningful next bottleneck after scaling workers, not evidence that serial index maintenance dominates the original solver. The tiled 24-worker build/interaction medians are 0.090/0.337 ms. Separate stage and complete-step medians need not add.

### Layout and arithmetic probes

The following entries are **benchmark-probe build plus interaction p50 milliseconds**, not speedups of the production evaluator. All probes share their benchmark index/scatter implementation, which differs from the production index. Fifteen static samples follow at least eight warmup calls and 0.5 seconds; band probes use five samples. The 120-step input is an actual completed capture evolved from the original tiled input, with source-clock provenance, not a newly sampled layout. Arithmetic changes are tested on these finite ordinary-scale fixtures; no general extreme-range claim follows.

| Probe | Fixed density 10k | Tiled evolved 10k | Same input after 120 steps | Concentrated band 10k |
| --- | ---: | ---: | ---: | ---: |
| Packed AoS baseline | 8.122 | 6.135 | 6.547 | 369.670 |
| Component SoA | 8.073 | 6.761 | 6.813 | 368.808 |
| Reciprocal constants / scalar ordinary arithmetic | 8.189 | 6.871 | 6.800 | 360.435 |
| Vector geometry filter, ordered scalar force | 7.656 | 5.133 | 5.037 | 324.552 |
| Float force arithmetic, double state/accumulation | 10.770 | 8.420 | 6.716 | 376.925 |
| Float reciprocal-square-root estimate, one double Newton refinement | 11.310 | 10.189 | 7.081 | 474.430 |
| Half-width cells | 13.033 | 7.238 | 7.469 | 268.982 |
| Quarter-width cells | 27.274 | 13.762 | 13.029 | 212.437 |
| Selective circle-to-cell stencil | 9.214 | 5.711 | 6.260 | 333.528 |
| Half-width cells plus selective stencil | 12.865 | 7.243 | 7.196 | 239.754 |

All 70 probe series reported zero timed managed allocation and the same accepted interaction count as the independent double direct reference. For the vector filter, force errors versus direct were exactly the scalar probe's errors: maximum component differences of `1.78e-15`, `2.16e-15`, `2.44e-15` and `7.67e-13` in the four columns. These are accumulation-order differences versus direct; the filter did not change retained force arithmetic or summation order. The float probe changes force arithmetic only, leaving stored state and accumulation double: its maximum force component error was `1.24e-5` on the band and approximately `0.93e-6` to `1.75e-6` on the three ordinary fixtures. The Newton probe's ordinary-case maximum error was approximately `0.79e-13` to `1.73e-13`. Float storage/conversion and long float trajectories were not promoted or claimed validated.

**Decisions:** reject component arrays alone, scalar reciprocal rewrites, float arithmetic and the approximate reciprocal square root for production: none gives a broad improvement here, and float/approximate paths add a distinct precision contract without a measured benefit. The vector filter was the only broadly promising arithmetic/layout probe: totals improved approximately 5.7%, 16.3%, 23.1% and 12.2% in the four columns. Its subsequent production comparison below found material regressions, so it too is rejected. These probe percentages are not shipped performance gains.

### Cell transitions and buffered lists

The three cutoff derivatives hold state/matrix/world fixed while changing only the cutoff to the immediately adjacent doubles around 0.125. Every candidate still compares against direct with that same saved cutoff; differences between rows describe changed workloads, rather than a regression from changing physics.

| Cutoff | Baseline candidates | Baseline / vector p50 ms | Half / quarter cells p50 ms | Selective / half-selective p50 ms |
| --- | ---: | ---: | ---: | ---: |
| Immediately below 0.125 | 675,090 | 7.017 / 5.929 | 8.393 / 13.408 | 7.539 / 9.253 |
| Exactly 0.125 | 675,090 | 7.264 / 6.668 | 10.364 / 19.832 | 9.420 / 13.015 |
| Immediately above 0.125 | 2,622,694 | 17.235 / 9.479 | 16.862 / 18.615 | 13.490 / 11.703 |

The immediately-above case has noisy tails (baseline/vector p95 21.445/27.777 ms); treat its median improvement as exploratory. The accepted count remains 236,760 in every derivative and candidate. The power-of-two transition increases candidate count by about 3.9× despite this unchanged accepted count. Selective and smaller-cell searches mitigate that transition or help a dense strip, but their dictionary lookups/stencil checks cost more on sparse ordinary cases. For example, quarter cells reduce fixed-density candidates from 669,236 to 380,118 yet increase total time from 8.122 to 27.274 ms; band candidates drop from 34,260,892 to 26,066,532 and time drops from 369.670 to 212.437 ms. Retain the present power-of-two nine-cell search globally: no measured alternative is a general replacement, and automatic scene-dependent switching needs its own measured policy rather than an unrecorded heuristic.

Buffered neighbour lists were considered, not implemented or timed. The measured rebuild/pack cost is small in serial cases (approximately 0.11–0.21 ms versus 5–369 ms interaction in these probes), so saving rebuilds alone cannot explain a worthwhile serial improvement. A list could also avoid repeated cell traversal, but requires displacement/skin checks and a measured amortized traversal benefit on moving scenes; the measured band already has 18,863,466 directed accepted pairs, requiring at least 75.5 MB for 32-bit source indices before offsets, skin neighbours or spare capacity. Current exact-cell storage scales with population rather than neighbour count and keeps no motion-validity state. No amortized list benefit has been demonstrated that justifies that additional ownership/memory/invalidation machinery. This is an explicit decision against adding a buffered-list subsystem in P2, not a claim that such lists can never help.

Native interaction acceptance remains pending the consolidated review. The measurements above do not establish GPU performance, million-particle capacity, post-P4 periodic/per-pair-range timing or input latency.

### Matched production vector-filter rejection

The vector probe was implemented in the actual production evaluator and compared with retained pre-candidate binaries in a second exclusive quiet window on the same machine/configuration. Each old/new pair uses the identical saved state, model, worker count, sample mode and warmup protocol. Version order alternates between cases. The 24 child processes completed; reports and exact hashes/arguments are under ignored `artifacts/worktree-evidence/p2-tuning/artifacts/benchmarks/p2/matched-20261105-163453/`, including `supervisor.json`, `summary.csv` and `pair-summary.csv`. The old core/benchmark hashes are the initial hashes above. Candidate core SHA-256 was `93C7AF56FF9B8AA5BF90F2692C254CED91F88910232C28F3E37A1EF2EF9F3794`, benchmark SHA-256 `3EF59CA94E6C675C0CFBA07CE3F3DC987FB3785702DD43A1EC4D55D0A7C01D13`.

The temporary candidate identified itself as `uniform-grid-packed-simd-filter/2`. It added reusable X/Y component arrays, increasing packed payload from 20 to 36 bytes per established particle. Hardware vectors applied a conservative squared-distance filter with a two-ULP outward margin. The unchanged scalar pair kernel retained strict cutoff decisions, force arithmetic and summation order. Scalar fallback covered subnormal squared cutoffs and unavailable hardware vectors. Focused comparisons covered full vector batches/tails, overlaps, cutoff transitions, subnormal geometry and extreme-coordinate direct fallback. No periodic/per-pair-range performance was claimed.

The following are complete production **p50 milliseconds**, not the benchmark probe. Ordinary cases have 21 samples and band cases nine; each version has the same at-least-eight-call/0.5-second warmup. Negative change is faster.

| Workload / mode / workers | Scalar baseline | Vector candidate | Change |
| --- | ---: | ---: | ---: |
| Fixed density 50k / static / 1 | 31.513 | 31.093 | -1.3% |
| Fixed density 50k / evolving / 1 | 32.578 | 32.396 | -0.6% |
| Fixed density 50k / static / 24 | 2.901 | 2.648 | -8.7% |
| Fixed density 50k / evolving / 24 | 2.938 | 2.740 | -6.7% |
| Tiled evolved 10k / static / 1 | 4.296 | 4.076 | -5.1% |
| Tiled evolved 10k / evolving / 1 | 4.308 | 4.457 | +3.5% |
| Tiled evolved 10k / static / 24 | 0.393 | 0.384 | -2.5% |
| Tiled evolved 10k / evolving / 24 | 0.444 | 0.421 | -5.2% |
| Cutoff immediately above 0.125 / static / 1 | 9.868 | 10.465 | +6.1% |
| Cutoff immediately above 0.125 / static / 24 | 0.738 | 0.611 | -17.2% |
| Concentrated band 10k / static / 1 | 307.396 | 358.357 | +16.6% |
| Concentrated band 10k / static / 24 | 19.961 | 20.231 | +1.4% |

Every old/new pair retained identical candidate and accepted counts, and every complete-step series measured zero managed allocation. The band serial regression is substantial in both complete-step p95 (314.586 to 373.898 ms) and separate prepared interaction p50 (285.415 to 364.157 ms). Small parallel gains do not justify that cost for a general replacement. The probe's index/traversal implementation did not predict the production result, which is why promotion required this second measurement.

**Rejected and removed:** restore the exact prior scalar evaluator, its `uniform-grid-packed/1` version and 20-byte packed payload. Retain the benchmark probes and measured evidence, and retain the useful 1–64 worker selection/population controls. No extra scene/worker-dependent SIMD heuristic, reduced-precision option or buffer subsystem is added. Production numerical hot-loop logic is unchanged from the measured scalar baseline, so no further timing run is needed to claim an optimization that did not ship. The larger supported worker choices are the measured CPU capacity improvement in P2.
