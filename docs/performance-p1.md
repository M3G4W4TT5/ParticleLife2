# P1 workload and measurement protocol

P1 adds saved inputs at 10,000, 50,000, 100,000 and 250,000 particles, with 1,000,000 available as an explicit stretch selection. These are measurement points, not interactive capacity claims. The initial 10k CPU screen below has completed; subsequent CPU/GPU capacity and native rendering/input observations are recorded in [P7](performance-p7.md) and the [visual review](visual-review-checklist.md). GPU interoperability has its [own report](gpu-integration.md). No performance improvement is claimed by adding this tooling.

The tracked recipe is `benchmarks/ParticleLife.Benchmarks/P1Inputs.cs` (`p1/1`). It writes complete experiment files and a hash manifest under ignored `artifacts/`. Generated states are not source dependencies or source-controlled bulk fixtures. A prepared input directory is immutable: repeat preparation verifies every saved file and the requested coverage; adding cases requires a new directory. An interrupted preparation without a completed manifest must also use a new empty directory.

## Inputs and their meaning

All cases retain the current binary64 CPU model, damped semi-implicit Euler, soft walls, seven species, seed 1729 and fixed timestep `1/144` reduced time units. Parameters are cutoff `0.1`, core fraction `0.3` (core radius `0.03`), force scale `1`, damping half-life `0.025`, wall layer `0.05` and wall strength `1.44`. Every directed species pair has the same cutoff/core; heterogeneous pair ranges are not implemented by these fixtures. The complete directed matrix and actual IDs, positions, velocities and species are saved in each experiment, alongside model/integrator identifiers and generation runtime. The manifest records complete parameters, requested grid/one-worker execution, file sizes and SHA-256 hashes. A seed alone is not a cross-runtime reproduction promise.

| Case prefix | World and initial geometry | Meaning |
| --- | --- | --- |
| `uniform-density` | Square side `sqrt(N/500)`; uniform positions | Density 500 per reduced area at every population; cutoff unchanged. |
| `uniform-fixed` | Unit square; uniform positions | Increasing density with unchanged model and world. |
| `band` | Unit square; `x` uniform `[0,1)`, `y` uniform `[0.49,0.51)` | Concentrated strip, not an evolved band. |
| `cluster` | Unit square; both coordinates uniform `[0.475,0.525)` | All initial distinct pairs within cutoff; quadratic stress, with no occupancy truncation. |
| `evolved-tiled` | Exact translated copies of a completed 500-particle motif in unit tiles | Locally evolved geometry; the large world has not evolved for 720 steps. |

The first four geometries use the same Random matrix and seeded uniform draws for a given population. Band and cluster transform positions only. Their initial velocities are zero. Fixed-density and fixed-area cases change world size; comparing them is a workload comparison, not a solver regression test.

The evolved motif starts from Chains, 500 particles, seven species and seed 1729, with the same parameters. It advances exactly 720 serial uniform-grid steps before capture. Both the complete origin and completed motif are saved and hashed. The captured source time, step and generator are recorded before rebasing its clock. Large inputs copy motif species and velocities unchanged, translate positions, and assign new unique sequential IDs. They use near-square integer factorization with no empty tiles: `4 × 5`, `10 × 10`, `10 × 20`, `20 × 25` and `40 × 50` respectively. Thus their nominal density also stays 500. Particles outside the source unit square remain outside their tile; positions are not clipped. There are no internal walls: particles on opposite sides of a seam interact normally. This is a practical evolved-motif workload, not evidence of a mature large-world morphology or the cost of globally evolving that population. P7 must revisit actual large-world evolved states.

## Commands and budgets

Run from the repository root in Release, with the native viewer closed and no competing builds, tests or benchmarks. Restore/build once before measurement; do not include those costs in solver timing:

```powershell
dotnet restore benchmarks/ParticleLife.Benchmarks/ParticleLife.Benchmarks.csproj --locked-mode
dotnet build benchmarks/ParticleLife.Benchmarks/ParticleLife.Benchmarks.csproj -c Release --no-restore
dotnet run --project benchmarks/ParticleLife.Benchmarks -c Release --no-build -- prepare-p1 artifacts/benchmarks/p1/inputs-screen screen
dotnet run --project benchmarks/ParticleLife.Benchmarks -c Release --no-build -- measure-p1 artifacts/benchmarks/p1/screen-w1 artifacts/benchmarks/p1/inputs-screen screen 5 30 1 evolving
```

Preparation defaults to `screen`, generating all five 10k geometries and the small source motif; it does not measure any large simulation. Default measurement screens only `uniform-density-10000` and `evolved-tiled-10000`, five samples each, two warmup steps, one worker, and a **30-second whole-child budget per case**. Short screening discovers feasibility and tooling problems; two warmup steps and five samples do not establish stable JIT behaviour or a precise tail-latency estimate. The nearest-rank p95 of five samples is their maximum. Preparation, especially JSON serialization, also consumes time and memory; it is outside the measurement deadline.

```text
prepare-p1 input-directory [selection=screen]
measure-p1 output-directory input-directory [selection=screen] [samples=5] [timeout-seconds=30] [workers=1] [mode=evolving]
```

| Selection | Preparation | Measurement |
| --- | --- | --- |
| `screen` | All five geometries at 10k | Only 10k fixed density and tiled evolved |
| `density` | Fixed density at 10k/50k/100k/250k | Same four cases |
| `ladder` | All five geometries at 10k/50k/100k/250k | Same twenty cases, including dense stress |
| `stress` | Fixed area, band and cluster at 10k/50k/100k/250k | Same twelve dense cases |
| `stretch` | All five geometries at 1M | Same five cases; explicit opt-in |
| `all` | All five geometries at all five populations | Same twenty-five cases; explicit opt-in |
| Exact case name | One case, e.g. `uniform-density-50000` | That case only |

Use a fresh output directory for every run. Missing requested inputs become `skipped-not-prepared` entries; the tool never generates missing cases during measurement. Workers are explicit, currently `1..8`; the runner overrides the saved worker count and reports both. It uses uniform-grid double precision throughout, reporting actual last execution, fallback count, candidate/accepted pairs and retained grid capacity. It never silently changes density, rules, particle count, timestep or precision to fit a budget. Failures/timeouts/skips are observations in `p1.json`, not a failed shell command; inspect those statuses when automating checks. Invalid invocation or supervisor errors return exit code 2.

For a broader fixed-density screen and matched worker comparison, prepare once and measure in separate sequential runs:

```powershell
dotnet run --project benchmarks/ParticleLife.Benchmarks -c Release --no-build -- prepare-p1 artifacts/benchmarks/p1/inputs-density density
dotnet run --project benchmarks/ParticleLife.Benchmarks -c Release --no-build -- measure-p1 artifacts/benchmarks/p1/density-w1 artifacts/benchmarks/p1/inputs-density density 5 30 1 evolving
dotnet run --project benchmarks/ParticleLife.Benchmarks -c Release --no-build -- measure-p1 artifacts/benchmarks/p1/density-w4 artifacts/benchmarks/p1/inputs-density density 5 30 4 evolving
```

Do not automatically run the entire dense ladder. An initial dense probe can select one already prepared case with a ten-second whole-child budget:

```powershell
dotnet run --project benchmarks/ParticleLife.Benchmarks -c Release --no-build -- measure-p1 artifacts/benchmarks/p1/cluster-10k-budget artifacts/benchmarks/p1/inputs-screen cluster-10000 5 10 1 static
```

Every case runs in its own child process. The deadline includes child startup, environment capture, hashing/loading, warmup, sampling and report writing. A timeout kills that child and its owned workers, with up to five seconds termination grace, then proceeds to the next selected case. Parent manifest verification is outside that deadline. A stage progress file distinguishes loading, warmup and measurement; a timeout does not establish that one step took the whole budget. No incomplete distribution is inferred from a terminated case. Complete per-case reports are published atomically. Standard output/error, case status and manifest hash remain in the parent report. The whole run budget is roughly case count times the per-case deadline plus verification and termination overhead, not a global fixed limit.

`evolving` performs two warmup steps, then advances the same simulation for the requested sample count. It reports actual sampled start/end clocks, step p50/p95/raw samples, and completed simulation time divided by measured sampling wall time. That wall region includes allocation-counter/telemetry overhead but excludes loading, warmup, full memory collections and report writing. `static` restores identical saved state outside each timed step; it reports compute-equivalent speed and leaves achieved evolving throughput null. Use `static` when comparing implementations on exactly the same one-step inputs. Evolving trajectories can differ across execution configurations; retain their raw inputs and configuration metadata.

Process-wide precise allocation deltas surround only each step, including grid background workers. Reset, memory sampling, hashing and reporting are excluded. A short run can allocate as cell occupancy grows; report the observation instead of claiming a general zero-allocation guarantee. Memory samples before input load, after warmup and after sampling include live managed bytes after forced collection, process working/private memory and peak working set. They include the saved snapshot, benchmark runtime, workers and index; they do not isolate solver memory. Grid retained bytes are a separate implementation-dependent payload estimate, excluding runtime/object/thread overhead.

## Targets and pending evidence

The presentation target is 60 FPS, independently of simulation throughput. At timestep `1/144`, playback speed 1 requires about 144 completed steps per wall second; solver-only mean cost must therefore be below roughly 6.94 ms before other scheduling overhead. A 60 FPS render loop does not prove that playback speed is sustained. A slower solver should retain usable camera/input controls and visibly report achieved speed.

For P2/P7, record measured FPS and frame distribution, controller/input latency while idle and dragging, upload/submission/draw/present boundaries, viewport, particle size, diagnostics, trails/glow and display synchronization. The initial responsiveness goal is input-to-visible-control feedback p95 below 50 ms while simulation is busy; this is a target requiring native observation, not a solver-runner result. Compare 1920 × 1080 and the supported small-window workflow with consistent settings. GPU measurements also need device/API/driver, precision, device memory, transfer/readback and synchronization boundaries.

| Evidence | Status |
| --- | --- |
| Workload generation/hash verification and minimal build | Passed; locked restore, Release benchmark build, repeated hash verification |
| 10k CPU fixed-density/tiled-evolved screen, p50/p95 and achieved speed | Completed short screen; values below |
| 50k/100k/250k density and worker comparison | Completed bounded one/four-worker screen below |
| Dense geometries | All ladder inputs saved; 10k fixed-area/band/cluster probes completed |
| 1M stretch | Saved fixed-density input and four-worker run completed; no interactive-capacity claim |
| Native rendering, frame rate and input proxy | Scoped CPU/GPU observations in [P7](performance-p7.md); native review and coverage limits in the [visual review](visual-review-checklist.md) |
| GPU buffer interoperability, memory and transfers | Completed prototype and production follow-up; see [GPU evidence](gpu-integration.md). This report retains the original P1 CPU screen. |

Publish measured values here only with the generated report paths/hashes, commit and assembly hashes, dirty-tree status, SDK/runtime/OS/CPU/logical processors, workers, power/runtime overrides, model/timestep and exact mode/budgets. Reuse retained inputs rather than rerunning historical long preparation commands. Extend measurements only when the next implementation decision requires them.

## Initial 10k CPU screen — 5 November 2026

The baseline source was `23622fa90b176d246093e3015d3319b78e411269` plus uncommitted P1 benchmark tooling; production core/viewer source was unchanged. The machine used Windows 11 Home build 26300, .NET SDK 10.0.302, runtime 10.0.10, an Intel Core Ultra 9 275HX (24 logical processors), and approximately 31.46 GiB visible RAM. No builds, tests or native viewers ran during measurement. The runner records runtime and process environment; no power-plan override was applied by the task.

Each row uses one CPU grid worker, binary64, two warmup steps followed by five evolving samples and a 30-second whole-child deadline. The p95 is the maximum of those five samples. These brief observations are a feasibility screen, not a stable tail estimate or a matched optimization result.

| Saved input | World | p50 / p95 ms | Achieved simulation speed | Step allocations |
| --- | --- | ---: | ---: | ---: |
| `uniform-density-10000` | sqrt(20) × sqrt(20) | 9.4851 / 9.8001 | 0.726× | 0 B |
| `evolved-tiled-10000` | 4 × 5 | 8.6987 / 10.4593 | 0.765× | 0 B |

The warmed process working sets were 54,382,592 and 55,267,328 bytes respectively. Both grid payload estimates were 883,708 bytes; process memory also includes the parsed experiment, runtime and tooling. Neither screen sustains requested playback speed 1. Rendering and input work are absent from these measurements.

Input SHA-256 values are `72BE0CC6508EA6F8E9EDA6D9AC1FBEE0AE79DBF9AD7522D5B651E783D0D8E1D4` and `49907C14CB0E23A061195EDD6874A1F1D7EB4E73452A301B9819F1320F3346CA`. The measured core assembly hash is `D7AFA8A6CB254C88FAE29F89823E29F13133D500182E151C9A4C0E0D67285E07`; benchmark assembly hash is `4F9913F7AAA4F34A1D0BDC9492B2B5A8AFA4E6DEE8C6E904B5F12134A79D3D57`. Raw results and verified inputs are `artifacts/benchmarks/p1/screen-w1/` and `inputs-screen/` in the P1 worktree.

A separate `cluster-10000` harness check requested 10,000 static samples with a one-second child budget. It terminated during warmup after 1.029 seconds and reported `timed-out`, with no latency distribution. This validates the timeout mechanism; it is not a measured one-second step or a completed dense capacity test. Build and check logs are under `artifacts/implementation/p1-checks/` in that worktree.

## Population ladder screen

The same binaries and machine then generated all twenty 10k/50k/100k/250k geometry inputs and a separate one-million fixed-density input. The Balanced power plan was recorded; no runtime or power overrides were applied. Measurements again ran sequentially without competing builds, tests or native windows. All twelve requested cases completed, with zero measured step allocations and no direct fallback.

| Workload | Mode / workers | p50 / p95 ms |
| --- | --- | ---: |
| Fixed density 10k | Evolving / 1 | 9.94 / 10.26 |
| Fixed density 50k | Evolving / 1 | 47.02 / 50.97 |
| Fixed density 100k | Evolving / 1 | 76.46 / 103.15 |
| Fixed density 250k | Evolving / 1 | 190.07 / 196.59 |
| Fixed density 10k | Evolving / 4 | 5.60 / 6.01 |
| Fixed density 50k | Evolving / 4 | 26.09 / 26.24 |
| Fixed density 100k | Evolving / 4 | 50.97 / 52.25 |
| Fixed density 250k | Evolving / 4 | 113.24 / 129.77 |
| Fixed area 10k | Static / 4 | 24.53 / 24.57 |
| Band 10k | Static / 4 | 84.27 / 88.24 |
| Dense cluster 10k | Static / 4 | 187.08 / 192.43 |
| Fixed density 1M | Evolving / 4 | 245.18 / 248.43 |

Each case still has only two warmup steps and five measured samples. The deadline is thirty seconds for density runs and ten seconds for each dense probe. Warmup wall duration therefore varies substantially with population and can leave smaller cases at a different JIT tier; these observations must not be treated as steady-state scaling or proof of an optimization. The million-particle run achieved approximately 0.028× requested simulation speed, excluding rendering and diagnostics. The denser 10k cases are distinctly more expensive despite having fewer particles.

The ladder manifest hash is `C4BB5E60153F3176B5419366149D174FC41083346AB308C3B88AA73E77B83CB0`; the stretch manifest hash is `22C5CD381D41D40DBAC145B0F4F94082D60D33557E11115EFA64342FF24FA920`. Complete per-input hashes, memory, raw timings and actual boundary metadata are in `artifacts/benchmarks/p1/inputs-ladder/`, `inputs-stretch/`, `density-w1/`, `density-w4/`, `fixedarea10k-w4/`, `band10k-w4/`, `cluster10k-w4/` and `stretch-density-w4/` in the P1 worktree. A compact table, environment and commands are under `artifacts/implementation/p1-baseline-checks/`.

P2 optimization decisions will use identical saved inputs and longer bounded warmup (at least eight calls and 0.5 seconds for ordinary cases), with matched static and representative evolving measurements. P7 will measure actual evolved large worlds, presentation and input with the completed app. Dense runs retain an explicit deadline, including when warmup itself is impractical.
