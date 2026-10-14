[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$RimWorldManagedDir,
    [switch]$IncludeClient
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot
try {
    foreach ($project in @('ClashOfRim.Events.Tests', 'ClashOfRim.Save.Tests', 'ClashOfRim.Client.Core.Tests')) {
        & dotnet run --project "Tools/$project/$project.csproj" -c $Configuration
        if ($LASTEXITCODE -ne 0) { throw "$project failed ($LASTEXITCODE)." }
    }

    # This subset is self-contained. The extended smoke scenario needs local SaveSample fixtures.
    Push-Location (Join-Path $repoRoot 'Tools/ClashOfRim.NetworkServer')
    try {
        & dotnet run --project ../ClashOfRim.NetworkSmoke -c $Configuration -- --snapshot-pipeline-only
        if ($LASTEXITCODE -ne 0) { throw "Network snapshot pipeline regressions failed ($LASTEXITCODE)." }
    }
    finally { Pop-Location }

    if ($IncludeClient) {
        if ([string]::IsNullOrWhiteSpace($RimWorldManagedDir)) {
            throw 'IncludeClient requires RimWorldManagedDir pointing to an installed RimWorld 1.7 runtime.'
        }
        . (Join-Path $PSScriptRoot 'ModPackageUtility.ps1')
        $clientDotNet = Get-ClientDotNet
        & $clientDotNet run --project Tools/ClashOfRim.Client.Tests/ClashOfRim.Client.Tests.csproj -c $Configuration "-p:RimWorldManagedDir=$RimWorldManagedDir"
        if ($LASTEXITCODE -ne 0) { throw "ClashOfRim.Client.Tests failed ($LASTEXITCODE)." }
    }
}
finally { Pop-Location }
