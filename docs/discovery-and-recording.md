# Discoveries and recordings

An edited session stores its actual initial state, ordered applied actions and final completed step. A local brush edit stores changed particle values and stable identities when that is smaller than a complete state. Replay applies those stored values at their recorded boundaries; pointer timing is not replayed. A change that reorders particles retains a complete state instead.

Undo checkpoints use lossless chunks of up to 4,096 particles. Each chunk stores exact double bits, stable identities and species, using Brotli compression only when it reduces size. Compression and restoration happen during editing, outside simulation steps. This reduces retained history, but an edit or explicit capture can still require temporary storage proportional to the population.

The advanced Discoveries controls show the estimated action payload, its limit, compressed undo usage and its budget. The defaults are 256 MiB for action payload and 128 MiB for undo. Action estimates include numerical payload and a small per-event allowance; they exclude the initial state, JSON expansion and some object overhead, so they are not file-size or process-memory guarantees.

Reaching the action limit rejects the next physical edit before it changes the state. Increasing the limit permits more edits. Reducing it below current usage preserves the complete recording and blocks further physical edits. Old undo entries may be evicted to meet the separate soft undo budget; the newest action and pending redo entries remain accessible even if they exceed that budget. Eviction does not delete recorded actions.

To begin a shorter recording, save the existing session and explicitly choose **New recording from here**. This retains the actual current particles and rules, pauses playback, records the source clock in origin metadata and starts a new baseline at step zero. It clears the previous action timeline and undo history.

Session format 3 includes compact particle deltas. Format 1 and 2 files retain their original meaning. Exact capture restoration and re-simulating recorded actions are separate operations: different execution backends, precision or hardware can produce different trajectories.

Native interaction acceptance is recorded in the [consolidated visual checklist](visual-review-checklist.md). Storage tests cover actual local brush deltas, replay, compressed undo, atomic limit rejection, undo eviction and explicit baseline restart.
