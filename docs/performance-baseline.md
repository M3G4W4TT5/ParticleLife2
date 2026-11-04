# Serial reference performance

The M2 benchmark uses the direct, directed all-pairs evaluator with double-precision state and the specified damped semi-implicit Euler step. It establishes a reference for M4; it does not establish an interactive particle-count budget.

## Reproduce

Run from the repository root, in Release mode without a debugger:

```powershell
dotnet run --project benchmarks/ParticleLife.Benchmarks -c Release -- latency artifacts/benchmarks/reference 31 quick
dotnet run --project benchmarks/ParticleLife.Benchmarks -c Release -- latency artifacts/benchmarks/full 31 all
dotnet run --project benchmarks/ParticleLife.Benchmarks -c Release -- latency artifacts/benchmarks/replay 31 artifacts/benchmarks/reference/uniform-fixed-500.experiment.json
dotnet run --project benchmarks/ParticleLife.Benchmarks -c Release -- bdn --filter '*uniform-fixed-500*' --job short
```

`quick` samples 500-particle uniform, dense-cluster, narrow-band and boundary/overlap cases. `all` adds 2,000 and 10,000 particles and fixed-density domains. A single case name or saved experiment path can replace `quick`; run the executable without arguments to list names. Use the saved path when comparing implementations, avoiding dependence on future random-generator behaviour. BenchmarkDotNet accepts its own filters and job settings after `bdn`.

BenchmarkDotNet saves inputs and session metadata under `artifacts/benchmarks/bdn-inputs`, outside its temporary build folders. Existing inputs are loaded on subsequent runs. Set `PARTICLELIFE_BENCHMARK_INPUTS` to an absolute directory containing previously saved case files to compare the same values; use a fresh directory to deliberately generate new inputs.

The latency runner saves each full experiment beside `latency.json`, including actual coordinates, velocities, species, stable identities, directed matrix, reduced-unit parameters and generator information. The report records input hashes, core and benchmark assembly hashes, Git commit and dirty status, runtime/SDK, OS, CPU, build configuration and one solver thread. Keep those inputs with raw measurements when comparing runs. Generated benchmark files are ignored by Git.

## Method and interpretation

Every timed step starts from the same initial state; resets occur outside the measured region. The runner warms each case for at least 64 steps and one second, recording the actual warm-up count. This gives tiered compilation time to settle but is not a guarantee against all JIT or scheduler effects. It then records complete-step latency and separately times force evaluation (including soft walls) and integration. Percentiles use nearest ranks. Stage timings include public API validation; they need not sum exactly to whole-step timings. There is no index-build stage in the direct reference.

Allocation deltas use `GC.GetAllocatedBytesForCurrentThread` around each measured call. BenchmarkDotNet provides an independent memory-diagnoser run. Its iteration setup restores initial state outside timing and forces a single invocation per iteration; short cases can therefore have wide confidence intervals.

Diagnostics are measured separately, with at most five samples per case. They use a cluster distance equal to the interaction cutoff and include the quadratic connected-component calculation. Their costs are excluded from core step timings. Rendering is absent. Candidate and accepted pairs are directed; coincident-pair counts are unordered. The boundary case retains off-world particles and one deliberately coincident pair.

Uniform fixed-domain cases use `[0,1]^2`. Fixed-density cases use side length `sqrt(N/500)`, keeping 500 particles per reduced area. Dense clusters lie inside `[0.475,0.525]^2`, so every distinct pair is within the 0.1 cutoff. Bands occupy `[0,1] x [0.49,0.51]`. All cases use seven species, seed 1729, and otherwise the initial model defaults. The saved values are authoritative; the seed alone is not a cross-runtime reproducibility guarantee.

The reported numerical difference compares separately evaluated public stages with `Simulation.Step` on identical input. It checks stage composition and buffer wiring, not the force law independently. Mathematical tests provide that evidence. No optimized solver exists yet, so this is not an optimization speedup or error comparison.

## Recorded results

Measurements are recorded here after the integrated Release build and correctness checks pass. Machine load, JIT tiering, sampling count and operating-system scheduling limit timing claims; no shared-CI timing threshold is enforced.
