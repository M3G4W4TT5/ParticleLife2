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

Native launch, minimum-window input, branding/splash, CPU/GPU selection, saves and media must be checked from the extracted package during the [consolidated visual review](visual-review-checklist.md). Practical sparse/dense/evolved capacities require the separate matched workload reports. **Packaging verification is complete for the current Preview 07 payload; native acceptance remains in progress.**

## Reviewed package evidence

Preview 07 was built from clean `6536e981` with SDK **10.0.302** and the self-contained Windows x64 runtime **10.0.10**. Its `-SmokeTest` checks passed. Independent verification matched size and SHA-256 for all **448 manifest-listed files** (449 files including the manifest). The ZIP is **78,605,437 bytes**, SHA-256 `D46BB91B306E792063D50D8D009B38AFB0949221808EB3A5BE9DA5753CFD09F7`. Local package output is under ignored `artifacts/packages/p7-review-07/`.

Preview 07 includes the focus and visible timing-feedback fix; its focused native recheck passes. Held physical pan and V14 acceptance remain pending. Package smokes do not open a native window. Earlier package observations remain attributable to their tested source.

The actual `Start-Viewer.cmd --paused` launch succeeded from `artifacts/implementation/visual-review/Private Preview 02/ParticleLife2`, a writable path containing spaces. The default discovery folder was beside the launcher. Default CPU particles, one paused step and Reset were visibly correct. These observations cover the launcher and focused renderer recheck; remaining startup, controls and practical performance coverage is recorded in the checklist. Those launcher observations concern Preview 02. Preview 03 native CPU appearance and CPU/GPU capture rechecks passed as detailed in the checklist. Preview 06 also includes visible timing controls and capture error routing (`b70089e`). Its actual normal-desktop launcher starts paused with all 500 default CPU particles visible. Its packaged CLI help and four-step periodic run pass (count 4, time 0.008); repeating the output destination rejects with exit 1 and leaves the original file hash unchanged. Physical editing and capture lifecycle bundles also pass, supported by exact CPU-session replay and independently decoded media. Remaining native cases, including GPU/dense V14 windows and the unresolved CPU physical-input assessment, are in progress.
