# M4 spatial-search performance

The M4 comparison measures the serial direct and exact uniform-grid backends on identical saved inputs, with unchanged model equations, double precision and damped semi-implicit Euler integration. The [M2/M3 baseline](performance-baseline.md) remains historical context; it is not the denominator of a current speedup.

## Reproduce

Build and verify the pinned Release solution using [CONTRIBUTING.md](../CONTRIBUTING.md). Stop competing benchmarks, builds, tests and viewer processes before measuring. Run from the repository root without a debugger:

```powershell
dotnet run --project benchmarks/ParticleLife.Benchmarks -c Release --no-build -- compare artifacts/benchmarks/m4 31 all 3
$env:PARTICLELIFE_BENCHMARK_INPUTS = (Resolve-Path artifacts/benchmarks/m4).Path
dotnet run --project benchmarks/ParticleLife.Benchmarks -c Release --no-build -- bdn --filter '*uniform-fixed-500*' '*uniform-fixed-10000*' '*uniform-density-10000*' --job short --warmupCount 6 --iterationCount 15
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

## Evidence

Controlled measurements and native verification are recorded below when collected. Raw generated inputs, per-case reports, full sample arrays and session metadata belong under ignored `artifacts/benchmarks/m4/`; BenchmarkDotNet logs and reports remain under ignored `BenchmarkDotNet.Artifacts/`. They are not build or test dependencies.
