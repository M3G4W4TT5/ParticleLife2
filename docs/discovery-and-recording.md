# Discoveries and recordings

An edited session stores its actual initial state, ordered applied actions and final completed step. A local brush edit stores changed particle values and stable identities when that is smaller than a complete state. Replay applies those stored values at their recorded boundaries; pointer timing is not replayed. A change that reorders particles retains a complete state instead.

Undo checkpoints use lossless chunks of up to 4,096 particles. Each chunk stores exact double bits, stable identities and species, using Brotli compression only when it reduces size. Compression and restoration happen during editing, outside simulation steps. This reduces retained history, but an edit or explicit capture can still require temporary storage proportional to the population.

The advanced Discoveries controls show the estimated action payload, its limit, compressed undo usage and its budget. The defaults are 256 MiB for action payload and 128 MiB for undo. Action estimates include numerical payload and a small per-event allowance; they exclude the initial state, JSON expansion and some object overhead, so they are not file-size or process-memory guarantees.

Reaching the action limit rejects the next physical edit before it changes the state. Increasing the limit permits more edits. Reducing it below current usage preserves the complete recording and blocks further physical edits. Old undo entries may be evicted to meet the separate soft undo budget; the newest action and pending redo entries remain accessible even if they exceed that budget. Eviction does not delete recorded actions.

To begin a shorter recording, save the existing session and explicitly choose **New recording from here**. This retains the actual current particles and rules, pauses playback, records the source clock in origin metadata and starts a new baseline at step zero. It clears the previous action timeline and undo history.

Session format 3 includes compact particle deltas. Format 1 and 2 files retain their original meaning. Exact capture restoration and re-simulating recorded actions are separate operations: different execution backends, precision or hardware can produce different trajectories.

Native interaction acceptance is recorded in the [consolidated visual checklist](visual-review-checklist.md). Storage tests cover actual local brush deltas, replay, compressed undo, atomic limit rejection, undo eviction and explicit baseline restart.

## Measured 50k editing session

On 5 November 2026, a bounded storage screen used the P7 fixed-density 50,000-particle capture after 120 globally evolved steps, with CPU binary64 grid execution and 24 workers. Six strokes alternated Move and Directional force, each with 20 actually applied localized samples. Five numerical steps followed each of the first four strokes, giving 120 brush events and 20 completed steps. Every event stored an exact sparse delta, with changed identities counted independently. No event retained a complete replacement state.

| Retained data or artifact | Bytes |
| --- | ---: |
| One complete state's numerical payload | 2,200,000 |
| All 120 delta payloads | 367,048 |
| Accounted action payload including allowances | 444,808 |
| Compressed grouped undo checkpoints | 21,320,082 |
| Action-session JSON | 13,448,952 |
| Action-session `.plshare` | 2,502,002 |
| Captured-session JSON, including four actual stored boundaries | 62,433,054 |
| Captured-session `.plshare` | 11,515,121 |

For comparison, storing the numerical payload of 120 full 50k snapshots would require 264,000,000 bytes before JSON/object overhead. That is an arithmetic comparator, not a timed measurement of an earlier implementation. Undo and capture storage remain separate from action deltas. Captured restore accounted for 5,329,494 bytes across baseline, terminal and CPU-transfer checkpoints; the destination entry shares the immutable terminal payload internally, while the public budget conservatively counts both entries. Export appends its mandatory current terminal capture, so the saved captured file has four boundaries. This screen contains actual CPU provenance only.

Preparing the immutable action save took 2.08 ms on its owner; worker serialization, file publication and metadata took 301.54 ms. Captured save preparation took 1.32 ms, followed by 585.53 ms of worker work. Captured-load preparation, including decoding and checkpoint packing, took 435.55 ms on a worker; owner publication and CPU resource construction took 3.27 ms. Action replay setup took 42.04 ms on its owner after 505.38 ms of worker file preparation. These are single, unwarmed storage-operation measurements, not input-latency guarantees or solver timings.

The prepared action save retained its original 120 events after live grouped undo/redo advanced the active timeline to 122. Undo reversed the final 20-sample stroke in one action; redo, action replay and exact captured restore all recovered the same terminal values, order, identities, species and clock. Both share files preserved scientific and companion bytes exactly and retained their library metadata. The input file remained unchanged.

The source was the captured-discovery implementation committed as `a7fabdf`, on Windows build 26300/.NET 10.0.10, with no competing builds, tests, viewers or measurements. Input SHA-256: `0179CB8D76597C12F0D6E374641FAA79E2CA366535CDA513E226038410CC07F8`; viewer assembly: `986E39F8474F8B3BD8771B3FD175CE807A92C7082C383D986550B5EC872A88B1`. The local ignored harness/method is under `artifacts/implementation/p6-storage-tool/`; its completed process envelope, actual files, phase times and hashes are in `artifacts/implementation/p6-storage-results-01/`. The whole supervised run completed in about eight seconds within its 300-second deadline.
