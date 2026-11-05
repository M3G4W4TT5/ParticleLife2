# Rebuild the curated runtime atlas on Windows. Not part of restore/build.
# Requires the bundled unmodified IBM Plex Sans font and System.Drawing.
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$fontRoot = Join-Path $PSScriptRoot '../src/ParticleLife.MonoGame/Assets/Fonts'
$fontPath = (Resolve-Path (Join-Path $fontRoot 'IBMPlexSans-Regular.ttf')).Path
$fontHash = (Get-FileHash -LiteralPath $fontPath -Algorithm SHA256).Hash
if ($fontHash -ne '975DCDA37D80F038DCD143C22E33CA2D97A0CC5A929AACE1C749153B0FE1AFA5') {
    throw 'The font source differs from the reviewed IBM Plex Sans source.'
}
$characters = @((32..126) + (160..255) + @(0x03B2, 0x0394, 0x2013, 0x2014, 0x2022, 0x2026, 0x2190, 0x2191, 0x2192, 0x2193, 0x2212))
$columns = 16
$cell = 80
$padding = 4
$width = $columns * $cell
$height = [int][Math]::Ceiling($characters.Count / $columns) * $cell
$collection = [System.Drawing.Text.PrivateFontCollection]::new()
$collection.AddFontFile($fontPath)
$font = [System.Drawing.Font]::new($collection.Families[0], 48, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$bitmap = [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$format = [System.Drawing.StringFormat]::GenericTypographic.Clone()
$format.FormatFlags = $format.FormatFlags -bor [System.Drawing.StringFormatFlags]::MeasureTrailingSpaces
$advances = @()
try {
    for ($index = 0; $index -lt $characters.Count; $index++) {
        $character = [string][char]$characters[$index]
        $x = ($index % $columns) * $cell + $padding
        $y = [Math]::Floor($index / $columns) * $cell + $padding
        $advances += $graphics.MeasureString($character, $font, [System.Drawing.PointF]::new(0, 0), $format).Width
        $graphics.DrawString($character, $font, [System.Drawing.Brushes]::White, [System.Drawing.PointF]::new($x, $y), $format)
    }
    $bitmap.Save((Join-Path $fontRoot 'plex-sans-atlas.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    [ordered]@{ Columns = $columns; CellSize = $cell; Padding = $padding; RasterSize = 48; Characters = $characters; Advances = $advances } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $fontRoot 'plex-sans-metrics.json') -Encoding utf8NoBOM
}
finally {
    $format.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
    $font.Dispose()
    $collection.Dispose()
}
