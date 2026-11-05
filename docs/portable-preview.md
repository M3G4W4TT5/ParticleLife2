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

Extract to a writable location, open `Start-Viewer.cmd`, then press **H / F1** for the guide. Its default save folder is `discoveries/` beside the launcher. The viewer executable can also be launched directly with its normal arguments. Keep scientific files and matching `.viewer.json` companions together.

From a terminal in the extracted folder:

```powershell
.\Run-Cli.cmd --help
.\Run-Cli.cmd run examples/experiments/periodic-seam.json --steps 20 --sample-every 0 --output artifacts/seam-run.json
```

Use a new output filename for every run. The CPU-only CLI does not need a display. The viewer still requires a working native OpenGL driver, including when CPU simulation is selected; an included .NET runtime cannot supply a graphics driver. GPU execution additionally requires the integrated compute backend and compatible device/API. CPU simulation remains the supported fallback.

This preview targets Windows desktop first. Linux CI checks headless builds, numerical contracts, CLI operation and display-free native compatibility; those checks do not establish a native Linux window. Signing, an installer, automatic updates and release publication are outside this package.

Native launch, minimum-window input, branding/splash, CPU/GPU selection, saves and media must be checked from the extracted package during the [consolidated visual review](visual-review-checklist.md). Practical sparse/dense/evolved capacities require the separate matched workload reports. **Packaging verification and native acceptance are pending until those commands and review cases are actually run.**
