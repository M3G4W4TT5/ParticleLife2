# Windows private preview package

The packaging script creates an unsigned, self-contained **Windows x64 private preview**. It does not install anything, sign a binary, publish a release or upload files. The desktop viewer and CPU-only CLI have separate runtime folders; keep the extracted folder intact.

## Build from a clean checkout

Use Windows, PowerShell 7.2 or later and the exact .NET SDK in `global.json`. Git and access to the configured NuGet feeds are required while building. The packaged applications carry their own runtime, so the recipient does not need the SDK.

```powershell
git clone https://github.com/M3G4W4TT5/ParticleLife2.git
Set-Location ParticleLife2
dotnet restore ParticleLife.slnx --locked-mode
dotnet build ParticleLife.slnx -c Release --no-restore
pwsh -NoProfile -File scripts/package-windows-preview.ps1 -SmokeTest
```

Build the intended reviewed branch. The script also accepts `-OutputDirectory artifacts/packages/my-private-preview` and `-NoArchive`; the directory must not exist. Each default output uses a new timestamp/commit/random suffix. No recursive deletion or replacement of old output is performed. Run builds and smokes outside a performance-measurement window.

The script snapshots Git-visible application source, project files and assets into `build/source` under the new output. Ignored artifacts, `.git`, `bin`, `obj` and local historical review material are excluded. Framework restore is locked. Adding the `win-x64` runtime target may extend **copied** lock files in staging; any change to an existing resolved package version or content hash fails packaging. Source lock files remain untouched. Publishing uses Release, self-contained runtime folders without trimming or single-file extraction.

## Contents and provenance

`ParticleLife2/` contains:

- `Start-Viewer.cmd`, `Run-Cli.cmd` and `START-HERE.txt`.
- `viewer/` and `cli/`, each with its actual self-contained runtime and required native/runtime assets.
- Small `examples/`, the README and maintained guides. The implementation assignment is excluded.
- `licenses/`, including the IBM Plex Sans SIL Open Font License and available package license metadata/notices. Runtime-provided notices remain with their runtime files.
- `build-info/` with the resolved publish lock files, and `manifest.json`.

The manifest records the commit, dirty-tree status, exact source/document/example hashes, SDK, runtime identifier, included runtime configuration, pinned package identities/content hashes and the SHA-256/size of each payload file. The manifest excludes its own hash to avoid a circular definition. A ZIP is created unless `-NoArchive` is supplied, with a separate ZIP SHA-256 sidecar. Build/restore/smoke logs and source staging stay outside the ZIP under `build/`.

`-SmokeTest` runs only the published viewer `--help`, CPU CLI `--help`, validation of the periodic example and a two-step CPU run. These commands create no native window or GPU context. Success records those specific checks in the manifest; it does not establish native package acceptance, GPU availability or capacity.

## Use the extracted folder

Extract to a writable location, open `Start-Viewer.cmd`, then press **H / F1** for the guide. Its default save folder is `discoveries/` beside the launcher. The viewer executable can also be launched directly with its normal arguments. Without `--save-dir`, a direct executable launch uses `artifacts/viewer` relative to the caller's working directory; use the launcher for the package-local default. Keep scientific files and matching `.viewer.json` companions together.

From a terminal in the extracted folder:

```powershell
.\Run-Cli.cmd --help
.\Run-Cli.cmd run examples/experiments/periodic-seam.json --steps 20 --sample-every 0 --output artifacts/seam-run.json
```

Use a new output filename for every run. The CPU-only CLI does not need a display. The viewer still requires a working native OpenGL driver, including when CPU simulation is selected; an included .NET runtime cannot supply a graphics driver. GPU execution additionally requires the integrated compute backend and compatible device/API. CPU simulation remains the supported fallback.

This preview targets Windows desktop first. Linux CI checks headless builds, numerical contracts, CLI operation and display-free native compatibility; those checks do not establish a native Linux window. Signing, an installer, automatic updates and release publication are outside this package.

Native launch, minimum-window input, branding/splash, CPU/GPU selection, saves and media are covered by the [consolidated visual review](visual-review-checklist.md), which identifies completed observations and coverage limits. Practical sparse/dense/evolved capacities require the separate matched workload reports. **Preview 10 packaging verification and the representative native review are complete.**

## Reviewed package evidence

Preview 10 was built from clean `b175957`; four headless smokes and all 448 manifest payload hashes and sizes passed (449 files including the manifest). Its ZIP is 78,627,326 bytes, SHA-256 `E09FBD53E19B0DFA24F558A457EA55E37F4FA644372D19CDC943BC7C1B597FD1`. The latest native rechecks used Preview 09; Preview 10 contains documentation changes and was not natively reviewed.

Preview 09 was built from clean `1078549`; four headless smokes passed and all 448 manifest payload hashes were verified. Its native boundary and preset Undo/Redo rechecks pass with exact saved replays. Package smokes do not open a native window.

Preview 08 was built from clean `a45d038c` with SDK **10.0.302** and the self-contained Windows x64 runtime **10.0.10**. Its `-SmokeTest` checks passed. Independent verification matched size and SHA-256 for all **448 manifest-listed files** (449 files including the manifest, before review inputs were staged). The ZIP is **78,617,821 bytes**, SHA-256 `4E0C1AFAE1D35F37D8A0BEB09F0925C563894B6D8B879A31F54ED0F4030FC145`. Local package output is under ignored `artifacts/packages/p7-review-08/`.

Preview 08 includes the publication-identity fix for same-count/clock CPU owner replacement. Its focused native recheck shows clustered geometry correctly after GPU→CPU transfer and repeated switching; startup saved-file precedence with `--species 50000` also passes. Preview 07 focused input/visible-feedback observations remain attributable to that package. Preview 08 shared pan/focus/Print behavior is user-confirmed; CPU50k and GPU100k physical running-pan results are scoped in the checklist/performance report; later representative dense/live-edit actions are recorded in the checklist. Package smokes do not open a native window. Earlier package observations remain attributable to their tested source.

The actual `Start-Viewer.cmd --paused` launch succeeded from `artifacts/implementation/visual-review/Private Preview 02/ParticleLife2`, a writable path containing spaces. The default discovery folder was beside the launcher. Default CPU particles, one paused step and Reset were visibly correct. These observations cover the launcher and focused renderer recheck; remaining startup, controls and practical performance coverage is recorded in the checklist. Those launcher observations concern Preview 02. Preview 03 native CPU appearance and CPU/GPU capture rechecks passed as detailed in the checklist. Preview 06 also includes visible timing controls and capture error routing (`b70089e`). Its actual normal-desktop launcher starts paused with all 500 default CPU particles visible. Its packaged CLI help and four-step periodic run pass (count 4, time 0.008); repeating the output destination rejects with exit 1 and leaves the original file hash unchanged. Physical editing and capture lifecycle bundles also pass, supported by exact CPU-session replay and independently decoded media. Later representative dense/live-edit coverage and the final physical batch and its limits are recorded in the checklist.
