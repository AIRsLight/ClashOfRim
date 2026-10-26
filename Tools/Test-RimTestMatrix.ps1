#requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$RimWorldPath,
    [Parameter(Mandatory)][string]$HarmonyPath,
    [string]$RunsDirectory,
    [string]$CatalogPath = (Join-Path $PSScriptRoot 'RimTestScenarios.json'),
    [string[]]$Scenario = @(),
    [switch]$FullMatrix,
    [switch]$Headless,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
if ((!$Scenario.Count -and !$FullMatrix) -or ($Scenario.Count -and $FullMatrix)) {
    throw 'Select targeted scenarios with -Scenario, or explicitly request every scenario with -FullMatrix.'
}
$root = Split-Path $PSScriptRoot -Parent
$catalog = @(Get-Content -LiteralPath $CatalogPath -Raw | ConvertFrom-Json)
if (@($catalog.id | Select-Object -Unique).Count -ne $catalog.Count) { throw 'Duplicate scenario IDs.' }
foreach ($id in $Scenario) { if ($id -notin $catalog.id) { throw "Unknown scenario: $id" } }
if ($Scenario.Count) { $catalog = @($catalog | Where-Object id -In $Scenario) }
if (!$RunsDirectory) { $RunsDirectory = Join-Path $root 'LocalOnly/RimTestMatrix' }
$matrixRoot = Join-Path ([IO.Path]::GetFullPath($RunsDirectory)) ([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $matrixRoot -Force | Out-Null
$results = [System.Collections.Generic.List[object]]::new()
$built = [bool]$SkipBuild
function Save-MatrixReport {
    [pscustomobject]@{ passed = @($results | Where-Object { !$_.passed }).Count -eq 0 -and $results.Count -eq $catalog.Count;
        planned = @($catalog.id); completed = $results.ToArray(); notAttempted = @($catalog.id | Where-Object { $_ -notin $results.id }); fullCoverage = $false } |
        ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $matrixRoot 'matrix.json') -Encoding utf8NoBOM
}
Save-MatrixReport
foreach ($profile in $catalog) {
    $profilePath = Join-Path $matrixRoot ($profile.id + '.json')
    $profile | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $profilePath -Encoding utf8NoBOM
    $destination = Join-Path $matrixRoot $profile.id
    $failure = $null
    try {
        & (Join-Path $PSScriptRoot 'Test-RimTest.ps1') -RimWorldPath $RimWorldPath -HarmonyPath $HarmonyPath -RunsDirectory $destination -Profile $profilePath -Headless:$Headless -SkipBuild:$built
        $built = $true
    }
    catch { $failure = $_.ToString(); Write-Warning "$($profile.id): $failure" }
    # Do not recurse through Game/Data junctions into installed assets.
    $reportPath = @(Get-ChildItem -LiteralPath $destination -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Get-Item -LiteralPath (Join-Path $_.FullName 'report.json') -ErrorAction SilentlyContinue }) | Select-Object -First 1
    $report = if ($reportPath) { Get-Content -LiteralPath $reportPath.FullName -Raw | ConvertFrom-Json } else { $null }
    $results.Add([pscustomobject]@{ id = $profile.id; passed = !$failure -and $null -ne $report -and $report.passed; report = $reportPath.FullName;
        executed = $(if ($report) { @($report.cases).Count } else { 0 }); notRun = @($report.notRun); failure = $failure })
    # A gameplay failure does not require rebuilding identical binaries for every following profile.
    if ($reportPath) { $built = $true }
    Save-MatrixReport
}
Write-Host "Matrix evidence: $matrixRoot"
if (@($results | Where-Object { !$_.passed }).Count) { throw 'RimTest matrix failed; inspect matrix.json and each report. Failed or unexecuted cases are never counted as passes.' }
