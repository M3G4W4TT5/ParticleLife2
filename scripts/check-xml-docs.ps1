$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repository 'src/ParticleLife.Core/ParticleLife.Core.csproj'
$probe = Join-Path $repository 'src/ParticleLife.Core/DocumentationGateProbe.cs'
if (Test-Path -LiteralPath $probe) { throw "Refusing to replace an existing file: $probe" }
$cases = @(
    @{ Code = 'namespace ParticleLife.Core; public class DocumentationGateProbe { }'; Diagnostic = 'CS1591' },
    @{ Code = "namespace ParticleLife.Core;`n/// <summary>Broken XML</remarks>`npublic class DocumentationGateProbe { }"; Diagnostic = 'CS1570' },
    @{ Code = "namespace ParticleLife.Core;`n/// <summary>See <see cref=`"MissingDocumentationType`"/>.</summary>`npublic class DocumentationGateProbe { }"; Diagnostic = 'CS1574' }
)
try {
    foreach ($case in $cases) {
        [IO.File]::WriteAllText($probe, $case.Code)
        $output = & dotnet build $project -c Release --no-restore 2>&1 | Out-String
        if ($LASTEXITCODE -eq 0 -or $output -notmatch "error $($case.Diagnostic)") {
            throw "Documentation gate failed for $($case.Diagnostic):`n$output"
        }
        Write-Output "Verified documentation error $($case.Diagnostic)."
    }
}
finally {
    if (Test-Path -LiteralPath $probe) { Remove-Item -LiteralPath $probe }
}
# Leave the authored library in a successful, unmodified build state.
& dotnet build $project -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Core rebuild after documentation probes failed.' }
