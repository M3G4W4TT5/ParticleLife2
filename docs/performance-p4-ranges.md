# Directed-range CPU traversal evaluation

The P4 grid starts from the maximum active directed cutoff. Short receiver rows can therefore inspect many sources that their actual pair radii reject. Two bounded production candidates are evaluated here; neither changes a radius, coefficient, population or physical rule to improve a timing.

Measurements use Release/.NET 10.0.10, SDK 10.0.302, Windows 10.0.26300, an Intel Core Ultra 9 275HX with 24 logical processors, and the Balanced power plan. Solver runs take place in an exclusive quiet window with native viewers, builds and tests stopped. Results are small five-sample screens, not universal capacity guarantees. Saved input hashes, binary hashes, complete samples and allocations remain in the ignored evidence directories below.

## Initial force-only screen

Four exact 4,000-particle/six-species inputs use uniform or clustered positions, with equal cutoffs or one receiver row at 0.2 and five rows at 0.02. The screen includes validation, index rebuild and force evaluation, not integration/rendering. Probes use the same exact pair kernel and are checked against independent direct enumeration. Coarse control and coarse/fine row probes are benchmark-only implementations, not selectable backends.

| Input | Production p50 ms | Coarse packed control | Coarse row probe | Quarter row probe |
| --- | ---: | ---: | ---: | ---: |
| Uniform, equal rows | 53.033 | 47.558 | 44.872 | 31.067 |
| Uniform, heterogeneous rows | 32.804 | 31.263 | 14.997 | 6.933 |
| Clustered, equal rows | 31.779 | 42.083 | 39.493 | 29.277 |
| Clustered, heterogeneous rows | 21.711 | 32.014 | 16.340 | 10.963 |

All 16 probes matched accepted/overlap counts and the direct force tolerance; maximum absolute force difference was `2.132e-13`. Warmed evaluations allocated zero managed bytes. These results justified production comparison, but candidate timings alone did not establish a shipping optimization.

Evidence: `artifacts/worktree-evidence/p4/artifacts/implementation/p4-range-profile/`. The retained pre-trial binary is under `artifacts/worktree-evidence/p4/artifacts/benchmarks/p4/production-baseline`; Core SHA-256 is `8CA6A36F85A306CC528DBD326FF20C8BCE0672A91141C0CFBEA032E082D063E1`.

## Rejected quarter-cell production trial

The first production trial used quarter-width power-of-two cells for nonperiodic pair tables with different receiver-row maxima. Conservative row queries retained every neighbour and used no circular rejection approximation. Global, equal-row and periodic configurations retained their previous path. Complete steps were compared using identical saved inputs, static/reset and evolving samples, workers 1 and representative workers 4, including sparse scenes and cutoff transitions.

| Worker-1 input | Baseline static / evolving p50 ms | Quarter-cell static / evolving p50 ms |
| --- | ---: | ---: |
| Uniform heterogeneous | 30.280 / 31.970 | 8.160 / 6.830 |
| Clustered heterogeneous | 31.890 / 30.790 | 11.100 / 11.200 |
| Sparse uniform heterogeneous | 1.240 / 1.270 | 2.110 / 2.220 |
| Sparse clustered heterogeneous | 2.980 / 2.880 | 3.110 / 3.330 |

All 48 matched children preserved accepted counts and allocated zero warmed bytes; dense worker-4 cases also improved. The 71–75% sparse-uniform regression rejects this candidate as the default. No population/density threshold is added to hide that tradeoff. The finer cell path is removed from production source.

Evidence: `artifacts/worktree-evidence/p4/artifacts/implementation/p4-production-matched-20261105-174108/`. Force-only and complete-step tables have different measurement boundaries and must not be compared as interchangeable timings.

## Retained coarse-row traversal

The retained implementation keeps the original maximum-cutoff cells and their source order. Per receiver it computes an outward-rounded fractional position/radius and selects a contiguous rectangle within the original nine cells. It skips only columns/rows whose sources all fail the actual kernel's component cutoff. There is no per-cell squared-distance test, occupancy threshold, timing adaptation or new execution setting. It applies only to nonperiodic pair tables whose receiver-row maxima differ. Global, equal-row and periodic configurations keep their existing selection. Accepted contribution order is retained; runtime provenance identifies this implementation as `uniform-grid-packed/3`.

The final matched comparison used eleven immutable 4,000-particle/six-species inputs: uniform and clustered heterogeneous scenes; both layouts scaled into an 8 by 8 world for lower density; a sparse case with row maxima differing by only five percent; cutoffs immediately below, at and above 0.125; and global, equal-pair and periodic controls. Heterogeneous rows otherwise use 0.2 for one receiver species and 0.02 for the other five. Coefficients, actual positions, velocities, radii, world dimensions and seeds are identical within each old/new pair. The fixed dt remains `1/144`, force scale 1 and damping half-life 0.025. Sparse fixtures are separate saved workloads, not silent changes to a measured scene.

Each child process performed at least eight warmup calls and at least 0.5 seconds of warmup, then five complete-step samples. Static runs reset outside timing; evolving runs restart at the same saved input after warmup and retain motion between measured steps. Index/interaction probes are measured separately. Tables below report complete simulation steps, excluding reset, serialization and graphics. Worker counts include the caller.

| Worker-1 input | Baseline static / evolving p50 ms | Retained static / evolving p50 ms |
| --- | ---: | ---: |
| Uniform heterogeneous | 30.951 / 31.662 | 13.420 / 14.365 |
| Clustered heterogeneous | 30.021 / 32.389 | 16.273 / 16.333 |
| Sparse uniform heterogeneous | 1.196 / 1.224 | 0.523 / 0.551 |
| Sparse clustered heterogeneous | 2.872 / 2.862 | 1.503 / 1.531 |
| Sparse, mildly heterogeneous | 1.826 / 1.707 | 1.586 / 1.475 |
| Cutoff immediately below 0.125 | 10.727 / 10.467 | 4.198 / 4.340 |
| Cutoff at 0.125 | 10.858 / 10.962 | 4.246 / 4.407 |
| Cutoff immediately above 0.125 | 27.481 / 27.338 | 9.936 / 9.045 |

With four workers, uniform heterogeneous steps changed from `5.965 / 5.976` to `2.994 / 2.989` ms static/evolving; clustered steps changed from `6.181 / 6.308` to `3.345 / 3.317` ms. The dense cases improve approximately 1.85–2.31 times at one worker and 1.85–2.00 times at four workers. The sparse regression of the quarter-cell candidate is absent, including the mildly heterogeneous case.

All 52 children completed. All 26 old/new accepted-count pairs matched, and every five-step series recorded zero managed bytes, including configured force workers. The focused current checks also passed ten row-grid cases, eighteen boundary/range cases, twenty-four packed-grid cases and eighteen editor/tool cases. They cover direct force/count parity, source coverage/multiplicity, subnormal bounds, exact original coarse contribution order, evolution, reconfiguration, worker determinism and warmed allocation behavior. Native and benchmark Release builds pass; graphics-free native controls were verified separately. These checks do not establish native visual/input acceptance.

Controls expose the screen's limits: global p50 moved about 1% faster; equal-pair p50 ranged from 3.4% faster to 1.4% slower; unchanged periodic selection measured 1.1–2.9% slower. Periodic p95 moved 0.3–2.9% higher. Five samples make p95 the largest observed sample, not a reliable tail guarantee; small control changes can reflect run/JIT/power variation or shared implementation overhead. The consistent dense/sparse improvements support retaining the coarse-row path on this machine, not a universal speed or unchanged-control timing promise.

Final evidence: `artifacts/worktree-evidence/p4/artifacts/implementation/p4-coarse-matched-20261105-180710/` contains `supervisor.json`, `summary.csv`, `pair-summary.csv` and all child reports. The quiet window ran on 5 November 2026, 18:07:10–18:08:42 UTC, with the environment above, workstation GC and Normal process priority. Measured source was the P4 working tree based on `d7ababb7aaee606c1278ddbae08066320a2b4bc2`; binary hashes distinguish its uncommitted implementation from the baseline. Later evidence-document edits do not change those measured binaries.

| Measured binary | SHA-256 |
| --- | --- |
| Baseline Core | `8CA6A36F85A306CC528DBD326FF20C8BCE0672A91141C0CFBEA032E082D063E1` |
| Retained Core | `E74AAD63390A7F0FE6BE54D825852AC21610136FE1BD7C7304E17EBC0F1161D0` |
| Baseline benchmark | `314CC5113C1288BD2C5EDAAB98C13AFBD2D340E09780F79B40CC8F92A9B8698A` |
| Retained benchmark | `9D4FAF52EAE17AF0FBD3F8472DAC81A29AFF90AB595289197FBE7D429FC58407` |

Input manifest: `artifacts/worktree-evidence/p4/artifacts/implementation/p4-coarse-inputs/inputs.json`, SHA-256 `19E7EAB523E7523E589F9FE21029F9A7DBD3B733239D29850F0C36FB1891AE80`. Individual input hashes appear in that manifest, the supervisor and each matched report.

## Reproduction

Prepare immutable derivatives in a fresh directory, then run both the saved baseline DLL and current Release DLL through the existing command:

```sh
dotnet run --project benchmarks/ParticleLife.Benchmarks -c Release --no-build -- prepare-p4-production artifacts/implementation/p4-range-profile artifacts/implementation/p4-coarse-inputs
dotnet path/to/ParticleLife.Benchmarks.dll measure-p2 fresh-output.json saved-input.json 1 static 5
dotnet path/to/ParticleLife.Benchmarks.dll measure-p2 fresh-output.json saved-input.json 1 evolving 5
```

Use all eleven prepared inputs with worker 1, then the uniform/clustered heterogeneous inputs with worker 4, for both binaries and both playback modes. Use fresh output names and an exclusive quiet window. Keep the rejected quarter-cell candidate rejected; the retained coarse-row result needs no additional heuristic or backend. Native end-to-end acceptance remains pending the consolidated review.
