# GPU execution and rendering boundary

P1's bounded experiment lives in `prototypes/GpuInterop/`, outside the production solution. It uses the application's pinned **MonoGame DesktopGL 3.8.6** and `GraphicsProfile.Reach`. The first target is **Windows with a current OpenGL 4.3 or newer context**. It adds no new package dependency beyond the existing MonoGame dependency chain.

The compute/render/capture route was **demonstrated on 5 November 2026** on the RTX 5080 Laptop GPU through the unchanged MonoGame host. The actual native run, pixel checks and saved-framebuffer inspection passed. Live-window computer-use inspection remains pending because app approval timed out; it is not reported as accepted. The consolidated visual review remains separate.

## Integration choice

Keep the MonoGame desktop host, input and editor. Put GPU kernels, buffers and particle drawing in a narrow OpenGL adapter using the host's current context. A storage-buffer object written by a compute shader is bound directly as the vertex buffer for the particle draw. There is no second device, OpenCL/CUDA allocation, cross-API import, or intermediate CPU copy during ordinary frames.

The locally installed package was inspected before choosing this route. Its nuspec pins SDL 2.32.10.2, OpenAL 1.24.3.4 and NVorbis 0.10.4 and identifies MonoGame source commit `c610a529667336f63da323c6057274258727664b`. The managed assembly exposes no public `GraphicsDevice` method to retrieve its OpenGL context or invalidate its internal state cache; the presence of the shared `NativeGraphicsHandles` type alone does not establish such access. The prototype therefore loads GL entry points through Windows `wglGetProcAddress` and verifies that the existing context remains current on the same thread. It neither reflects private fields nor changes context creation.

The actual context version is queried at startup. The tested `Reach` host supplied OpenGL 4.6 without any context-creation change. A context below 4.3 or missing entry point fails this GPU smoke explicitly. Merely selecting `GraphicsProfile.HiDef` is not treated as a promise of compute support. A controlled context-version request or host adaptation would be needed on a driver that supplies an older context; this prototype does not pretend such a path has been demonstrated.

The relevant API rules are the Khronos [shader-storage/vertex/buffer-update barriers](https://wikis.khronos.org/opengl/GLAPI/glMemoryBarrier), [incoherent memory visibility](https://wikis.khronos.org/opengl/Incoherent_Memory_Access) and [synchronization objects](https://wikis.khronos.org/opengl/Sync_Object). The Windows entry-point mechanism is documented by Microsoft for [`wglGetProcAddress`](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-wglgetprocaddress). MonoGame's [native-handle data type](https://docs.monogame.net/api/Microsoft.Xna.Framework.Graphics.NativeGraphicsHandles.html) describes backend-specific handles, but is not used by this adapter.

## What the experiment demonstrates

The smoke uploads 4,096 records once and advances 120 fixed translation steps of `dt = 1/64`. This intentionally simple 2D rule isolates ownership, layout, synchronization and rendering; it is **not a particle-life force solver**, grid implementation, capacity claim or numerical-model comparison.

Each 32-byte record contains two float32 position components, two float32 velocity components, the low/high uint32 words of a stable 64-bit identity, a uint32 species and initialized padding. The identity high word is nonzero, testing that identities survive without a float conversion or loss of high bits. GLSL `std430` and the explicit C# layout agree. The fixture uses power-of-two velocity/timestep multiplication so byte comparison with its CPU translation reference is appropriate; this does not imply bitwise CPU/GPU force trajectories.

Two buffers alternate roles. Compute reads only the completed input and writes the other buffer. A shader-storage barrier before reuse orders prior reads before writes. After dispatch, shader-storage and vertex-attribute barriers make output visible to subsequent compute and vertex fetching; buffer-update visibility supports the later explicit capture. The adapter then publishes the output handle. The published handle is GPU-command-visible, rather than a claim that execution has finished on the CPU.

The graphics thread submits the compute and draw commands in one context. The renderer borrows the completed handle only for the immediate draw submission; no other thread retains an unowned native handle. Ordinary frames contain no particle readback or explicit CPU fence wait. A separate VAO holds the adapter's vertex attributes. `GlStateScope` restores every touched program, VAO, array/SSBO binding, indexed SSBO slot and enable flag, keeping MonoGame's cached state valid. The framebuffer and viewport stay owned by MonoGame. The current host has no other SSBO users; indexed restoration uses whole-buffer bindings. Supporting an additional adapter that uses ranged SSBO bindings would require preserving its offsets and lengths as well.

An explicit capture inserts a completion fence, waits with a five-second deadline and reads owned bytes with `glGetBufferSubData`. The full state is compared byte-for-byte with the CPU fixture, written to `capture.bin`, loaded from disk and restored into fresh device buffers. A second explicit readback must retain every stored byte. This raw little-endian file is an experiment artifact, **not an application checkpoint format**.

The original output and restored output must each render all 4,096 expected particle-centre pixels in their species colours. Magenta and cyan rectangles drawn by MonoGame before and after the native draw must retain their expected pixels. These checks exercise actual storage-to-vertex consumption and return to normal MonoGame drawing. `result.json` records the shared compute/render object names, state hashes, pixel checks, device/API information and explicit readback durations. The saved framebuffer is a separate explicit observation. Those durations include waiting and driver overhead and are not kernel timings.

## Recorded P1 evidence

The Release run used Windows `10.0.26300.0`, .NET runtime `10.0.10`, MonoGame `3.8.6.0`, `GraphicsProfile.Reach`, **NVIDIA GeForce RTX 5080 Laptop GPU**, OpenGL **4.6.0 NVIDIA 621.01** and GLSL **4.60 NVIDIA**. No host adaptation or secondary context was needed on that configuration.

| Check | Observed result |
| --- | --- |
| Release build and full project formatting | Passed; zero build warnings/errors. |
| CPU-only startup | Passed, 4,096 particles and 120 steps; no SDL2/OpenGL/OpenAL native modules loaded. |
| Native hardware process | Exited successfully with `status: pass`. |
| Ordinary GPU execution | 120 fixed steps, 1.875 simulated seconds; zero particle-state readbacks. |
| Compute-to-render storage | Original completed buffer 1; restored compute and vertex buffer both 3. |
| GPU-rendered expected positions/species | All 4,096 particle-centre pixel checks passed before restore and all 4,096 after restore. |
| MonoGame state restoration | Both before/after marker pixel checks passed. |
| Capture and disk restore | Two explicit particle readbacks; every captured/restored byte matched the CPU fixture, including high identity bits. |
| Resident particle-buffer payload | 262,144 bytes for two buffers; capture file 131,072 bytes. This excludes driver/program/framebuffer allocations. |
| Saved framebuffer inspection by verification worker | Coloured grid and both magenta/cyan markers observed. |
| Live-window computer-use inspection | Pending: the PASS window was enumerated, but obtaining its state returned `Computer Use app approval timed out` during the bounded hold. |

CPU, captured and restored SHA-256 were all `2BDB3F7DDB43EB2BD683E75828B8BBC2BE21C13362EB9E424C923225EF090744`. The single reported state capture took 0.4994 ms and final framebuffer readback 0.8278 ms. The state capture followed an explicit framebuffer read that had already drained prior work, so these are functional-smoke observations, not representative asynchronous capture latency or transfer-throughput measurements.

Evidence is retained locally under ignored `artifacts/implementation/p1-gpu-smoke-1/` (`result.json`, `capture.bin`, `framebuffer.png`) and `artifacts/implementation/p1-gpu-checks/` (build, CPU-only, formatting/native logs and `verification-summary.txt`). The prototype passed the functional P1 integration criterion. Live-window visual acceptance is still carried into the consolidated review; the approval timeout is not evidence of a rendering failure.

## Reproduce the check

Run from the repository root, with no benchmarks, competing builds/tests or other particle viewers running:

```powershell
dotnet restore prototypes/GpuInterop/GpuInterop.csproj --locked-mode
dotnet build prototypes/GpuInterop/GpuInterop.csproj -c Release --no-restore
dotnet format prototypes/GpuInterop/GpuInterop.csproj --verify-no-changes --no-restore
dotnet run --project prototypes/GpuInterop -c Release --no-build -- --cpu-only
dotnet run --project prototypes/GpuInterop -c Release --no-build -- --smoke artifacts/implementation/p1-gpu-smoke
```

Use a fresh output directory on each run; the smoke exclusively creates its evidence files. CPU-only mode runs without constructing `Game` or loading a native graphics library and rejects loaded SDL2/OpenGL/OpenAL modules. The GPU mode requires a real desktop; it displays a coloured grid and the two MonoGame markers, then retains the PASS window for 45 seconds before closing. Escape before PASS cancels with a nonzero result. Failures print their exception and return a nonzero exit code; no successful report is written.

Prescribed native inspection must confirm the window and markers using the computer-use workflow. Keep the machine-readable `result.json`, `capture.bin`, `framebuffer.png`, console output and inspection screenshot under ignored `artifacts/`. This is the narrowly justified P1 hardware smoke, not the consolidated P1–P7 visual review.

## Smallest production boundary for P5

The shared model, CPU engine, logical edit commands and file data remain graphics-free. Do not force GPU execution into the CPU-span `IForceEvaluator`. P2 can move the CPU owner to a worker thread and publish reusable completed render data independently of this GPU route. Its bulk renderer must split ordinary draw entry points from optional compute support, or keep the existing rendering fallback: the prototype's eager 4.3 compute requirement must not become a CPU-viewer prerequisite. GPU command submission can remain on the GL owner thread while the device executes resident kernels; moving the GL context between threads or adding shared contexts is unnecessary for the demonstrated route.

A production GPU adapter needs operations to submit a fixed step, apply small commands at a completed boundary, expose a short-lived completed render lease, request/poll an explicit owned capture, and dispose device resources. Its publication record carries completed step/time, active configuration/version and actual execution/precision metadata. The editor must be able to observe that small record and apply scalar/rule edits without calling CPU `Observe()` or downloading all particles. Full downloads are deliberate for backend transfer, structural edits requiring host state, checkpoints and file capture.

P5 should make explicit capture asynchronous: submit a visibility barrier and fence, poll completion without blocking input, then publish an owned captured state with its exact boundary metadata. A bounded staging/capture queue must keep the source allocation alive until the GPU copy and readback complete. Cancelling the caller does not permit early buffer reuse while a copy still owns it. A CPU switch publishes only after capture, conversion and CPU construction all succeed; a failed switch leaves the active backend valid. GPU/device loss may recover only the latest valid owned capture, never uncaptured device state.

The prototype's full state save/restore is evidence for the transfer primitive, not completed P5 persistence. P5 still owns particle-life kernels and spatial bins/scan/scatter, P4 boundaries and directed ranges, P3 commands/structural edits, versioned backend/precision metadata, capture/checkpoint formats, user-visible selection/failure handling, asynchronous staging and matched performance measurements. Full-frame GL state queries and fixed 3-pixel points are correctness scaffolding here; retain a narrow restoration contract and measure any production changes.

## Explicit limits

- Windows/WGL and the actual device/context reported by the smoke are the first supported route. Linux, macOS, alternate GPUs, older GL contexts and cross-context synchronization are unverified.
- CPU-only startup is headless. The normal MonoGame CPU viewer still needs its ordinary rendering device; CPU simulation/CLI must remain independent of GPU compute availability.
- The prototype submits one small stage per display frame solely to bound its test duration. Production simulation time must remain independent of rendering cadence and use fixed numerical steps.
- Captures block in this bounded proof; asynchronous capture, allocation failure recovery, device-loss recovery and app checkpoint semantics remain P5 work.
- No GPU numerical performance, one-million-particle capacity, deterministic shader reductions or cross-device trajectory identity is claimed.

## P5 production implementation and verification

The production implementation is in `src/ParticleLife.MonoGame/Gpu/`, with graphics-free execution/capture contracts in Core and Viewer. It is separate from the P1 prototype. The Release build, focused capture/CLI/ownership tests and production hardware correctness harness pass. Performance measurements and native interaction acceptance remain separate work; the hardware check is not the consolidated visual review.

`GlGpuSimulation` owns two 32-byte logical-slot state buffers. Each step clears/counts hashed cells, hierarchically scans bucket counts, scatters logical indices, then evaluates directed forces from the consistent completed input and integrates a separate candidate. Full signed cell-coordinate checks filter hash collisions; occupancy is never capped. The scan is linear work across 512-element blocks and recursively scanned sums. The ordinary path reads only small completion/occupancy records. Dense buckets reduce the receiver count per submission; they never discard neighbours or change the model. A completed candidate publishes only after its fence and numerical-status checks pass.

All native work remains on the current MonoGame graphics thread. The owner polls fences with timeout zero and retains one candidate and one explicit staging capture. It submits a bounded amount of work per UI pump and caps retained playback demand without changing fixed numerical steps. Cancelled or failed candidates leave the last completed buffer and clock intact. Model/workspace replacements construct candidate resources before publication. Cleanup checks context ownership, so loss of the native context cannot replace the original failure with another cleanup exception.

`GlResidentRenderer` consumes that same completed buffer as instanced disc attributes: position at byte 0, velocity at byte 8, identity words at byte 16 and species at byte 24. It uploads no particles. Camera/radius changes are uniforms; colours use a separate palette. Quads avoid point-size and centre-clipping limits. Native draw and compute scopes restore their touched state, including ranged SSBO offsets/sizes. CPU rendering remains independently available through the GL 3.3 point adapter or MonoGame bulk-quad fallback, without compute initialization.

The supported GPU numerical domain is deliberately finite: model scalars must remain zero or normal finite float32 values, nonzero coefficients cannot round to zero, directed core/cutoff intervals cannot collapse, and cutoff squares must be normal finite float32. Nonperiodic cell coordinates are bounded by `2^20` per axis; periodic grids use at most `2^20` cells per axis. Unsupported configurations fail visibly while CPU support remains available. Stored particle components retain every finite float32 bit, including signed zero and subnormal values, through exact capture/restore. Device arithmetic may flush subnormal intermediates; action replay is not claimed bitwise repeatable. Host time remains the recorded binary64 accumulation of the configured timestep.

`EditorSession.Run` declares capture requirements explicitly: unknown/structural operations default to capture-required, while known scalar/rule/presentation operations opt out. CPU-to-GPU transfer detaches the actual CPU editor at an ordered boundary, buffers later commands, and constructs the GPU owner before adoption. Failed construction reattaches the same editor/history. GPU-to-CPU transfer explicitly captures and widens actual float values; recovery after a device failure explicitly restores only the latest owned checkpoint and reports the discarded uncaptured continuation.

The canonical `particle-life-captured-session` schema 1 embeds the independently versioned action-session codec. Captures carry actual state, clock, configuration, applied-event count and hardware/precision/implementation provenance. Validation checks membership/species against recorded full replacements and identity deltas, including terminal same-boundary edits. It does not claim trajectory equivalence. Periodic checkpoints default to every 600 completed GPU steps and may be disabled; terminal capture remains mandatory when saving a captured session. Checkpoints use the shared lossless compressed snapshot codec and a 64 MiB retained budget, evicting optional periodic checkpoints with a visible notice. Transfer checkpoints are retained or the requested transfer is rejected.

The CLI separates `replay <capture> --restore-capture` from `--resimulate-on-cpu`; restoration widens actual values and retains the captured clock, while CPU re-simulation starts from the recorded initial conditions/actions and warns about GPU trajectory differences. No graphics library is required for either action.

Run the production correctness harness only with no competing benchmark/build/test/viewer:

```powershell
dotnet build src/ParticleLife.MonoGame/ParticleLife.MonoGame.csproj -c Release --no-restore
dotnet run --project src/ParticleLife.MonoGame -c Release --no-build -- --gpu-checks artifacts/implementation/p5-gpu-correctness-1
```

Use a fresh output directory. The harness closes automatically, has a 90-second watchdog, and emits `GPU_CORRECTNESS_PASS` only after all checks succeed. `gpu-correctness.json` records device provenance, per-case tolerances/errors, readback counters and exact state hashes. Small actual captures and `resident-framebuffer.png` provide reviewable evidence. The eighteen fixtures cover all boundary rules, directed ranges, dense buckets, negative coordinates/hash collisions, exact zero, tiny coefficients/cores, overlap/empty state, signed-zero/subnormal storage, and a 131,073-particle three-level scan with a stationary analytic oracle. An exact binary single-step fixture checks both negative periodic stencil axes with six cells per axis. Further checks cover exact upload/JSON restore, capture-free camera/radius rendering, cancellation after force submission, failed-model retention, structural IDs/clock transfer, and whole-candidate rejection for coordinate-range/non-finite failures. These are correctness checks, not GPU timings or a consolidated UI visual review.

The first production hardware run passed six numerical fixtures and all 96 expected resident framebuffer centres, then rejected the six-cell periodic scene against its unchanged CPU tolerance. Review identified an invalid signed-remainder expression when a stencil coordinate was `-1`: [GLSL section 5.9](https://registry.khronos.org/OpenGL/specs/gl/GLSLangSpec.4.60.html) leaves integer remainder with negative operands undefined. Periodic stencil wrapping now uses one conditional addition/subtraction, since its range is only `[-1, cellCount]`. The original tolerances remain unchanged, and failed comparisons now retain both actual and CPU-reference capture files. The failed first run is retained under `artifacts/implementation/p5-gpu-correctness-1/`.

### Production hardware result, 5 November 2026

The corrected harness passed all eighteen numerical/storage fixtures and four rendering/cancellation/failure checks on the NVIDIA GeForce RTX 5080 Laptop GPU, OpenGL 4.6 / NVIDIA 621.01, Windows 11 build 26300 and .NET 10.0.10. Evidence is in the P5 worktree's ignored `artifacts/implementation/p5-gpu-correctness-2/`. The tested native assembly SHA-256 is `0FCD2292F4210F8D82B643E410957FC9BB54B00B24BC24D1B2D762BB33A2E2D2`.

| Check | Observed result |
| --- | --- |
| Ordinary numerical loops | Zero full particle-state readbacks in every fixture. |
| Explicit device capture/restore | Exact stored values in all eighteen fixtures, including signed zero and subnormals. |
| Saved JSON capture round-trip | Exact in all seventeen small fixtures. The 131,073-particle scan fixture deliberately omits JSON serialization; its report's `SavedCaptureRoundTripExact: false` means unexecuted, not a comparison failure. |
| Periodic regression | Original six-cell scene maximum position error `3.234744846425386e-7`; additional binary-exact seam fixture error zero. |
| Dense 769-particle bucket, twelve steps | Maximum velocity error `1.3941524805133332e-5`, within the declared `2e-4` absolute plus `2e-3` relative tolerance. No occupancy limit or discarded neighbours. |
| 131,073-particle hierarchical scan | One stationary analytic step, exact positions/velocities. This checks three scan levels without claiming dense large-population throughput. |
| Shared-buffer rendering | All 96 expected particle centres and 2,276 coloured pixels observed; zero particle uploads, camera/radius uploads or full state readbacks during drawing. |
| Cancellation and failure | Submitted candidate cancellation, failed model replacement, coordinate-range rejection and non-finite rejection retain the last valid completed state. |

The saved framebuffer was inspected and contains the expected pink/teal grid and state-restoration markers. The report records 1.35 seconds of harness wall time and explicitly sets `PerformanceClaim: false`: this is correctness evidence, not a step, rendering or transfer benchmark. Production capacity measurements and the consolidated live CPU/GPU interaction review remain separate acceptance work.
