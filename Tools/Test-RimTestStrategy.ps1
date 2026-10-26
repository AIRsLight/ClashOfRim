#requires -Version 7.4
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'RimTest/tools/RunStorage.ps1')
$parent = Join-Path $root 'LocalOnly/RimTestStrategyTests'
$fixture = Join-Path $parent ([Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $fixture -Force
$checks = 0
function Check([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw "FAILED: $Message" }
    $script:checks++
    Write-Host "PASS $Message"
}
function Expect-SelectionError([scriptblock]$Action, [string]$Expected) {
    $message = ''
    try { & $Action } catch { $message = $_.Exception.Message }
    Check ($message.Contains($Expected)) $Expected
}
try {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Test-RimTestMatrix.ps1') -Destination $fixture
    $matrix = Join-Path $fixture 'Test-RimTestMatrix.ps1'
    $catalog = Join-Path $fixture 'catalog.json'
    '[{"id":"one"},{"id":"two"},{"id":"three"}]' | Set-Content -LiteralPath $catalog
    # Exercise selection/report plumbing with a synthetic consumer, never a game.
    @'
param($RimWorldPath, $HarmonyPath, $RunsDirectory, $Profile, [switch]$Headless, [switch]$SkipBuild)
$evidence = Join-Path $RunsDirectory 'synthetic-run'
$null = New-Item -ItemType Directory -Path $evidence -Force
'{"passed":true,"cases":[{"id":"synthetic"}],"notRun":[]}' | Set-Content -LiteralPath (Join-Path $evidence 'report.json')
'@ | Set-Content -LiteralPath (Join-Path $fixture 'Test-RimTest.ps1')
    $arguments = @{ RimWorldPath = 'unused'; HarmonyPath = 'unused'; CatalogPath = $catalog; RunsDirectory = (Join-Path $fixture 'runs') }
    Expect-SelectionError { & $matrix @arguments } 'Select targeted scenarios'
    Check (!(Test-Path -LiteralPath $arguments.RunsDirectory)) 'default matrix rejection creates no run directory'
    Expect-SelectionError { & $matrix @arguments -Scenario one -FullMatrix } 'Select targeted scenarios'
    Expect-SelectionError { & $matrix @arguments -Scenario unknown } 'Unknown scenario'
    & $matrix @arguments -Scenario two -SkipBuild
    $report = Get-ChildItem -LiteralPath $arguments.RunsDirectory -Directory | ForEach-Object { Get-Content -LiteralPath (Join-Path $_.FullName 'matrix.json') -Raw | ConvertFrom-Json }
    Check ($report.passed -and $report.planned.Count -eq 1 -and $report.planned[0] -eq 'two' -and $report.completed.Count -eq 1) 'targeted matrix runs only the selected scenario'
    $arguments.RunsDirectory = Join-Path $fixture 'explicit-full'
    & $matrix @arguments -FullMatrix -SkipBuild
    $report = Get-ChildItem -LiteralPath $arguments.RunsDirectory -Directory | ForEach-Object { Get-Content -LiteralPath (Join-Path $_.FullName 'matrix.json') -Raw | ConvertFrom-Json }
    Check ($report.passed -and $report.planned.Count -eq 3 -and $report.completed.Count -eq 3) 'explicit full matrix still executes every catalog entry'
    $thirdParty = Join-Path $PSScriptRoot 'Test-RimTestThirdParty.ps1'
    $thirdArguments = @{ RimWorldPath = 'unused'; HarmonyPath = 'unused'; WorkshopRoot = 'unused'; RunsDirectory = (Join-Path $fixture 'third-party') }
    Expect-SelectionError { & $thirdParty @thirdArguments } 'Select targeted profiles'
    Expect-SelectionError { & $thirdParty @thirdArguments -Profile one -FullMatrix } 'Select targeted profiles'
    Check (!(Test-Path -LiteralPath $thirdArguments.RunsDirectory)) 'third-party default rejection does not build or create runs'
    Write-Host "Strategy tests: $checks passed. No game process was launched."
} finally {
    Remove-RimTestTree $parent $fixture
}
