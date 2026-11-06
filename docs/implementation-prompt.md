# Particle Life 2 implementation prompt

Use this prompt in a fresh **GPT-6 Astra, Max reasoning** context with this repository as the workspace. The instructions below are the implementation assignment, not a request for another plan.

## Objective and scope

Implement **all of P1–P7** in `docs/architecture-and-migration.md`, including the integrated review and a pull request. **Do not implement P8 or any 3D infrastructure.** Continue working through the phases until the complete 2D update is ready for my review. Do not finish after a prototype, one phase, a planning document or a progress report, and do not ask whether to continue after each phase.

Workspace: `C:\Users\watts\Workspace\ChatGPT\Particle Life`.
Repository: `https://github.com/M3G4W4TT5/ParticleLife2`.

Start by reading `AGENTS.md`, `CONTRIBUTING.md`, the full architecture plan, and `docs/visual-review-checklist.md`. Read the existing performance and editor guides when relevant. Inspect the actual source and Git state; old status notes are not proof of the current checkout. The approved roadmap was committed as `6e807a3` on `agent/exploration-performance-roadmap`. Start from a tip containing that roadmap and this handoff, preserving subsequent fixes and user changes.

The roadmap is the detailed feature specification. Its implementation precedents and source links are already recorded; do not repeat the entire repository research. The phases cover:

1. P1: larger saved benchmark workloads, useful performance targets, and a bounded GPU/renderer interoperability prototype.
2. P2: responsive background CPU simulation, bulk rendering, worker selection, measured CPU/layout/arithmetic/neighbour-search improvements and clear performance controls.
3. P3: coherent controls, pan/zoom, matrix bulk editing and clipboard support, visual populations, independent generators, palettes and direct manipulation brushes.
4. P4: periodic and reflecting boundaries, directed per-pair core/cutoff ranges, their editor controls and versioned persistence.
5. P5: full GPU-resident 2D simulation and rendering, selectable CPU/GPU execution, deliberate state transfer, capture/checkpoints and device handling.
6. P6: the discovery library, selective presets, sharing by file, practical session storage, trails/glow/velocity colouring, tracking, screenshots and animation capture.
7. P7: large-population tuning, UX/accessibility polish, packaging, documentation, the consolidated review and the final PR.

Implement every committed feature. Where the roadmap says to evaluate an optimization, measure the promising candidates and retain worthwhile ones; an evidenced rejection of a candidate is a completed evaluation. Optional tools such as an API documentation website remain optional. Do not silently downgrade mandatory GPU or UX work to future work because it is difficult. One million particles is a stretch measurement, not a requirement to misrepresent performance or remove interactions.

## Product direction

This is a creative particle-life sandbox. Prioritize interesting emergence, many particles, responsive interaction and an enjoyable app. Single precision, measured approximations and artistic rules are welcome. Avoid turning the work into a scientific validation programme or a general-purpose framework.

Keep ordinary software correctness: consistent update stages, explicit buffer ownership, stable particle identities, sensible supported ranges, dependable saves and preservation of old file meanings. Keep CPU simulation fully supported alongside GPU, including CPU-only headless operation. Hardware choice, neighbour search and precision are separate concepts.

Retain reproducibility as a useful feature: actual starting/captured values, applied edits, settings and relevant versions. Preserve fixed simulation steps independently of render timing. Distinguish restoring a recorded capture from re-simulating recorded actions; CPU/GPU trajectories and nondeterministic GPU replay need not match exactly. Do not silently change rules, precision, density or population to meet a performance target.

## Lead agent and delegation

You are the Astra Max lead. Own architecture, integration, prioritization and completion, and **use sub-agents** for bounded work. Use this routing policy:

| Work | Model | Reasoning |
| --- | --- | --- |
| Routine test/build commands, logs, prescribed Git operations and pushes, file/checklist checks | GPT-6.1 Sol (`gpt-6.1-sol`) | `low`; `medium` when procedure or diagnosis needs it |
| Prescribed visual inspection, computer use and other step-by-step verification | GPT-6.1 Sol (`gpt-6.1-sol`) | `low` or `medium` |
| Ordinary coding and focused code review | GPT-6.1 Sol (`gpt-6.1-sol`) | `high` |
| Difficult coding, concurrency, persistence or numerical optimization within an established design | GPT-6.1 Sol (`gpt-6.1-sol`) | `xhigh` (Extra High) |
| Very complex design/coding, especially GPU simulation, synchronization, memory layout and compute/render interoperability | GPT-6 Astra (`gpt-6-astra`) | An appropriate level such as `high`, `xhigh` or `max` |

Do not use Astra sub-agents for routine tests, Git operations or prescribed visual checks. Escalate the reasoning task when a routine worker finds a difficult issue; keep the mechanical execution on Sol. Use the requested model families and explain any unavailable capability rather than silently substituting another family.

When model/reasoning overrides require it, spawn with `fork_turns: "none"` and supply a self-contained brief: workspace/worktree, branch, task, relevant files and decisions, file ownership, constraints, required verification and expected return evidence. Do not rely on unshared parent history.

Coordinate file ownership and respect the available concurrency limit. Parallelize genuinely independent work. All agents must understand whether they share a checkout; never let a Git worker change branches while another agent is editing or running a command there. Use isolated worktrees for parallel writers where useful. Review and integrate delegated changes rather than accepting a worker's completion claim without checking its result.

## Verification with minimal waiting

Use tests and benchmarks **only when necessary to establish correctness, resolve uncertainty or substantiate a performance decision**. This instruction controls the timing of checks in this implementation; the contributor commands still define the required final verification.

- Reuse applicable existing results for an unchanged revision/configuration. Do not start by rerunning every historical benchmark or test suite.
- During development, choose the smallest relevant build/test set after meaningful changes. A phase merge needs proportionate evidence, not another automatic full-solution test run.
- Add targeted tests for new behavioural contracts and meaningful regressions. Avoid tests that mirror implementation or add little confidence.
- Benchmark only changed performance-sensitive paths or unresolved design choices. Use matched saved inputs, short screening runs and bounded stress cases; broaden only when a decision needs it. Do not run benchmarks alongside builds, tests, native viewers or other competing CPU/GPU work on the same machine.
- Delegate required test execution to Sol low/medium with exact commands, revision and expected evidence. It can run independently while you do non-conflicting work; never attribute results to code changed during that run. Queue repository-mutating checks, including XML probes, away from concurrent edits.
- Once P1–P7 are integrated and feature-complete, run the applicable contributor gates on the final implementation. Reuse current successful checks; after fixes rerun affected checks and only broaden when the change or a failure warrants it. Do not skip required checks or claim unrun checks passed.

### Consolidated visual review

Maintain `docs/visual-review-checklist.md` throughout implementation. Add concrete cases as features land: setup/fixture, backend/device, actions, expected result and applicable window sizes. Keep unexecuted cases marked pending. Store screenshots, logs and generated captures under ignored `artifacts/`, with concise results in the checklist.

Do **one consolidated end-to-end visual/interaction review after all P1–P7 feature work is integrated**. Delegate its prescribed cases to Sol low/medium using computer-use tools and the applicable skills. Cover CPU and GPU, large populations, resizing/input, new controls, saves/replay, switching, errors and capture. Fix discovered issues, then repeat only the affected cases and necessary regressions. Record observed outcomes rather than inferring UI success from passing unit tests.

Do not repeatedly launch the app for opportunistic visual reviews during development. An earlier minimal hardware/rendering or input smoke check is justified only when needed to resolve a real implementation blocker, such as P1 GPU buffer interoperability; keep it narrowly scoped and record why it was necessary. Phase status may explicitly say visual acceptance is deferred to the consolidated review; it must not say those cases already passed.

If a physical key, GPU condition or user-controlled desktop interaction cannot be verified through tools, ask me for the specific help needed and keep that case visibly unresolved. Continue other work while waiting.

## Branches and integration

Use a local primary update branch named **`agent/particle-life-2-update`**. Ensure its starting point includes current `main` fixes and the approved roadmap/handoff. Reuse an existing branch if it is already the correct update branch; inspect before creating, merging or renaming anything.

Create a separate topic branch for each phase, for example:

- `agent/p1-performance-foundation`
- `agent/p2-cpu-and-rendering`
- `agent/p3-exploration-tools`
- `agent/p4-boundaries-and-ranges`
- `agent/p5-gpu-simulation`
- `agent/p6-discovery-and-visuals`
- `agent/p7-scale-and-release`

Branch each phase from the latest appropriate integration state. Make cohesive commits with relevant documentation and focused verification. When a phase's implementation and relevant automated checks are complete, merge it **locally into `agent/particle-life-2-update`**, preferably retaining the phase boundary with a merge commit. Record any end-of-project visual checks still pending. Resolve integration conflicts and continue to the next phase without asking for permission to proceed. If independent phase work overlaps, integrate prerequisite changes deliberately rather than working against stale assumptions.

Do final review fixes on a short-lived topic branch and integrate them into the same update branch. Do not implement directly on `main`. Preserve unrelated user work and do not reset or force-push it. Keep generated binaries, media and benchmark output out of source commits; put necessary summaries and reproducible fixtures in their documented locations.

At the end, **push the primary update branch and create a PR into `main`** in the existing repository. This instruction authorizes that push and PR creation. Do not create a separate user-review PR for every phase unless needed for an external workflow; phase integration is local. The final PR must describe the final features, practical CPU/GPU performance, verification, replay behaviour and material limitations. Inspect required CI checks, fix failures caused by this work and update the PR as needed.

**Stop before merging the primary update branch into `main`, locally or remotely. Do not enable auto-merge. I will review the final PR and decide when to merge it.** Do not publish a release as part of this assignment.

## Questions, continuity and finish condition

You may ask me questions whenever needed, including design preferences, genuine ambiguity, missing access or a decision that materially changes the scope. Ask early enough to avoid wasted work, preferably asynchronously, and continue independent work while waiting. Use reasonable judgement for routine implementation choices; do not introduce repeated phase-approval gates.

Keep concise progress updates and maintain the roadmap's phase status and decisions. Keep commands in the maintained contributor/architecture documentation. Preserve a compact continuation record under ignored `artifacts/implementation/` with branch/worktree ownership, completed work, pending work, relevant check results and open questions, so compaction or a resumed context does not restart the project. Resume from the recorded state and verify the checkout rather than repeating completed work.

If persistent goal tools are available, create a goal for this complete P1–P7 assignment through the final reviewable PR, without adding a self-imposed token budget. Keep that goal active across phase boundaries and follow the tool's actual completion/blocking rules.

Continue until P1–P7 are implemented and integrated, required verification and the consolidated visual review are complete, and the final reviewable PR exists. A difficult phase or an exhausted context is not completion. If truly blocked by access, hardware or a required user decision, state the precise blocker, ask for what is needed and continue any independent work; never mark incomplete work as done. If a blocker prevents full completion, keep any PR explicitly draft and identify the unmet criteria.

Your final response should link the PR, summarize what shipped and the measured performance, link the completed visual checklist, and identify any remaining material limitations. Confirm that `main` was not merged and that 3D remains deferred. Begin implementation now.
