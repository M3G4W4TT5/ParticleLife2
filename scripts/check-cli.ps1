#requires -Version 7.2
# Exercise the built CLI as a user would, retaining reports in ignored artifacts/.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = Split-Path -Parent $PSScriptRoot
$assembly = Join-Path $repository 'src/ParticleLife.Cli/bin/Release/net10.0/ParticleLife.Cli.dll'
if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) {
    throw 'Build the solution in Release before running the CLI checks.'
}

$outputDirectory = Join-Path $repository ('artifacts/cli-check/' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $outputDirectory
$script:commandCount = 0

function Assert-That {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Close {
    param([double]$Expected, [double]$Actual, [string]$Message)
    # Reduced-unit times of order one: allow double summation roundoff only.
    $tolerance = 1e-12 + 1e-12 * [Math]::Max([Math]::Abs($Expected), [Math]::Abs($Actual))
    Assert-That ([double]::IsFinite($Actual) -and [Math]::Abs($Expected - $Actual) -le $tolerance) $Message
}

function ConvertTo-CanonicalValue {
    param([AllowNull()][object]$Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [System.Collections.IDictionary]) {
        $result = [ordered]@{}
        foreach ($key in ($Value.Keys | Sort-Object)) {
            $result[$key] = ConvertTo-CanonicalValue -Value $Value[$key]
        }
        return $result
    }
    if ($Value -is [pscustomobject]) {
        $result = [ordered]@{}
        foreach ($property in ($Value.PSObject.Properties | Sort-Object Name)) {
            $result[$property.Name] = ConvertTo-CanonicalValue -Value $property.Value
        }
        return $result
    }
    if ($Value -is [array]) {
        $result = [object[]]::new($Value.Count)
        for ($i = 0; $i -lt $Value.Count; $i++) {
            $result[$i] = ConvertTo-CanonicalValue -Value $Value[$i]
        }
        return ,$result
    }
    return $Value
}

function ConvertTo-CanonicalJson {
    param([AllowNull()][object]$Value)
    # Sort object keys, preserve array order, and serialize parsed numeric values.
    $canonical = ConvertTo-CanonicalValue -Value $Value
    return ConvertTo-Json -InputObject $canonical -Depth 100 -Compress
}

function Read-Json {
    param([string]$Path)
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Write-Json {
    param([string]$Path, [object]$Value)
    $json = ConvertTo-Json -InputObject $Value -Depth 100
    [IO.File]::WriteAllText($Path, $json + "`n")
}

function Invoke-Cli {
    param([string[]]$Arguments, [int]$ExpectedExit = 0, [string]$ErrorPattern = '', [scriptblock]$WhileRunning = $null)
    $script:commandCount++
    $startInfo = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $startInfo.WorkingDirectory = $repository
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add($assembly)
    foreach ($argument in $Arguments) { $startInfo.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $processStarted = $false
    try {
        $process.StartInfo = $startInfo
        $processStarted = $process.Start()
        Assert-That $processStarted 'The CLI process did not start.'
        # Read both streams concurrently so neither pipe can block the child.
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if ($null -ne $WhileRunning) { & $WhileRunning $process }
        Assert-That ($process.WaitForExit(30000)) 'The CLI command exceeded its 30-second timeout.'
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $exitCode = $process.ExitCode
    }
    finally {
        if ($processStarted -and -not $process.HasExited) {
            $process.Kill($true)
            $null = $process.WaitForExit(5000)
        }
        $process.Dispose()
    }

    $logPath = Join-Path $outputDirectory ('command-{0:d2}.json' -f $script:commandCount)
    Write-Json $logPath ([ordered]@{ arguments = $Arguments; exitCode = $exitCode; stdout = $stdout; stderr = $stderr })
    Assert-That ($exitCode -eq $ExpectedExit) "CLI exited $exitCode, expected $ExpectedExit. Arguments: $Arguments`n$stdout`n$stderr"
    if ($ExpectedExit -eq 0) {
        Assert-That ([string]::IsNullOrWhiteSpace($stderr)) "Successful CLI command wrote an error: $stderr"
    }
    else {
        Assert-That ($stderr -match '(?m)^Error:') "CLI failure did not provide its concise error message: $stderr"
        Assert-That ($stderr -notmatch '(?m)Unhandled exception|^\s+at\s') "CLI leaked an unhandled exception or stack trace: $stderr"
        if ($ErrorPattern.Length -gt 0) {
            Assert-That ($stderr -match $ErrorPattern) "CLI error did not match '$ErrorPattern': $stderr"
        }
    }
}

$seed = 20261104
$population = 25
$species = 4
$randomPath = ''
foreach ($preset in @('random', 'chains', 'snakes')) {
    $experimentPath = Join-Path $outputDirectory "$preset.json"
    $sampledPath = Join-Path $outputDirectory "$preset-sampled.json"
    $unsampledPath = Join-Path $outputDirectory "$preset-unsampled.json"
    Invoke-Cli -Arguments @('create', '--preset', $preset, '--particles', "$population", '--species', "$species",
        '--seed', "$seed", '--backend', 'direct', '--output', $experimentPath)
    Invoke-Cli -Arguments @('validate', $experimentPath)
    $experiment = Read-Json $experimentPath
    $sourceHash = (Get-FileHash -LiteralPath $experimentPath -Algorithm SHA256).Hash
    Assert-That ($experiment.initialState.ids.Count -eq $population) "$preset did not save the requested population."
    Assert-That ($experiment.initialState.positions.Count -eq $population -and $experiment.initialState.velocities.Count -eq $population) "$preset omitted particle state."
    Assert-That ($experiment.matrix.values.Count -eq $species * $species) "$preset did not save actual matrix entries."
    Assert-That ($experiment.generator.seed -eq $seed -and $experiment.generator.name.Length -gt 0 -and $experiment.generator.version.Length -gt 0) "$preset omitted generator provenance."

    Invoke-Cli -Arguments @('run', $experimentPath, '--steps', '20', '--sample-every', '5', '--output', $sampledPath)
    Invoke-Cli -Arguments @('run', $experimentPath, '--steps', '20', '--sample-every', '0', '--output', $unsampledPath)
    $sampled = Read-Json $sampledPath
    $unsampled = Read-Json $unsampledPath
    Assert-That ((ConvertTo-CanonicalJson $sampled.finalState) -ceq (ConvertTo-CanonicalJson $unsampled.finalState)) "$preset diagnostics changed the exact final state."
    Assert-That ($sampled.requestedSteps -eq 20 -and $sampled.finalState.step -eq 20) "$preset report has an incorrect step count."
    Assert-Close (20 * $experiment.timeStep) $sampled.finalState.time "$preset report has an incorrect final simulation time."
    Assert-That ($sampled.sampleEvery -eq 5 -and $unsampled.sampleEvery -eq 0) "$preset report omitted its measurement cadence."
    Assert-That (($sampled.measurements.step -join ',') -ceq '0,5,10,15,20') "$preset diagnostics have an incorrect cadence."
    Assert-That ($unsampled.measurements.Count -eq 0) "$preset still sampled diagnostics when disabled."
    foreach ($sample in $sampled.measurements) {
        Assert-That ($sample.count -eq $population -and $sample.finiteCount -eq $population) "$preset lost particles or reported non-finite state."
        Assert-Close ($sample.step * $experiment.timeStep) $sample.time "$preset sample has an incorrect simulation time."
        Assert-Close $sampled.clusterDistance $sample.clusterDistance "$preset sample omitted its cluster criterion."
    }
    foreach ($report in @($sampled, $unsampled)) {
        Assert-That ((ConvertTo-CanonicalJson $report.experiment) -ceq (ConvertTo-CanonicalJson $experiment)) "$preset run report did not retain the complete original experiment."
        Assert-That ($report.sourceExperimentSha256 -ceq $sourceHash) "$preset report source digest does not identify the saved input."
        Assert-That ($report.environment.runtime.Length -gt 0 -and $report.environment.sdk.Length -gt 0) "$preset report omitted runtime metadata."
        Assert-That ($report.execution.requestedNeighbourSearchVersion -ceq $experiment.neighbourSearchVersion) "$preset report omitted the requested neighbour search."
        Assert-That ($report.execution.integratorVersion -ceq $experiment.integratorVersion) "$preset report omitted the integration method."
        Assert-That ($report.execution.lastCompletedStep.actualNeighbourSearchVersion -ceq 'direct/1') "$preset did not execute the selected direct search."
        Assert-That ($report.execution.directFallbackSteps -eq 0 -and $null -eq $report.execution.backendOverride) "$preset reported an unexpected override or fallback."
        Assert-That ($report.environment.buildConfiguration -ceq 'Release' -and $report.environment.simulationThreads -eq 1) "$preset report has incorrect build or solver metadata."
        Assert-That ($report.environment.coreAssemblySha256 -match '^[0-9A-F]{64}$') "$preset report omitted the core assembly digest."
        Assert-That ($report.lastForceEvaluation.candidatePairs -eq $population * ($population - 1)) "$preset report has an incorrect directed-pair count."
    }
    Assert-That ((Get-FileHash -LiteralPath $experimentPath -Algorithm SHA256).Hash -ceq $sourceHash) "$preset run modified its input experiment."
    if ($preset -ceq 'random') { $randomPath = $experimentPath }
}

foreach ($preset in @('random', 'chains', 'snakes')) {
    $experimentPath = Join-Path $outputDirectory "$preset-grid.json"
    $gridReportPath = Join-Path $outputDirectory "$preset-grid-run.json"
    $overrideReportPath = Join-Path $outputDirectory "$preset-direct-override.json"
    # Omitting --backend deliberately checks the new host default, not only explicit selection.
    Invoke-Cli -Arguments @('create', '--preset', $preset, '--particles', "$population", '--species', "$species",
        '--seed', "$seed", '--output', $experimentPath)
    $experiment = Read-Json $experimentPath
    Assert-That ($experiment.schemaVersion -eq 2 -and $experiment.neighbourSearchVersion -ceq 'uniform-grid/1') "$preset did not save grid as the intended search."
    Assert-That ($experiment.modelVersion -ceq 'particle-life/1' -and $experiment.integratorVersion -ceq 'damped-semi-implicit-euler/1') "$preset search optimization changed its physical model or integrator identity."
    Invoke-Cli -Arguments @('validate', $experimentPath)
    Invoke-Cli -Arguments @('run', $experimentPath, '--steps', '20', '--sample-every', '0', '--output', $gridReportPath)
    Invoke-Cli -Arguments @('run', $experimentPath, '--steps', '20', '--sample-every', '0', '--backend', 'direct', '--output', $overrideReportPath)
    $gridReport = Read-Json $gridReportPath
    $overrideReport = Read-Json $overrideReportPath
    Assert-That ($gridReport.execution.lastCompletedStep.actualNeighbourSearchVersion -ceq 'uniform-grid/1') "$preset grid run did not use the grid."
    Assert-That ($gridReport.lastForceEvaluation.candidatePairs -lt $population * ($population - 1)) "$preset grid run did not prune any candidates."
    Assert-That ($gridReport.execution.directFallbackSteps -eq 0) "$preset unexpectedly fell back to direct."
    Assert-That ($overrideReport.execution.savedNeighbourSearchVersion -ceq 'uniform-grid/1' -and $overrideReport.execution.backendOverride -ceq 'direct/1') "$preset override lost source or effective provenance."
    Assert-That ($overrideReport.execution.lastCompletedStep.actualNeighbourSearchVersion -ceq 'direct/1') "$preset explicit override did not execute direct search."
    Assert-That ((ConvertTo-CanonicalJson $overrideReport.experiment) -ceq (ConvertTo-CanonicalJson $experiment)) "$preset override altered the report's original input."
}

# validate and run must decode the same input, while reports hash the original encoded bytes.
$encodingCases = [ordered]@{
    'utf8-bom' = [Text.UTF8Encoding]::new($true)
    'utf16-le' = [Text.UnicodeEncoding]::new($false, $true)
    'utf16-be' = [Text.UnicodeEncoding]::new($true, $true)
    'utf32-le' = [Text.UTF32Encoding]::new($false, $true)
    'utf32-be' = [Text.UTF32Encoding]::new($true, $true)
}
$sourceJson = [IO.File]::ReadAllText($randomPath)
$baselineReport = Read-Json (Join-Path $outputDirectory 'random-unsampled.json')
foreach ($encodingCase in $encodingCases.GetEnumerator()) {
    $encodedPath = Join-Path $outputDirectory ($encodingCase.Key + '.json')
    $encodedReportPath = Join-Path $outputDirectory ($encodingCase.Key + '-run.json')
    [IO.File]::WriteAllText($encodedPath, $sourceJson, $encodingCase.Value)
    $encodedHash = (Get-FileHash -LiteralPath $encodedPath -Algorithm SHA256).Hash
    Invoke-Cli -Arguments @('validate', $encodedPath)
    Invoke-Cli -Arguments @('run', $encodedPath, '--steps', '20', '--sample-every', '0', '--output', $encodedReportPath)
    $encodedReport = Read-Json $encodedReportPath
    Assert-That ((ConvertTo-CanonicalJson $encodedReport.experiment) -ceq (ConvertTo-CanonicalJson $baselineReport.experiment)) "$($encodingCase.Key) changed the decoded experiment."
    Assert-That ((ConvertTo-CanonicalJson $encodedReport.finalState) -ceq (ConvertTo-CanonicalJson $baselineReport.finalState)) "$($encodingCase.Key) changed the simulation result."
    Assert-That ($encodedReport.sourceExperimentSha256 -ceq $encodedHash) "$($encodingCase.Key) report did not hash the original encoded bytes."
    Assert-That ((Get-FileHash -LiteralPath $encodedPath -Algorithm SHA256).Hash -ceq $encodedHash) "$($encodingCase.Key) input was modified."
}

$zeroPath = Join-Path $outputDirectory 'zero-steps.json'
Invoke-Cli -Arguments @('run', $randomPath, '--steps', '0', '--sample-every', '5', '--output', $zeroPath)
$zero = Read-Json $zeroPath
$original = Read-Json $randomPath
$expectedZero = [ordered]@{
    time = 0; step = 0; ids = $original.initialState.ids; positions = $original.initialState.positions
    velocities = $original.initialState.velocities; species = $original.initialState.species
}
Assert-That ((ConvertTo-CanonicalJson $zero.finalState) -ceq (ConvertTo-CanonicalJson $expectedZero)) 'A zero-step run changed the saved initial state.'
Assert-That ($zero.requestedSteps -eq 0 -and $null -eq $zero.lastForceEvaluation) 'A zero-step report implies force evaluation occurred.'
Assert-That ($null -eq $zero.execution.lastCompletedStep -and $zero.execution.directFallbackSteps -eq 0) 'A zero-step report implies actual search execution occurred.'
Assert-That ($zero.measurements.Count -eq 1 -and $zero.measurements[0].step -eq 0) 'A zero-step run should include its requested initial diagnostic.'

$partialPath = Join-Path $outputDirectory 'partial-cadence.json'
Invoke-Cli -Arguments @('run', $randomPath, '--steps', '18', '--sample-every', '5', '--output', $partialPath)
$partial = Read-Json $partialPath
Assert-That (($partial.measurements.step -join ',') -ceq '0,5,10,15,18') 'The final off-cadence state was not sampled exactly once.'

Invoke-Cli -Arguments @('run', $randomPath) -ExpectedExit 1 -ErrorPattern 'Missing --steps'
Invoke-Cli -Arguments @('run', $randomPath, '--steps', 'invalid') -ExpectedExit 1 -ErrorPattern 'requires an integer'
Invoke-Cli -Arguments @('run', $randomPath, '--steps', '-1') -ExpectedExit 1
Invoke-Cli -Arguments @('run', $randomPath, '--steps') -ExpectedExit 1 -ErrorPattern 'incomplete option'
Invoke-Cli -Arguments @('run', $randomPath, '--steps', '1', '--unknown', '1') -ExpectedExit 1 -ErrorPattern 'Unknown or incomplete option'
Invoke-Cli -Arguments @('run', $randomPath, '--steps', '1', '--backend', 'invalid') -ExpectedExit 1 -ErrorPattern 'Backend must be direct or grid'
Invoke-Cli -Arguments @('create', '--dt', '0', '--output', (Join-Path $outputDirectory 'invalid-dt.json')) -ExpectedExit 1
Invoke-Cli -Arguments @('create', '--dt', 'NaN', '--output', (Join-Path $outputDirectory 'nonfinite-dt.json')) -ExpectedExit 1 -ErrorPattern 'finite number'

$invalidSchemaPath = Join-Path $outputDirectory 'invalid-schema.json'
$invalidSchema = Read-Json $randomPath
$invalidSchema.schemaVersion = 999
Write-Json $invalidSchemaPath $invalidSchema
Invoke-Cli -Arguments @('validate', $invalidSchemaPath) -ExpectedExit 1 -ErrorPattern 'Unsupported experiment schema'
Invoke-Cli -Arguments @('run', $invalidSchemaPath, '--steps', '1') -ExpectedExit 1 -ErrorPattern 'Unsupported experiment schema'

$existingHash = (Get-FileHash -LiteralPath $randomPath -Algorithm SHA256).Hash
Invoke-Cli -Arguments @('create', '--output', $randomPath) -ExpectedExit 1 -ErrorPattern 'Output already exists'
Invoke-Cli -Arguments @('run', $randomPath, '--steps', '1', '--output', $randomPath) -ExpectedExit 1 -ErrorPattern 'Output already exists'
Assert-That ((Get-FileHash -LiteralPath $randomPath -Algorithm SHA256).Hash -ceq $existingHash) 'Output protection changed an existing experiment.'

# Synchronize with PrepareOutput via a unique parent directory created by the child. The bounded
# direct-solver workload gives this process time to create the destination before the final write.
$raceInput = Join-Path $outputDirectory 'output-race-input.json'
$raceParent = Join-Path $outputDirectory 'output-race'
$raceOutput = Join-Path $raceParent 'result.json'
$sentinelText = 'Created by another writer while the simulation was running.'
Invoke-Cli -Arguments @('create', '--particles', '2000', '--species', '2', '--seed', "$seed", '--backend', 'direct', '--output', $raceInput)
Invoke-Cli -Arguments @('run', $raceInput, '--steps', '300', '--sample-every', '0', '--output', $raceOutput) -ExpectedExit 1 -WhileRunning {
    param($runningProcess)
    $readyTimer = [Diagnostics.Stopwatch]::StartNew()
    while (-not [IO.Directory]::Exists($raceParent)) {
        Assert-That (-not $runningProcess.HasExited) 'The CLI exited before preparing its output directory.'
        Assert-That ($readyTimer.ElapsedMilliseconds -lt 10000) 'The CLI did not prepare its output directory within ten seconds.'
        Start-Sleep -Milliseconds 5
    }
    # CreateNew ensures this test cannot replace an already completed report and hide a failure.
    $sentinelStream = [IO.FileStream]::new($raceOutput, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $sentinelBytes = [Text.Encoding]::UTF8.GetBytes($sentinelText)
        $sentinelStream.Write($sentinelBytes, 0, $sentinelBytes.Length)
    }
    finally {
        $sentinelStream.Dispose()
    }
}
Assert-That ([IO.File]::ReadAllText($raceOutput) -ceq $sentinelText) 'The CLI replaced an output file created during the run.'

$overflowPath = Join-Path $outputDirectory 'numerical-overflow.json'
$overflowOutput = Join-Path $outputDirectory 'numerical-overflow-result.json'
$overflow = Read-Json $randomPath
$overflow.timeStep = 2
$overflow.parameters.forceScale = 0
$overflow.parameters.wallStrength = 0
$overflow.parameters.dampingHalfLife = $null
$overflow.initialState = [ordered]@{
    ids = @(991); positions = @(@{ x = 0.5; y = 0.5 }); velocities = @(@{ x = 1e308; y = 0 }); species = @(0)
}
Write-Json $overflowPath $overflow
Invoke-Cli -Arguments @('validate', $overflowPath)
Invoke-Cli -Arguments @('run', $overflowPath, '--steps', '1', '--sample-every', '0', '--output', $overflowOutput) -ExpectedExit 1 -ErrorPattern 'Integration for particle 991 is non-finite'
Assert-That (-not (Test-Path -LiteralPath $overflowOutput)) 'A failed numerical step published a run report.'

$parallelInput = Join-Path $outputDirectory 'parallel.json'
$parallelReport = Join-Path $outputDirectory 'parallel-report.json'
$serialOverrideReport = Join-Path $outputDirectory 'serial-override.json'
Invoke-Cli -Arguments @('create', '--particles', '64', '--backend', 'grid', '--workers', '4', '--output', $parallelInput)
Invoke-Cli -Arguments @('run', $parallelInput, '--steps', '20', '--sample-every', '5', '--output', $parallelReport)
Invoke-Cli -Arguments @('run', $parallelInput, '--steps', '20', '--workers', '1', '--sample-every', '5', '--output', $serialOverrideReport)
$parallel = Read-Json $parallelReport
$serialOverride = Read-Json $serialOverrideReport
Assert-That ($parallel.execution.savedWorkerCount -eq 4 -and $parallel.execution.lastCompletedStep.actualWorkerCount -eq 4) 'Saved worker selection was not replayed.'
Assert-That ($serialOverride.execution.savedWorkerCount -eq 4 -and $serialOverride.execution.workerCountOverride -eq 1) 'Worker override lost provenance.'
Assert-That ((ConvertTo-CanonicalJson $parallel.finalState) -ceq (ConvertTo-CanonicalJson $serialOverride.finalState)) 'Worker scheduling changed receiver sums.'
Assert-That ($parallel.diagnostics.algorithmVersion -eq 'spatial-components/1') 'CLI did not use reusable diagnostics.'
Invoke-Cli -Arguments @('run', $parallelInput, '--steps', '1', '--workers', '0') -ExpectedExit 1
Invoke-Cli -Arguments @('run', $parallelInput, '--steps', '1', '--workers', '9') -ExpectedExit 1
Invoke-Cli -Arguments @('run', $parallelInput, '--steps', '1', '--backend', 'direct', '--workers', '4') -ExpectedExit 1

# Finite velocity components can have an overflowing diagnostic Hypot while a small dt still integrates.
$measurementFailureInput = Join-Path $outputDirectory 'measurement-failure.json'
$measurementFailureReport = Join-Path $outputDirectory 'measurement-failure-report.json'
$measurementFailure = Read-Json $randomPath
$measurementFailure.timeStep = 0.0001
$measurementFailure.parameters.forceScale = 0
$measurementFailure.parameters.wallStrength = 0
$measurementFailure.parameters.dampingHalfLife = $null
$measurementFailure.initialState = [ordered]@{
    ids = @(992); positions = @(@{ x = 0; y = 0 }); velocities = @(@{ x = 1.3e308; y = 1.3e308 }); species = @(0)
}
Write-Json $measurementFailureInput $measurementFailure
Invoke-Cli -Arguments @('run', $measurementFailureInput, '--steps', '2', '--sample-every', '1', '--output', $measurementFailureReport)
$measurementFailureResult = Read-Json $measurementFailureReport
Assert-That ($measurementFailureResult.finalState.step -eq 2 -and $measurementFailureResult.diagnostics.failures.Count -eq 3) 'Optional diagnostic failure stopped valid integration.'

Write-Output "CLI checks passed ($script:commandCount commands). Reports retained at $outputDirectory"
