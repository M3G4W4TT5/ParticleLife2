param(
    [string]$Destination = 'artifacts/m4-1/baseline',
    [string]$InputDirectory = 'artifacts/benchmarks/m4',
    [string]$OutputDirectory = 'artifacts/m4-1/baseline-results',
    [switch]$SkipEvolution
)
$ErrorActionPreference = 'Stop'
$baselineCommit = 'e2d8a272582602206a6aa38d5b854eaf33dca86a'
# Isolated accepted source, never a second production implementation or build dependency.
$destinationPath = [IO.Path]::GetFullPath($Destination)
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $destinationPath, $outputPath | Out-Null
if (-not (Test-Path -LiteralPath (Join-Path $destinationPath 'ParticleLife.slnx')))
{
    $archivePath = Join-Path $destinationPath 'accepted-m4.zip'
    git archive --format=zip "--output=$archivePath" $baselineCommit
    if ($LASTEXITCODE -ne 0) { throw 'Cannot archive accepted M4 commit.' }
    Expand-Archive -LiteralPath $archivePath -DestinationPath $destinationPath
}
$runnerPath = Join-Path $destinationPath 'benchmarks/ParticleLife.Benchmarks/ComparisonRunner.cs'
$runner = (& git show "$($baselineCommit):benchmarks/ParticleLife.Benchmarks/ComparisonRunner.cs") -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'Cannot read accepted comparison runner.' }
# Bounded paired warm-up; M4.1 warms each complete/stage path independently. Core source is unchanged.
$runner = $runner.Replace('MinimumWarmupSteps = 64;', 'MinimumWarmupSteps = 8;')
$runner = $runner.Replace('MinimumWarmupSeconds = 1;', 'MinimumWarmupSeconds = 0.5;')
if ($SkipEvolution)
{
    $runner = $runner.Replace('object evolving = MeasureEvolving(evolvingExperiment, evolvingPath, repetitions);', 'object? evolving = null;')
    $runner = $runner.Replace('Console.WriteLine($"Evolving comparison: {evolvingExperiment.Name}, {repetitions} x {EvolvingSteps} consecutive steps.");', 'Console.WriteLine("Static comparison only; evolution is measured separately.");')
}
[IO.File]::WriteAllText($runnerPath, $runner)
$profileSource = Join-Path $PSScriptRoot '../benchmarks/ParticleLife.Benchmarks/TraversalProfile.cs'
Copy-Item -LiteralPath $profileSource -Destination (Join-Path $destinationPath 'benchmarks/ParticleLife.Benchmarks/TraversalProfile.cs')
$programPath = Join-Path $destinationPath 'benchmarks/ParticleLife.Benchmarks/Program.cs'
$program = [IO.File]::ReadAllText($programPath)
if (-not $program.Contains('"profile-m41"'))
{
    $dispatch = @'
try
{
    if (args.Length == 3 && args[0] == "profile-m41")
    {
        TraversalProfile.Run(args[1], args[2]);
        return 0;
    }
'@
    $program = $program.Replace("`r`n", "`n").Replace("try`n{", $dispatch)
    [IO.File]::WriteAllText($programPath, $program)
}
foreach ($input in Get-ChildItem -LiteralPath $InputDirectory -Filter '*.experiment.json')
{
    $target = Join-Path $outputPath $input.Name
    if (Test-Path -LiteralPath $target)
    {
        if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $input.FullName).Hash)
        { throw "Different existing input: $target" }
    }
    else { Copy-Item -LiteralPath $input.FullName -Destination $target }
}
@{
    acceptedCommit = $baselineCommit
    sourceArchive = $destinationPath
    runnerChanges = 'Minimum warm-up: 8 steps and 0.5 seconds; standalone candidate/geometry profiler added; core source unchanged.'
    skipEvolution = $SkipEvolution.IsPresent
    inputs = @(Get-ChildItem -LiteralPath $outputPath -Filter '*.experiment.json' | ForEach-Object {
        @{ file = $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
    })
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputPath 'baseline-manifest.json')
Push-Location $destinationPath
try
{
    dotnet restore benchmarks/ParticleLife.Benchmarks --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Baseline restore failed.' }
    dotnet build benchmarks/ParticleLife.Benchmarks -c Release --no-restore -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Baseline build failed.' }
}
finally { Pop-Location }
Write-Output "Prepared baseline. Run its DLL with: compare `"$outputPath`" 15 all 2"
