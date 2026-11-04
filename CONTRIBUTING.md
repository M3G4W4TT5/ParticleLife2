# Contributing to Particle Life 2

Particle Life 2 should be understandable enough to learn from and efficient enough to explore meaningful particle systems. Changes should make the model, numerical method and performance tradeoffs clear to the next contributor.

The current scope and milestone order are in the [architecture and migration plan](docs/architecture-and-migration.md). M1–M3 provide the tested headless reference implementation and interactive viewer. Spatial acceleration is M4.

## Build and verification

Use .NET SDK **10.0.302**, pinned without roll-forward in `global.json`. Projects target `net10.0` with C# 14, nullable references, SDK analyzers and warnings as errors. This is the installed, verified SDK baseline, not a claim to use the newest servicing patch. Review SDK updates deliberately and rerun these gates. Direct dependencies are pinned to xUnit v3 **4.0.2**, MonoGame DesktopGL **3.8.6** and BenchmarkDotNet **0.15.8**; tracked lock files pin transitive resolution. The core and CLI have no package dependencies.

Run from the repository root:

```sh
dotnet restore ParticleLife.slnx --locked-mode
dotnet format ParticleLife.slnx --verify-no-changes --no-restore
dotnet build ParticleLife.slnx -c Release --no-restore
dotnet test --solution ParticleLife.slnx -c Release --no-build
dotnet run --project src/ParticleLife.MonoGame -c Release --no-build -- --help
dotnet run --project tests/ParticleLife.MonoGame.Compatibility -c Release --no-build
pwsh -NoProfile -File scripts/check-xml-docs.ps1
pwsh -NoProfile -File scripts/check-cli.ps1
```

The test runner uses Microsoft Testing Platform selected in `global.json`; the explicit `--solution` option is required by that mode. Use `dotnet format ParticleLife.slnx --no-restore` to apply formatting. After intentionally changing dependencies, restore without `--locked-mode` and review the lock-file diff. PowerShell 7 is needed for the two portable verification scripts. CI runs these checks on Linux; local baseline measurements are on Windows.

The solution build includes the MonoGame viewer. Host tests exercise its actual graphics-free playback controller, experiment editing, safe saving and isotropic coordinate mapping. Seven parity cases save experiments, invoke the real CLI and compare its output with the viewer controller after 120 completed steps, matching stable identities with `1e-12` absolute plus `1e-12` relative tolerances in reduced units. They include the supplied fixture, all three presets, state resampling, a matrix change and an off-world case with a different timestep. These short cases allow tiny floating-point variation without claiming cross-runtime bitwise identity.

The `--help` command and MonoGame compatibility check do not create a graphics device or window. They can run on Linux CI alongside the numerical and host tests. Native graphics startup is verified separately on Windows; the Linux native window has not been verified.

The core and graphics-free viewer library generate XML documentation in their build output directories. The XML gate temporarily adds malformed/missing documentation probes to the core, checks that CS1591, CS1570 and CS1574 are errors, removes them in a `finally` block, and rebuilds the unmodified core. Generated API website publishing remains M5 work.

For timing commands and their measurement boundaries, use the [reference benchmark protocol and results](docs/performance-baseline.md). Performance tests check zero allocations for warmed successful steps; diagnostics, serialization and snapshots are intentionally outside that contract.

### Interactive verification

After the Release build, launch the actual native window:

```sh
dotnet run --project src/ParticleLife.MonoGame -c Release --no-build
dotnet run --project src/ParticleLife.MonoGame -c Release --no-build -- examples/experiments/two-species.json --paused
```

Use separate launches for the default population and saved fixture. Check the following before reporting an interactive change verified:

1. Inspect particle rendering, species colours and readable diagnostics. Resize to wide and tall shapes; the world must retain its aspect ratio and particle count. The viewer requests at least 420 × 340 pixels for its controls.
2. Exercise keyboard commands and clickable help rows. Pause, wait, resume, and single-step; verify there is no resume burst and single-step advances once. Lose and regain focus; playback must stay paused until explicitly resumed.
3. Reset after advancing. Confirm time/step zero and restored initial positions. Compare `I`, `M` and preset selection: they must retain the different parts of the experiment documented in the README.
4. Save twice and verify distinct files. Launch a saved file as a startup argument and reload it with `F5`. Try a missing or invalid file and a CLI run report; show an error while retaining a valid experiment. A failed startup load retains the generated fallback paused; a failed reload retains the current experiment paused.
5. Observe a scheduled measurement and its simulation time. Reset or replace the experiment and confirm the measurement clears. Toggle diagnostics with `D`; measurements must not change the simulation's equations or step results.
6. Check responsiveness with the default 500 particles. Use `F9` to record the latest approximately one-second FPS and controller-update timings, the step, and dropped playback time. Controller timing includes scheduled diagnostics and excludes rendering/present; it is not a pure solver benchmark. Record workload, runtime, machine and limitations with any performance claim.

The catch-up limit bounds steps per frame, not the cost of one all-pairs step or diagnostic. A large population can still delay input. Test native startup and controls on the target platform; passing the display-free checks alone does not establish that result.

## Readable numerical code

Keep equations in small named functions with explicit inputs. Use classes for state ownership and lifecycle, and small value types for numerical data. Give an interface a concrete purpose, such as selecting between the direct reference search and the grid search. Use composition where compatible terms contribute to the same physical quantity.

Use names that distinguish distance from squared distance, force from acceleration, and potential from force. Mathematical symbols are appropriate in a short equation when their meaning and units are documented. Explain derivations and assumptions; avoid comments that merely translate an assignment into English.

Prefer ordinary loops and array access in numerical code. Keep allocations, logging and formatting outside repeated particle/pair evaluation. Validate configuration at the boundary, establish invariants once, and retain meaningful checks for non-finite results. Any unsafe code, unusual bit manipulation, specialized dispatch or approximate arithmetic needs a measured benefit, a readable explanation and a reference comparison.

Follow `.editorconfig`: four spaces and Allman braces for C#, with descriptive names and a consistent namespace layout. Enable nullable reference types and standard SDK analyzers when projects are created. Do not suppress warnings broadly to make a build pass.

## XML documentation is the API source

Document public types and members using C# XML comments. Document important internal numerical algorithms as well, especially where a derivation, approximation or ownership contract would otherwise be obscure. Generated API pages should come from these comments, not a parallel set of handwritten descriptions for every class and method.

Use the relevant tags from Microsoft's [XML documentation guidance](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/xmldoc/recommended-tags):

- `<summary>` explains the operation and its purpose in complete sentences.
- `<param>`, `<typeparam>` and `<returns>` state quantities, units, sign conventions, valid ranges and output meaning.
- `<remarks>` records equations, assumptions, computational cost, buffer ownership, mutation, allocation and concurrency constraints where relevant.
- `<exception>` documents meaningful invalid-input or numerical-failure cases.
- `<example>` demonstrates a useful operation or interpretable scientific case.
- `<see cref="...">` and `<seealso>` connect related code. Use source links in remarks for papers and derivations, identifying the relevant equation or section.

For example, the documentation for a radial coefficient function should make clear that it returns a dimensionless coefficient rather than a force. An illustrative comment could read:

```csharp
/// <summary>
/// Evaluates the radial coefficient for the Particle Life interaction.
/// </summary>
/// <param name="distanceRatio">
/// Particle separation divided by the positive interaction cutoff.
/// </param>
/// <param name="attraction">
/// Directed species coefficient in the interval [-1, 1].
/// </param>
/// <returns>
/// A dimensionless coefficient. Positive values attract; negative values repel.
/// </returns>
/// <remarks>
/// <para>
/// The repulsive core is independent of species. The coefficient is zero at
/// and beyond the cutoff. Zero-separation direction is handled by the caller.
/// </para>
/// <para>
/// Evaluation does not mutate simulation state or allocate managed memory.
/// </para>
/// </remarks>
```

This illustrates documentation style, not an already implemented API. The actual method's comments must also state its exact curve, repulsive-core parameter and preconditions. Numerical claims in comments must agree with tests and measurements.

The core enables [`GenerateDocumentationFile`](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props#generatedocumentationfile). Missing public XML comments and malformed or unresolved documentation references fail the authored library build. Compiler checks cannot judge the scientific content; review units, signs and assumptions explicitly.

Use [Docfx](https://dotnet.github.io/docfx/docs/dotnet-api-docs.html) as the proposed API renderer, with a pinned tool version when introduced. Generated API metadata and HTML are build artifacts. Handwritten Markdown is reserved for the project overview, architecture decisions, contribution instructions and experiment walkthroughs. A comment change and the corresponding code change belong in the same commit.

## Project README

Keep the README concise and user-facing, in the project's author's voice. It should explain the project, its current status and, when available, how to run and explore it. Follow the direct, explanatory style of the original Particle Life README.

Keep agent work logs, internal reasoning and detailed migration discussion out of the README. Put architecture decisions in the plan and implementation details in C# XML comments. Do not add links to private research repositories to the README.

## Correctness and performance evidence

Use deterministic small cases to test equations and state transitions. Compare optimized neighbour sets and force evaluations with the direct reference implementation. Express tolerances with their units or scales and the reason they are appropriate. Conservation tests must match the model's assumptions.

Use the benchmark protocol in the plan for numerical optimizations. Report the workload and environment, before/after timings, allocation behaviour and numerical error. Profile before introducing specialized complexity. A change that increases the simulation's density or alters a cutoff is a changed workload, not evidence of a regression in the same workload.

Run focused correctness tests and the normal build checks for each relevant change. Run affected benchmarks when changing performance-sensitive code. Shared CI timing is informational until a stable baseline exists. Documentation-only changes need link, formatting and content checks; they do not need invented simulation tests.

Do not promise bitwise identity across runtimes or parallel backends unless that guarantee has been deliberately designed and verified. Preserve experiment metadata and initial conditions so differences can be investigated.

## Git and delivery workflow

Make frequent, cohesive commits. A useful commit completes a reviewable step with the tests and XML documentation needed to understand it. Separate model changes, numerical fixes and optimizations when practical. Avoid committing arbitrary broken intermediate states to `main`.

Use short-lived topic branches for implementation work. Before a commit, inspect the diff, run the relevant checks, and stage only intended files. Write a concise imperative subject explaining the change; examples include `fix: evaluate all forces from a consistent state` and `perf: reuse neighbour-search buffers`. A commit body can record a model consequence or a benchmark result that needs context.

Keep SDK versions, dependency versions and local tool versions reproducible once the build exists. A clean checkout should be sufficient to restore, build, test and generate documentation using the documented commands. Generated binaries, test output, benchmark working files, tool caches, downloaded papers and historical source snapshots stay out of ordinary source commits. Small curated experiment fixtures and comparison summaries can be tracked intentionally.

The existing `review/` directory contains local historical analysis and is ignored. Do not use it as an implicit dependency of production projects or maintained tests. Bring required regression cases into the new test project with clear provenance.

The GitHub remote is [M3G4W4TT5/ParticleLife2](https://github.com/M3G4W4TT5/ParticleLife2), currently private. Publishing is separate from generating local API documentation. Do not configure deployment or upload simulation results merely because a documentation generator or CI workflow supports it.
