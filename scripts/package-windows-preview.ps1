#requires -Version 7.2
<#
.SYNOPSIS
Creates a fresh, unsigned Windows x64 private preview with its own .NET runtime.
.DESCRIPTION
Publishes a source snapshot under ignored artifacts, never rewriting source lock files or deleting
an existing output. Framework restore is locked. RID restore occurs only in the snapshot and is
rejected if any existing package version or content hash changes. No native window is launched.
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [switch]$NoArchive,
    [switch]$SmokeTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'This private preview script targets a Windows build host and win-x64.' }
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$commit = (& git -C $repository rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the source checkout.' }
$checkoutStatus = @(& git -C $repository status --porcelain)
if ($LASTEXITCODE -ne 0) { throw 'Cannot capture checkout status.' }
$pinnedSdk = (Get-Content -LiteralPath (Join-Path $repository 'global.json') -Raw | ConvertFrom-Json).sdk.version
Push-Location $repository
try { $sdk = (& dotnet --version).Trim(); if ($LASTEXITCODE -ne 0) { throw 'Cannot read the pinned SDK.' } }
finally { Pop-Location }
if ($sdk -ne $pinnedSdk) { throw "Expected SDK $pinnedSdk, found $sdk." }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = 'artifacts/packages/preview-win-x64-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + $commit.Substring(0, 8) + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory, $repository)
if (Test-Path -LiteralPath $outputRoot) { throw "Output already exists; choose a fresh directory: $outputRoot" }
$payload = Join-Path $outputRoot 'ParticleLife2'
$source = Join-Path $outputRoot 'build/source'
$logs = Join-Path $outputRoot 'build/logs'
New-Item -ItemType Directory -Path $payload, $source, $logs | Out-Null

function Copy-Input([string]$Relative, [string]$DestinationRoot) {
    $destination = Join-Path $DestinationRoot $Relative
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repository $Relative) -Destination $destination
}

function Describe-File([string]$Path, [string]$Root) {
    [ordered]@{
        path = [IO.Path]::GetRelativePath($Root, $Path).Replace('\', '/')
        bytes = (Get-Item -LiteralPath $Path).Length
        sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    }
}

function Invoke-Dotnet([string[]]$Arguments, [string]$LogName) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $logs $LogName) | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit $LASTEXITCODE. See build/logs/$LogName." }
}

function Read-Pins([string]$LockPath) {
    $data = Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json -AsHashtable
    $pins = @{}
    foreach ($framework in $data['dependencies'].Values) {
        foreach ($id in $framework.Keys) {
            $entry = $framework[$id]
            if ($entry['type'] -ne 'Project') {
                $key = $id.ToLowerInvariant()
                $pin = [ordered]@{ version = $entry['resolved']; contentHash = $entry['contentHash'] }
                if ($pins.ContainsKey($key) -and ($pins[$key]['version'] -ne $pin['version'] -or $pins[$key]['contentHash'] -ne $pin['contentHash'])) {
                    throw "A lock file contains conflicting versions for $id. Review packaging support before proceeding."
                }
                $pins[$key] = $pin
            }
        }
    }
    return $pins
}

$sourceSelectors = @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'global.json',
    'nuget.config', 'NuGet.Config', 'scripts/package-windows-preview.ps1',
    'src/ParticleLife.Core', 'src/ParticleLife.Viewer', 'src/ParticleLife.MonoGame', 'src/ParticleLife.Cli')
$sourcePaths = @(& git -C $repository -c core.quotepath=false ls-files --cached --others --exclude-standard -- @sourceSelectors)
if ($LASTEXITCODE -ne 0 -or $sourcePaths.Count -eq 0) { throw 'Cannot enumerate source inputs.' }
$sourcePaths = @($sourcePaths | Where-Object { $_ -notmatch '(^|[/\\])(\.git|artifacts|bin|obj|review)([/\\]|$)' })
foreach ($relative in $sourcePaths) { Copy-Input $relative $source }
$sourceInputs = @($sourcePaths | Sort-Object | ForEach-Object { Describe-File (Join-Path $source $_) $source })
$locks = @(Get-ChildItem -LiteralPath (Join-Path $source 'src') -Filter packages.lock.json -File -Recurse)
$originalPins = @{}
foreach ($lock in $locks) { $originalPins[$lock.FullName] = Read-Pins $lock.FullName }

Push-Location $source
try {
    $viewerProject = 'src/ParticleLife.MonoGame/ParticleLife.MonoGame.csproj'
    $cliProject = 'src/ParticleLife.Cli/ParticleLife.Cli.csproj'
    Invoke-Dotnet -Arguments @('restore', $viewerProject, '--locked-mode') -LogName 'restore-viewer-framework.txt'
    Invoke-Dotnet -Arguments @('restore', $cliProject, '--locked-mode') -LogName 'restore-cli-framework.txt'
    # Runtime targets extend the copied locks only. Tracked package pins below must remain unchanged.
    Invoke-Dotnet -Arguments @('restore', $viewerProject, '--runtime', 'win-x64', '-p:SelfContained=true', '-p:RestoreLockedMode=false') -LogName 'restore-viewer-win-x64.txt'
    Invoke-Dotnet -Arguments @('restore', $cliProject, '--runtime', 'win-x64', '-p:SelfContained=true', '-p:RestoreLockedMode=false') -LogName 'restore-cli-win-x64.txt'
    foreach ($lock in $locks) {
        $after = Read-Pins $lock.FullName
        foreach ($id in $originalPins[$lock.FullName].Keys) {
            $before = $originalPins[$lock.FullName][$id]
            if (-not $after.ContainsKey($id) -or $after[$id]['version'] -ne $before['version'] -or $after[$id]['contentHash'] -ne $before['contentHash']) {
                throw "RID restore changed the pinned package $id. No package will be published."
            }
        }
    }
    $publishOptions = @('-c', 'Release', '--runtime', 'win-x64', '--self-contained', 'true', '--no-restore',
        '-p:PublishTrimmed=false', '-p:PublishSingleFile=false', "-p:SourceRevisionId=$commit")
    Invoke-Dotnet -Arguments (@('publish', $viewerProject) + $publishOptions + @('--output', (Join-Path $payload 'viewer'))) -LogName 'publish-viewer.txt'
    Invoke-Dotnet -Arguments (@('publish', $cliProject) + $publishOptions + @('--output', (Join-Path $payload 'cli'))) -LogName 'publish-cli.txt'
}
finally { Pop-Location }

$documentationPaths = @(& git -C $repository -c core.quotepath=false ls-files --cached --others --exclude-standard -- README.md CONTRIBUTING.md docs examples)
if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate bundled documentation and examples.' }
$documentationPaths = @($documentationPaths | Where-Object { $_ -ne 'docs/implementation-prompt.md' -and $_ -notlike 'docs/api/*' -and $_ -notlike 'docs/_site/*' -and $_ -notmatch '(^|[/\\])(\.git|artifacts|bin|obj|review)([/\\]|$)' })
foreach ($relative in $documentationPaths) { Copy-Input $relative $payload }
$documentationInputs = @($documentationPaths | Sort-Object | ForEach-Object { Describe-File (Join-Path $payload $_) $payload })
$licenseRoot = Join-Path $payload 'licenses'
New-Item -ItemType Directory -Path $licenseRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $source 'src/ParticleLife.MonoGame/Assets/Fonts/OFL.txt') -Destination (Join-Path $licenseRoot 'IBM-Plex-Sans-OFL.txt')
Push-Location $source
try {
    $cacheLine = @(& dotnet nuget locals global-packages --list)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot locate package license metadata.' }
}
finally { Pop-Location }
$packageCache = ($cacheLine | Where-Object { $_ -match '^global-packages:\s*(.+)$' } | Select-Object -First 1) -replace '^global-packages:\s*', ''
if ([string]::IsNullOrWhiteSpace($packageCache)) { throw 'Cannot identify the global packages directory.' }
$packageMetadata = @{}
foreach ($pins in $originalPins.Values) {
    foreach ($id in $pins.Keys) { $packageMetadata[$id] = $pins[$id] }
}
foreach ($id in $packageMetadata.Keys) {
    $packagePath = Join-Path (Join-Path $packageCache $id) $packageMetadata[$id]['version']
    $destination = Join-Path $licenseRoot ($id + '-' + $packageMetadata[$id]['version'])
    New-Item -ItemType Directory -Path $destination | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $packagePath -File) {
        if ($file.Name -match '(?i)license|notice|copying|\.nuspec$') { Copy-Item -LiteralPath $file.FullName -Destination $destination }
    }
}
$resolvedLocks = @()
foreach ($lock in $locks) {
    $relative = [IO.Path]::GetRelativePath($source, $lock.FullName)
    $destination = Join-Path (Join-Path $payload 'build-info') $relative
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
    Copy-Item -LiteralPath $lock.FullName -Destination $destination
    $resolvedLocks += Describe-File $destination $payload
}

@'
@echo off
pushd "%~dp0"
"viewer\ParticleLife.MonoGame.exe" --save-dir "%~dp0discoveries" %*
set "particlelife_exit=%errorlevel%"
popd
exit /b %particlelife_exit%
'@ | Set-Content -LiteralPath (Join-Path $payload 'Start-Viewer.cmd') -Encoding ascii
@'
@echo off
pushd "%~dp0"
"cli\ParticleLife.Cli.exe" %*
set "particlelife_exit=%errorlevel%"
popd
exit /b %particlelife_exit%
'@ | Set-Content -LiteralPath (Join-Path $payload 'Run-Cli.cmd') -Encoding ascii
@'
Particle Life 2 - unsigned private preview for Windows x64

Extract the complete folder to a writable location. Start-Viewer.cmd opens the viewer;
H / F1 opens its quickstart. New discoveries are saved beside this file in discoveries/.
The included runtime does not require an installed .NET SDK. A working native OpenGL
driver is still required for the desktop window. CPU simulation remains available.

Run-Cli.cmd --help uses the CPU-only runner without opening a graphics context.
For a small example: Run-Cli.cmd run examples/experiments/periodic-seam.json --steps 20 --sample-every 0 --output artifacts/seam-run.json

See docs/quickstart.md and docs/portable-preview.md. All output paths need new names.
This folder is a private preview, not an installer, signed binary or published release.
Native package acceptance and measured CPU/GPU capacity require the separate review.
'@ | Set-Content -LiteralPath (Join-Path $payload 'START-HERE.txt') -Encoding utf8NoBOM

$smoke = 'not requested'
if ($SmokeTest) {
    Push-Location $payload
    try {
        & (Join-Path $payload 'viewer/ParticleLife.MonoGame.exe') --help 2>&1 | Set-Content -LiteralPath (Join-Path $logs 'viewer-help.txt')
        if ($LASTEXITCODE -ne 0) { throw 'Published viewer --help failed.' }
        & (Join-Path $payload 'cli/ParticleLife.Cli.exe') --help 2>&1 | Set-Content -LiteralPath (Join-Path $logs 'cli-help.txt')
        if ($LASTEXITCODE -ne 0) { throw 'Published CPU CLI --help failed.' }
        & (Join-Path $payload 'cli/ParticleLife.Cli.exe') validate examples/experiments/periodic-seam.json 2>&1 | Set-Content -LiteralPath (Join-Path $logs 'cli-validate.txt')
        if ($LASTEXITCODE -ne 0) { throw 'Published CPU CLI fixture validation failed.' }
        & (Join-Path $payload 'cli/ParticleLife.Cli.exe') run examples/experiments/periodic-seam.json --steps 2 --sample-every 0 --output (Join-Path $outputRoot 'build/cpu-smoke.json') 2>&1 | Set-Content -LiteralPath (Join-Path $logs 'cli-run.txt')
        if ($LASTEXITCODE -ne 0) { throw 'Published CPU CLI two-step smoke failed.' }
        $smoke = 'viewer --help and CPU CLI help, fixture validation and two-step run passed; no native window opened'
    }
    finally { Pop-Location }
}
$runtimeConfiguration = Get-Content -LiteralPath (Join-Path $payload 'viewer/ParticleLife.MonoGame.runtimeconfig.json') -Raw | ConvertFrom-Json -AsHashtable
$files = @(Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName | ForEach-Object { Describe-File $_.FullName $payload })
[ordered]@{
    schemaVersion = 1
    kind = 'particle-life-private-preview-package'
    builtAtUtc = [DateTime]::UtcNow.ToString('O')
    sourceCommit = $commit
    sourceWorkingTreeDirty = $checkoutStatus.Count -gt 0
    sourceStatus = $checkoutStatus
    sdk = $sdk
    runtimeIdentifier = 'win-x64'
    selfContained = $true
    runtimeOptions = $runtimeConfiguration['runtimeOptions']
    buildHost = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
    configuration = 'Release'
    sourceInputs = $sourceInputs
    documentationAndExampleInputs = $documentationInputs
    originalPackagePins = $packageMetadata
    resolvedPublishLocks = $resolvedLocks
    files = $files
    hashPolicy = 'SHA-256 of every payload file before manifest.json; manifest excludes itself and the archive.'
    smoke = $smoke
    nativeAcceptance = 'pending consolidated Windows native review; no graphics acceptance inferred from headless smoke'
    capacity = 'not established by packaging; use matched CPU/GPU performance reports'
    distribution = 'unsigned private preview; no signing, installer, release publication or upload performed'
} | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $payload 'manifest.json') -Encoding utf8NoBOM
if (-not $NoArchive) {
    $archive = Join-Path $outputRoot 'ParticleLife2-win-x64-private-preview.zip'
    Compress-Archive -LiteralPath $payload -DestinationPath $archive -CompressionLevel Optimal
    ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash + '  ' + [IO.Path]::GetFileName($archive)) |
        Set-Content -LiteralPath ($archive + '.sha256') -Encoding ascii
}
Write-Host "Private preview: $payload"
Write-Host "Manifest: $(Join-Path $payload 'manifest.json')"
