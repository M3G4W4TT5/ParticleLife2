# Contributing to Particle Life 2

Particle Life 2 should be understandable enough to learn from and efficient enough to explore meaningful particle systems. Changes should make the model, numerical method and performance tradeoffs clear to the next contributor.

The current scope and milestone order are in the [architecture and migration plan](docs/architecture-and-migration.md). The repository is currently at the planning stage. Build, test, benchmark and documentation-generation commands will be added alongside the projects that implement them.

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

When the core project is created, enable [`GenerateDocumentationFile`](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props#generatedocumentationfile). Treat missing public XML comments and malformed or unresolved documentation references as build failures for authored library code. Compiler checks cannot judge the scientific content; review units, signs and assumptions explicitly.

Use [Docfx](https://dotnet.github.io/docfx/docs/dotnet-api-docs.html) as the proposed API renderer, with a pinned tool version when introduced. Generated API metadata and HTML are build artifacts. Handwritten Markdown is reserved for the project overview, architecture decisions, contribution instructions and experiment walkthroughs. A comment change and the corresponding code change belong in the same commit.

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

Remote setup will select a repository name, visibility and license and push the local history. Publishing is separate from generating local API documentation. Do not configure deployment or upload simulation results merely because a documentation generator or CI workflow supports it.
