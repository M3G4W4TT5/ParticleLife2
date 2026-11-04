# M4.1 CPU traversal and exact diagnostics

M4.1 preserves the reviewed model and adds contiguous grid traversal, explicit CPU workers and reusable spatial diagnostics. The accepted [M4 measurements](performance-m4.md) remain historical evidence. Current direct comparisons and a reconstructed accepted-M4 executable provide separate denominators; no particle count alone guarantees interactive performance.

## Implementation and numerical contracts

The grid packs positions and species into contiguous cell ranges. Its nine-cell order and ascending source order within each cell are unchanged; outputs retain original particle order and stable identities. The direct evaluator remains an independent nested enumeration. Both use the unchanged double-precision pair kernel, directed coefficients, soft walls, overlap policy and fixed-step integration.

One worker is the default serial path. Explicit counts 2–8 use persistent sleeping workers plus the caller, dynamically claiming batches of 32 complete receivers. Each receiver owns one output and one sequential sum. All workers finish before integration, fallback or exception propagation; failed steps publish neither partial state nor new statistics/metadata. Warm steps allocate no managed memory on the caller or workers. Dispose simulations/controllers between steps to release their worker resources.

Schema 1 remains direct/serial; older schema-2 files remain serial. Schema 2 now writes explicit `workerCount`. Saved `uniform-grid/1` still means the same exact search/order convention, while reports separately identify the implementation as `uniform-grid-packed/1`. CLI/viewer `--workers` is explicit, retained through editing/save/reload and never selected from timings. Direct requires one worker. A coordinate or accumulation fallback reports actual direct execution with one worker.

`Diagnostics.Sample` remains the straightforward reference. `DiagnosticSampler` owns reusable component, coincidence and spatial buffers and builds from the completed state being measured. It never reuses the preceding force index. Exact coincidence counts precede connectivity work; redundant edges within an already connected component can be skipped, and connectivity can stop when one component remains. Speeds are a separate linear pass. The final edge predicate remains `double.Hypot(dx,dy) <= radius`, including radii whose square underflows/overflows. A strictly larger binary cell width covers inclusive rounded-boundary cases; unindexable finite states or widths use an exact direct fallback. At established capacity, sampling allocates one 80-byte immutable result on this runtime, independently of population. Worst-case connectivity remains quadratic.

The locality rationale is consistent with [Páll and Hess, §2.1](https://arxiv.org/html/1306.1737v1); this scalar implementation does not adopt their clustered SIMD kernel. Receiver scheduling addresses uneven work discussed by [Meyer, §II](https://arxiv.org/html/1305.4196v3), with disjoint directed receivers replacing cell-pair dependencies. Neither paper's published speedups predict this model's performance. No selective stencil, SIMD, pair-symmetry shortcut, approximation, reduced precision or geometry subsystem was added. Buffered Verlet lists remain a later experiment.

## Saved workloads and observed geometry

Preparation retains the 19 M4 files byte-for-byte: tiny populations, fixed-domain/fixed-density populations, dense clusters, bands and boundary/off-world/coincidence cases. Their parameters remain those in the M4 report.

The cutoff sweep uses the **same fixed-density 10,000-particle state**, matrix, world, damping, force scale, walls and timestep. Only `R` changes to the adjacent representable double below, exactly at and immediately above `0.0625` and `0.125`. It tests cell-width transitions separately from population or density changes.

Three complete originating experiments use Random, Chains and Snakes, each with 500 particles, seven species, seed 1729, the actual saved matrix/state and default model quantities. Serial packed-grid evolution saves checkpoints after 3,600, 14,400 and 28,800 steps: approximately 25, 100 and 200 reduced time units. The manifest records accumulated time, exact input/output file hashes, backend, fallback counts, runtime and implementation. Snapshot experiments rebase their clock to zero; the manifest retains the original clock. Generation is outside all timed work.

Visual inspection of all twelve origin/checkpoint plots found segmented curved chains in Chains; concentrated groups alongside diffuse particles and finite off-world groups in Random; and compact groups plus a rosette-like arrangement in Snakes at time 200. These are observed structures, not claims of maturity, stationarity or physical validation. The geometries continue changing between checkpoints.

| Saved preset | Occupied 20×20 bins, origin → time 200 | Bin-population coefficient of variation, origin → time 200 | Components / largest at radius 0.03, time 200 |
| --- | ---: | ---: | ---: |
| Random | 277 → 46 | 0.922 → 6.017 | 29 / 182 |
| Chains | 278 → 141 | 0.875 → 2.070 | 101 / 25 |
| Snakes | 278 → 34 | 0.875 → 5.577 | 6 / 380 |

Bin statistics describe the nominal world and separately record off-world particles. Component counts are radius-dependent geometric quantities, not an automatic morphology classifier. Three matrices at one seed and 500 particles bound generation cost; they do not characterize all emergent regimes or demonstrate large-population equilibrium. The original synthetic dense/band cases remain useful stress inputs.

## Measurement protocol

All numerical measurements use identical saved values and Release/.NET 10.0.10 with SDK 10.0.302 on the Intel Core Ultra 9 275HX (24 logical processors), Windows 11 build 26300, workstation GC, Normal priority and Balanced power plan. Runtime tiering/PGO/GC overrides are unset. No competing benchmark, test, build or viewer runs during timing. Thermal conditions and ordinary operating-system background activity are not controlled.

The measured implementation source is commit `041975b7cc7e9b5af6792f25d8a039b24df8bb27`; later documentation does not change its numerical logic. Generation used an earlier working tree recorded at `e705c196c01c460f7119d324ae37a1329077d414`, with a different assembly hash; its actual saved output values remain authoritative. The accepted source is `e2d8a272582602206a6aa38d5b854eaf33dca86a`, archived under ignored artifacts with its core unchanged. Its comparison runner's minimum warm-up is reduced to eight calls and 0.5 seconds; a standalone candidate/geometry profiling command is added. All changes to that isolated harness are reproducible in `scripts/prepare-m4-baseline.ps1`. Reports retain assembly hashes and actual checkout dirtiness; documentation edits were present during measurement. The baseline manifest identifies the archived accepted source: an environment `commit` read from the enclosing checkout is not the archived source identity. All archived core C# files were checked against the accepted archive.

Each static sample resets outside timing. Complete steps include validation, index/packing, force evaluation, integration and publication. Each new backend's complete and stage paths warm independently for at least eight calls and 0.5 seconds. Two within-process repetitions of 15 samples rotate backend order. Run 2 covers all 34 selected cases; Run 1 independently repeats nine representative cases. Three further processes measure the time-200 preset continuations. The first accepted-M4 invocation covers all 19 static cases; 15 later separate invocations repeat six static cases and measure three time-200 states plus six cutoff cases. Each accepted-M4 static comparison uses 30 samples. Within-process repetitions and independent invocations are not conflated. Nearest-rank p50/p95 retain every observation without trimming. No timing threshold is enforced in CI.

Build, prepared interaction and integration are separately sampled; their medians need not sum to the step median. Candidate-only and ordinary-scale geometric-filter probes warm for at least 32 calls and 0.5 seconds each; they consume checksums and validate acceptance against direct enumeration. These probes are not additive attribution of a full kernel. Their original-index geometry probe also does not substitute for the production packed-position loop.

Allocations use `GC.GetTotalAllocatedBytes(true)` outside timed boundaries, covering the entire process and all workers. Prepared-force samples additionally record caller plus background callback counters. Setup/growth observations are single measurements after shared code warm-up, not percentiles. Retained index payload estimates exclude object headers, thread stacks/events, simulation buffers and process overhead. Diagnostics warm and run independently of solver timing; reference/spatial results are checked for exact equality at every sample.

The warm-up protocols are similar lower bounds, not identical histories: the archived runner warms direct and grid together for eight calls and 0.5 seconds combined, then establishes stage capacity once; the new runner warms each complete and stage path independently for eight calls and 0.5 seconds. The all-case order also differs, and later baseline invocations start a fresh process per case. Tiering, shared-kernel profiles and machine state can therefore influence differences. Both baseline observations are retained below rather than selecting the slower one.

| Build purpose | Core assembly SHA-256 |
| --- | --- |
| Long-state generation | `FE7477E1F5EEEEED4D4C8D36C4E75632B6D726ACD072E1DB6CCF96E120294D38` |
| Current five measurement processes | `E415D462080DE8506EEEF7B479774B430D983D59795B03DB5D2BFD6C7A7C9032` |
| Accepted-M4 initial invocation | `42DE341F50EF9EA68F95CE0581F80C670DE64EEF21D18104E95B571C562A6F8C` |
| Accepted-M4 later invocations | `E5D383A3B43053D5F4B7ACE848357749DF8ADEEAD89AD340E91954A051D3C734` |

The two archived builds retain identical accepted core source; assembly metadata/build context differs. Full benchmark-assembly hashes, SDK/runtime information and power-plan identifiers are in each raw report.

## Complete-step results

Each cell is **p50 / p95 milliseconds**, with 30 static samples per backend in Run 2. These include rebuilding and publication, and exclude diagnostics/rendering. All measured ordinary-scale requests used their configured backend without fallback. The old-grid column uses the initial archived run for static cases and the separate extended invocations for the three evolved states. Independently repeated old/current observations follow immediately below; the first old run is not the sole comparison denominator. The complete 34-case current results remain in the raw reports.

| Saved case | Old grid observation | Current direct | Packed serial | 2 workers | 4 workers | 8 workers |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| uniform-fixed-500 | 0.3841 / 0.4064 | 0.7758 / 0.8393 | 0.2576 / 0.2672 | 0.1394 / 0.1776 | 0.1030 / 0.1309 | 0.0871 / 0.0995 |
| uniform-fixed-2000 | 5.3669 / 6.2546 | 13.0505 / 13.7916 | 3.1329 / 3.2851 | 1.6450 / 1.7559 | 0.8700 / 0.9755 | 0.5160 / 0.5935 |
| uniform-density-10000 | 9.2250 / 9.8301 | 151.4034 / 156.8683 | 5.4262 / 5.6653 | 2.9738 / 3.6815 | 1.6051 / 1.7858 | 1.0485 / 1.2350 |
| uniform-fixed-10000 | 134.2189 / 137.5128 | 310.4015 / 317.7400 | 68.2850 / 69.6474 | 36.3356 / 36.9900 | 18.4980 / 19.3722 | 10.3058 / 11.2144 |
| cluster-2000 | 69.1228 / 72.4275 | 37.3506 / 41.6252 | 26.6763 / 29.2164 | 13.9584 / 15.2009 | 7.1458 / 8.0217 | 4.1220 / 4.5428 |
| cluster-10000 | 1683.2698 / 1756.8941 | 999.7500 / 1013.8408 | 728.4872 / 735.5767 | 366.7462 / 378.6316 | 183.1195 / 189.2987 | 97.5502 / 100.6418 |
| band-2000 | 19.3427 / 21.8921 | 18.4259 / 18.9108 | 9.8127 / 10.1464 | 5.1849 / 5.4955 | 2.7694 / 3.4137 | 1.6382 / 2.2056 |
| band-10000 | 474.2321 / 484.2155 | 494.3428 / 515.1217 | 273.6974 / 290.4778 | 142.6279 / 149.3877 | 73.0894 / 74.8225 | 39.6847 / 41.4277 |
| boundary-overlap-10000 | 89.7479 / 93.3044 | 280.2506 / 290.6736 | 49.7664 / 51.3282 | 25.9026 / 26.6870 | 13.2424 / 15.1224 | 7.6439 / 8.3247 |
| evolved-chains-500-t200 | 0.2570 / 0.2850 | 0.8139 / 0.9240 | 0.2037 / 0.2135 | 0.1189 / 0.1493 | 0.0789 / 0.1143 | 0.0885 / 0.1304 |
| evolved-random-500-t200 | 0.8235 / 0.9544 | 1.4976 / 1.5757 | 0.7208 / 0.7394 | 0.4005 / 0.4228 | 0.2318 / 0.2888 | 0.1783 / 0.2463 |
| evolved-snakes-500-t200 | 1.4947 / 1.6175 | 2.1838 / 2.3116 | 1.3816 / 1.5485 | 0.7460 / 0.7716 | 0.4342 / 0.4944 | 0.2933 / 0.5583 |

Packing is retained on evidence from complete-step and traversal comparisons; gains vary, and the independent baseline below narrows their magnitude. Parallel evaluation further reduces the large interaction costs. Eight workers are not uniformly best: boundary/overlap 500 has a 0.0703 ms four-worker median versus 0.0855 ms with eight, and evolved Chains similarly favours four in Run 2. At 16 particles direct/serial-grid/eight-worker medians are 0.0007/0.0011/0.0119 ms; the two-particle timings approach timer resolution. Packing is not an overhead win at every small size: the 128-particle old/current serial-grid medians are 0.0184/0.0262 ms in their single full-suite processes. Direct remains explicit and the host default remains serial grid. There is no measured universal crossover or automatic backend switch.

### Independent baseline and current observations

Both accepted-M4 observations are shown where repeated, alongside both current serial-grid observations. Every cell is p50/p95 ms. A dash means no first baseline observation of that evolved state, not a missing current comparison.

| Case | Old grid, initial process | Old grid, independent repeat | Packed, Run 1 | Packed, Run 2 |
| --- | ---: | ---: | ---: | ---: |
| band-2000 | 19.3427 / 21.8921 | 12.2604 / 12.9881 | 10.1916 / 10.7194 | 9.8127 / 10.1464 |
| boundary-overlap-2000 | 3.8962 / 4.6507 | 2.6821 / 2.8243 | 2.6077 / 2.8203 | 2.1997 / 3.0160 |
| cluster-2000 | 69.1228 / 72.4275 | 29.3961 / 31.8980 | 30.0505 / 32.3803 | 26.6763 / 29.2164 |
| evolved-chains-500-t200 | - | 0.2570 / 0.2850 | 0.2093 / 0.2363 | 0.2037 / 0.2135 |
| evolved-random-500-t200 | - | 0.8235 / 0.9544 | 0.7614 / 0.8477 | 0.7208 / 0.7394 |
| evolved-snakes-500-t200 | - | 1.4947 / 1.6175 | 1.4592 / 1.6196 | 1.3816 / 1.5485 |
| uniform-density-10000 | 9.2250 / 9.8301 | 6.7199 / 8.0816 | 6.0446 / 7.5470 | 5.4262 / 5.6653 |
| uniform-fixed-2000 | 5.3669 / 6.2546 | 3.6904 / 4.1129 | 3.2553 / 3.4421 | 3.1329 / 3.2851 |
| uniform-fixed-500 | 0.3841 / 0.4064 | 0.2843 / 0.3020 | 0.2775 / 0.3242 | 0.2576 / 0.2672 |

The fixed-density 10,000 old-grid median is **9.2250 ms initially and 6.7199 ms on repeat**; current serial medians are 6.0446 and 5.4262 ms. Using the repeated baseline and full current run gives a 1.24× median ratio, rather than the 1.70× obtained from the first baseline. Dense 2,000 similarly shifts from 69.1228 to 29.3961 ms in the unchanged old grid; current serial is 30.0505/26.6763 ms, so one current observation is slightly slower than the repeated old-grid median. The unchanged direct path also varies substantially. These observations limit attributing the full historical-grid ratio solely to layout or claiming a tight universal speedup.

Across independent current runs, fixed-density 10,000 serial/eight-worker p95 maxima are **7.5470/1.4502 ms**; fixed-domain 2,000 maxima are **3.4421/0.7467 ms**. The corresponding dense 2,000 maxima are 32.3803/5.2536 ms. For the three time-200 500-particle states, eight-worker p95 maxima range from 0.1380 to 0.6188 ms. These are maxima of measured percentiles, not hard latency bounds or independent-run confidence intervals.

## Traversal, geometry and phase costs

The grid retains exactly the same candidate set/order as M4. Examples below report directed candidates and accepted pairs with nonzero separation on the saved input, including pairs whose coefficient is zero; direct always visits `N(N-1)` candidates. Exact coincidence counts remain separate. Every backend agrees on accepted/overlap counts.

| Case | Grid candidates | Accepted directed interactions |
| --- | ---: | ---: |
| uniform-density-10000 | 669,236 | 152,378 |
| uniform-fixed-10000 | 11,778,340 | 2,863,176 |
| cluster-10000 | 99,990,000 | 99,990,000 |
| band-2000 | 1,377,140 | 760,504 |
| evolved-random-500-t200 | 82,268 | 69,746 |
| evolved-chains-500-t200 | 33,724 | 14,272 |
| evolved-snakes-500-t200 | 152,506 | 131,976 |

Prepared candidate-only and candidate-plus-geometric-filter p50 ms expose the cost before coefficient/direction/force accumulation. The same source probe runs against old and current indexes. These original-index probes still gather positions and are not the packed force loop; subtracting columns would not isolate a reliable cost for one instruction group.

| Case | Old traversal | Current traversal | Old traversal + geometry | Current traversal + geometry |
| --- | ---: | ---: | ---: | ---: |
| uniform-density-10000 | 2.0028 | 1.6783 | 4.7772 | 4.9035 |
| uniform-fixed-10000 | 10.8780 | 6.9470 | 51.0733 | 50.2444 |
| cluster-2000 | 3.4853 | 2.1588 | 5.7656 | 5.9966 |
| band-2000 | 1.3849 | 0.9494 | 3.4273 | 3.5745 |

Contiguous enumeration reduces the traversal probe, but adding scattered geometric reads largely removes that benefit in this probe. The production loop additionally packs position/species data, making locality a relevant improvement rather than relying on SIMD alone. Dense accepted interactions still require the full pair mathematics and remain quadratic.

Separately sampled phase **p50 ms** from Run 2 follow. Integration is the serial-grid result; phases include their documented validation boundaries.

| Case | Direct interaction | Grid build/pack | Serial interaction | 4-worker interaction | 8-worker interaction | Integration |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| uniform-density-10000 | 151.4070 | 0.1107 | 5.2758 | 1.4553 | 0.9617 | 0.0215 |
| uniform-fixed-10000 | 310.5362 | 0.0855 | 68.3566 | 18.4004 | 10.2024 | 0.0272 |
| cluster-10000 | 996.1732 | 0.0917 | 724.1210 | 183.2134 | 96.4949 | 0.0331 |
| evolved-random-500-t200 | 1.4877 | 0.0058 | 0.7110 | 0.2451 | 0.1744 | 0.0009 |
| evolved-chains-500-t200 | 0.8069 | 0.0044 | 0.1968 | 0.0835 | 0.0894 | 0.0009 |
| evolved-snakes-500-t200 | 2.1784 | 0.0043 | 1.3802 | 0.4184 | 0.3367 | 0.0013 |

At fixed-density 10,000, build/pack remains about 0.11 ms versus 5.28 ms serial interaction. The retained improvement comes primarily from traversal/locality and receiver parallelism, not amortizing a large index build. No selective stencil or pair-symmetric force shortcut was promoted. Persistent workers justify their extra lifecycle/memory complexity on these larger workloads, while one-worker execution avoids that scheduling cost.

## Cutoff transitions

Below/at/above are adjacent doubles, not appreciably different physical radii. Accepted counts are unchanged within each triple, yet the force cell width doubles immediately above the boundary. These are fixed population, density, coordinates and coefficients. Old-grid values are the separate extended baseline invocations; current figures are from Run 2.

| Cutoff | Force cell width | Candidates / accepted | Old grid p50/p95 ms | Packed serial p50/p95 ms | 8 workers p50/p95 ms | Spatial diagnostic p50/p95 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 0.0625 below | 0.0625 | 170,344 / 60,270 | 3.5002 / 3.8045 | 3.2358 / 3.4068 | 0.7695 / 1.0430 | 2.8337 / 3.9734 |
| 0.0625 at | 0.0625 | 170,344 / 60,270 | 3.5222 / 5.3221 | 3.1198 / 3.2779 | 0.7407 / 0.8767 | 5.2823 / 6.3704 |
| 0.0625 above | 0.125 | 669,236 / 60,270 | 5.4841 / 5.8823 | 4.1456 / 5.0885 | 0.8711 / 1.0198 | 5.2309 / 5.7014 |
| 0.125 below | 0.125 | 669,236 / 237,164 | 7.7930 / 8.5259 | 6.3087 / 6.7813 | 1.2489 / 1.4159 | 2.1470 / 2.4554 |
| 0.125 at | 0.125 | 669,236 / 237,164 | 7.5678 / 8.1907 | 6.3063 / 6.5912 | 1.2039 / 1.4867 | 5.2340 / 6.7285 |
| 0.125 above | 0.25 | 2,576,538 / 237,164 | 15.1381 / 16.7140 | 11.4124 / 11.7731 | 1.9700 / 2.2080 | 5.2452 / 6.1785 |

Serial grid time rises from 6.3063 to 11.4124 ms immediately above 0.125 while accepted interactions remain 237,164. Diagnostic cells are **strictly wider** than their inclusive radius, so their discontinuity occurs at the boundary itself: 0.125-below versus 0.125-at gives 2.1470 versus 5.2340 ms. Neither discontinuity changes force or connectivity meaning. Cutoff-dependent budgets must include this geometry rather than treating particle count as the sole workload description.

## Evolving saved structures

Each continuation starts from the saved time-200 state, rebased to time zero, and performs **1,440 consecutive steps**, nominally 10 further reduced time units. Two repetitions reset only between trajectories, giving 2,880 timed steps per backend/preset. These are separate processes from static measurement, and each backend evolves independently. Values are p50/p95 ms.

| Preset | Direct | Packed serial | 4 workers | 8 workers | Candidate range | Occupied-cell range | Max endpoint position / velocity difference vs direct |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| random | 1.2174 / 1.4186 | 0.7094 / 0.7786 | 0.1969 / 0.2286 | 0.1101 / 0.1588 | 82,008-110,634 | 24-29 | 2.989e-15 / 4.230e-15 |
| chains | 0.7273 / 0.7876 | 0.1617 / 0.1777 | 0.0499 / 0.0693 | 0.0415 / 0.0571 | 33,722-33,900 | 52-54 | 4.578e-16 / 2.510e-15 |
| snakes | 2.1205 / 2.4316 | 1.3867 / 1.6199 | 0.3918 / 0.5148 | 0.2747 / 0.3267 | 152,396-153,418 | 16-16 | 8.903e-05 / 9.658e-04 |

All evolving requests completed without fallback or step allocation. Grid worker counts have identical endpoint error metrics and candidate/occupancy ranges. The Snakes trajectory amplifies initially tiny accumulation-order differences; its endpoint discrepancy is not a local-force error bound, nor does long chaotic divergence by itself establish a search defect. The saved states and step counts allow that divergence to be investigated. This longer evidence does not establish asymptotic or equilibrium behaviour.

## Exact diagnostics and responsiveness

These are separate sampled-state timings, **p50/p95 ms** from Run 2. Every spatial sample equals the reference result exactly. Samples retain their own time/step; simulation steps exclude sampling. Dense connectivity can finish after enough edges join the graph, while exact coincidence counting has already completed, so its cost need not follow the number of accepted force interactions.

| Case | Reference diagnostics | Spatial diagnostics | Spatial candidate / distance calls |
| --- | ---: | ---: | ---: |
| uniform-fixed-500 | 1.1276 / 1.2101 | 0.1162 / 0.1263 | 11,723 / 2,605 |
| uniform-fixed-2000 | 18.4491 / 19.6994 | 0.4018 / 0.4386 | 63,386 / 10,019 |
| uniform-density-10000 | 438.2934 / 445.2850 | 3.0999 / 3.6242 | 287,891 / 51,933 |
| uniform-fixed-10000 | 455.3257 / 484.5100 | 1.8776 / 2.7022 | 336,969 / 49,826 |
| cluster-10000 | 491.5478 / 502.5314 | 0.3057 / 0.3317 | 9,999 / 9,999 |
| evolved-random-500-t200 | 1.0820 / 1.2308 | 0.2135 / 0.2837 | 41,134 / 869 |
| evolved-chains-500-t200 | 1.0858 / 1.1242 | 0.1806 / 0.2173 | 16,862 / 5,350 |
| evolved-snakes-500-t200 | 1.4679 / 1.5525 | 0.3634 / 0.3902 | 76,253 / 1,154 |

Fixed-density 10,000 diagnostic median falls from 438.2934 to 3.0999 ms, about 141× on this input; its independent-process spatial p95 maximum is 3.6297 ms. Samples allocate **80 bytes** for the immutable result, versus 80,128 bytes for the 10,000-particle reference. Reusable storage is retained: 1,007,416 estimated payload bytes at 10,000 particles with particle capacity 10,000 and both dictionaries at 10,103 slots; 51,512 bytes at 500 with capacities 500/521/521. Clearing retained capacity still costs work after shrink. Arbitrary disconnected/dense geometry can remain quadratic; neither early termination nor this speedup is universal.

### Workload-specific budgets on this machine

The M4 planning budgets were 0.6 ms for uniform/boundary 500, 7 ms for fixed-domain uniform 2,000 and 12 ms for fixed-density 10,000, all core-step p95. The following rounded M4.1 targets are supported by the larger p95 from the two representative current invocations. They are planning headroom, not timing tests, maxima or guarantees for other states.

| Saved workload, R=0.1 | Workers | Observed p95 maximum | Core-step planning budget |
| --- | ---: | ---: | ---: |
| Uniform fixed-domain 500 | 1 | 0.3242 ms | 0.4 ms |
| Uniform fixed-domain 2,000 | 1 | 3.4421 ms | 4 ms |
| Uniform fixed-density 10,000 | 1 | 7.5470 ms | 8 ms |
| Uniform fixed-density 10,000 | 4 | 2.1328 ms | 3 ms |
| Uniform fixed-density 10,000 | 8 | 1.4502 ms | 2 ms |

A separate **4 ms diagnostic p95 planning budget** fits the fixed-density 10,000, R=0.1 samples; it does not cover the cutoff-transition cases, which reach 6.7285 ms. At `dt=1/144`, real-time stepping permits about **6.944 ms per step** before host work. Serial fixed-density 10,000 misses that p95 budget in one process; four/eight workers meet the core-only budget. Fixed-domain 10,000 still takes 11.2144 ms p95 with eight workers, the dense 10,000 cluster 100.6418 ms, and the 10,000 band 41.4277 ms: all miss it. Dense 2,000 reaches 5.2536 ms p95 with eight workers, leaving limited host headroom. Rendering, scheduled diagnostics and catch-up work remain additional costs; adding separate p95 values does not establish a joint frame percentile or viewer FPS.

## Numerical error, allocations and retained storage

Direct and grid accepted/overlap counts agree exactly for every static comparison. The grid keeps M4's source accumulation order, so packing and worker scheduling introduce no new per-receiver summation order. Unit/state tests retain the extreme, subnormal, fallback, stable-identity and failure-atomicity contracts; set/multiplicity assertions are exact, while ordinary reduced-unit force/short-step comparisons use the existing justified absolute-plus-relative tolerances.

Across the five current measurement processes, maximum local vector-norm differences against direct are **1.84449e-11 force**, **8.89911e-16 next position** and **1.28095e-13 next velocity**, all on dense 10,000. The largest relative force difference is **4.81097e-4** on time-200 Chains, where the maximum absolute force difference is only **4.60743e-15**. These maxima imply a vector norm no larger than 9.57691e-12 at that worst-relative-error particle. Near-zero residuals make relative error ill-conditioned; do not apply the earlier uniform-input relative bound to this state or omit the absolute scale. Maximum relative next-velocity error is 8.42668e-5 on the same case, with absolute error 3.19975e-17 there. Long-trajectory discrepancies above are reported separately.

Independent recomputation checked 889 numerical series and **50,100 complete-step samples**, including 43,200 evolving steps. Every complete step, build, interaction and integration sample in those current reports allocated **zero process-wide managed bytes**; the additional caller-plus-background force counters also report zero. This excludes warm-up, construction/growth, serialization, snapshots, diagnostic result objects and failed exceptional calls.

Packed index payload is approximately `52N + 36C` bytes serial or `68N + 36C` with parallel output storage, where N and C are retained particle/dictionary capacities. The accepted linked index used `20N + 36C`. For fixed-density 10,000, C=10,103 and 1,296 cells are occupied: packed storage is 883,708 bytes serial or 1,043,708 parallel, versus 563,708 for M4. These are payload estimates, not total process memory. Worker stacks/events and the simulation's other buffers are additional.

The following are single setup observations after shared code warm-up. **Construction starts at 2,500 particles**; growth resets to 10,000 and includes its first complete step. Shrinking back to 2,500 retains the established capacity.

| Workers | Construction ms / allocated bytes | Growth reset+step ms / allocated bytes | Retained index after growth and shrink |
| --- | ---: | ---: | ---: |
| 1 | 0.0373 / 231,184 | 5.6123 / 1,804,096 | 883,708 bytes |
| 8 | 0.4066 / 235,560 | 1.3553 / 1,964,120 | 1,043,708 bytes |

## Verification

On 4 November 2026, the full documented local gates passed: locked restore, formatting verification, Release build with zero warnings/errors, all **308 tests**, display-free viewer help/compatibility, XML documentation probes and **59 CLI commands**. The tests include direct/serial-grid/four-worker CLI-controller replay, exact worker-order checks, threshold/subnormal/extreme-radius cases, fallback and failure atomicity, disposal and allocation contracts. Logs and exit codes are retained under `artifacts/m4-1/final-verification/`.

Native Windows checks used MonoGame DesktopGL 3.8.6, .NET 10.0.10 and the NVIDIA GeForce RTX 5080 Laptop GPU. Separate launches verified default 500-particle serial grid, the legacy direct fixture, an explicit four-worker grid override and replay of a viewer-saved four-worker experiment without overrides. Observed checks covered single-step, reset and diagnostic clearing, focus-loss pause and explicit resume, scheduled sample time/step, diagnostic toggle, preset/reload worker retention, two distinct saves with identical contents, and a failed reload retaining the valid experiment paused. Saved values took precedence over `--species 50000`; the saved file also completed a 120-step CLI replay with configured/actual workers four. Tall (693×832) and compact wide (693×468) windows retained the square world and six-particle count; the compact wide world is small because controls occupy most of its height.

One approximately one-second default native timing sample recorded **144.0 FPS**, controller-update mean **0.262 ms**, maximum **0.660 ms**, and zero dropped playback time at step 877. This includes scheduled diagnostics and excludes drawing/present. It is a short native observation, not a solver benchmark or sustained latency bound. Native logs, saved experiments and the CLI replay report are retained under `artifacts/m4-1/native/`. Not every keyboard binding was exercised; Right and clickable help controls supplied the observed actions. Linux/macOS native windows remain unverified.

## Reproduce and retained evidence

Run the [full verification suite](../CONTRIBUTING.md) first, then stop competing work. Preparation is separate from timing. From the repository root:

```powershell
dotnet run --project benchmarks/ParticleLife.Benchmarks -c Release --no-build -- prepare-m41 artifacts/benchmarks/m4-1/inputs artifacts/benchmarks/m4
pwsh -NoProfile -File scripts/prepare-m4-baseline.ps1 -InputDirectory artifacts/benchmarks/m4-1/inputs
dotnet artifacts/m4-1/baseline/benchmarks/ParticleLife.Benchmarks/bin/Release/net10.0/ParticleLife.Benchmarks.dll compare artifacts/m4-1/baseline-results 15 all 2
dotnet run --project benchmarks/ParticleLife.Benchmarks -c Release --no-build -- compare-m41 artifacts/benchmarks/m4-1/run-1 artifacts/benchmarks/m4-1/inputs representative 15 2
dotnet run --project benchmarks/ParticleLife.Benchmarks -c Release --no-build -- compare-m41 artifacts/benchmarks/m4-1/run-2 artifacts/benchmarks/m4-1/inputs all 15 2
foreach ($preset in 'random', 'chains', 'snakes') {
    dotnet run --project benchmarks/ParticleLife.Benchmarks -c Release --no-build -- compare-m41 "artifacts/benchmarks/m4-1/evolving-$preset" artifacts/benchmarks/m4-1/inputs "evolved-$preset-500-t200" 15 2 1440
}
```

The independently repeated baseline, evolved/cutoff baseline and traversal profiles use the same archived core:

```powershell
pwsh -NoProfile -File scripts/prepare-m4-baseline.ps1 -InputDirectory artifacts/benchmarks/m4-1/inputs -OutputDirectory artifacts/m4-1/baseline-extended -SkipEvolution
$baselineDll = 'artifacts/m4-1/baseline/benchmarks/ParticleLife.Benchmarks/bin/Release/net10.0/ParticleLife.Benchmarks.dll'
$repeatCases = 'uniform-fixed-500', 'uniform-fixed-2000', 'uniform-density-10000', 'cluster-2000', 'band-2000', 'boundary-overlap-2000', 'evolved-random-500-t200', 'evolved-chains-500-t200', 'evolved-snakes-500-t200'
foreach ($case in $repeatCases) {
    dotnet $baselineDll compare artifacts/m4-1/baseline-extended 15 "artifacts/benchmarks/m4-1/inputs/$case.experiment.json" 2
}
foreach ($inputFile in Get-ChildItem artifacts/benchmarks/m4-1/inputs/cutoff-*.experiment.json) {
    dotnet $baselineDll compare artifacts/m4-1/baseline-extended 15 $inputFile.FullName 2
}
foreach ($case in 'uniform-density-10000', 'uniform-fixed-10000', 'cluster-2000', 'band-2000') {
    dotnet $baselineDll profile-m41 "artifacts/benchmarks/m4-1/inputs/$case.experiment.json" "artifacts/m4-1/baseline-profiles/$case.profile.json"
}
```

Use fresh output directories for independent repetitions. The per-case baseline files persist; its aggregate `comparison.json` is replaced by each single-case invocation. `inputs.json` verifies saved hashes on repeated preparation; seeds alone are not replay inputs. Generated serial inputs omit the optional worker field so the original M4 parser and current parser consume identical bytes. If historical M4 files are absent, preparation generates complete static inputs and labels that provenance. Use those same files across implementations rather than substituting historical hashes; pass `-InputDirectory artifacts/benchmarks/m4-1/inputs` to baseline preparation in that fresh checkout.

Large/generated evidence stays ignored and is not a source/build dependency:

- `artifacts/benchmarks/m4-1/inputs/`: 37 complete experiments, `inputs.json`, twelve SVG plots and `morphology.html`; exact origin/checkpoint hashes and morphology statistics.
- `artifacts/benchmarks/m4-1/run-1/`, `run-2/`, `evolving-random/`, `evolving-chains/`, `evolving-snakes/`: per-case and aggregate raw timing/allocation samples, environments, counts, errors and capacity data.
- `artifacts/m4-1/baseline/`, `baseline-results/`, `baseline-extended/`, `baseline-profiles/`: accepted source/build, source manifests, both baseline observations, extended input comparisons and prepared traversal probes.
- `artifacts/m4-1/tables.md`, `audit-summary.md`, `summarize.py`: compact derived evidence and independent input/count/percentile/allocation checks; console logs and `morphology.png` retain the observed generation/measurement session.
- `artifacts/m4-1/final-verification/`, `native/`: local gate logs/exit codes, native observations, saved parallel inputs and actual CLI replay output.
