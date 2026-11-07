<#
.SYNOPSIS
Builds the bundled native icon atlas from the checked-in, pinned Lucide SVGs.
#>
param()
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$sourceDirectory = Join-Path $repository 'src/ParticleLife.MonoGame/Assets/Lucide'
$temporaryDirectory = Join-Path $repository 'artifacts/lucide-atlas'
$version = & magick -version
if ($LASTEXITCODE -ne 0 -or $version[0] -notmatch 'ImageMagick 7\.1\.2-3 ') {
    throw 'Use ImageMagick 7.1.2-3 to reproduce the checked-in atlas.'
}
New-Item -ItemType Directory -Force -Path $temporaryDirectory | Out-Null
$names = @('mouse-pointer-2', 'hand', 'circle-plus', 'paintbrush', 'circle-minus', 'magnet', 'expand', 'wind',
    'undo-2', 'redo-2', 'circle-dashed', 'square-dashed', 'circle-check', 'circle-ellipsis',
    'square-chevron-right', 'square-chevron-left', 'check')
$images = @()
foreach ($name in $names) {
    $source = Get-Content -Raw -LiteralPath (Join-Path $sourceDirectory "$name.svg")
    $temporarySvg = Join-Path $temporaryDirectory "$name.svg"
    $image = Join-Path $temporaryDirectory "$name.png"
    # This resolves the SVG currentColor paint without changing its geometry or stroke.
    [IO.File]::WriteAllText($temporarySvg, $source.Replace('currentColor', '#ffffff'))
    & magick -background none -density 1536 $temporarySvg -filter Lanczos -resize 96x96 "PNG32:$image"
    if ($LASTEXITCODE -ne 0) { throw "Failed to rasterize $name." }
    $images += $image
}
& magick @images +append -strip "PNG32:$(Join-Path $sourceDirectory 'lucide-atlas.png')"
if ($LASTEXITCODE -ne 0) { throw 'Failed to assemble icon atlas.' }
Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $sourceDirectory 'lucide-atlas.png')
