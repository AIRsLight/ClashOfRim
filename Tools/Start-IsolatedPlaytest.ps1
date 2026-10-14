[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RimWorldPath,
    [Parameter(Mandatory = $true)][string]$HarmonyPath,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateRange(0, 2)][int]$LaunchClients = 0,
    [switch]$QuickTest
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$gameSource = (Resolve-Path -LiteralPath $RimWorldPath).Path
$harmonySource = (Resolve-Path -LiteralPath $HarmonyPath).Path
foreach ($required in @('RimWorld.exe', 'RimWorld.dll', 'UnityStubs.dll', 'Data', 'Rendering')) {
    if (-not (Test-Path -LiteralPath (Join-Path $gameSource $required))) { throw "Missing 1.7 runtime input: $required" }
}
if (-not (Test-Path -LiteralPath (Join-Path $harmonySource '1.7/Assemblies/0Harmony.dll'))) {
    throw 'HarmonyPath must contain the Harmony 1.7 package.'
}
$gameVersion = (Get-Content -LiteralPath (Join-Path $gameSource 'Version.txt') -TotalCount 1).Trim()
if ($gameVersion -notmatch '^1\.7\.') { throw "Expected RimWorld 1.7, found $gameVersion" }
$knownExpansions = @(Get-ChildItem -LiteralPath (Join-Path $gameSource 'Data') -Directory | ForEach-Object {
    $aboutPath = Join-Path $_.FullName 'About/About.xml'
    if ($_.Name -ne 'Core' -and (Test-Path -LiteralPath $aboutPath)) {
        ([xml](Get-Content -LiteralPath $aboutPath -Raw)).ModMetaData.packageId.ToLowerInvariant()
    }
})

& (Join-Path $PSScriptRoot 'BuildClientMod.ps1') -Configuration $Configuration -RimWorldManagedDir $gameSource
if ($LASTEXITCODE -ne 0) { throw 'Client build failed.' }

# A new directory for every run prevents reuse of old mod settings, saves or test results.
$runRoot = Join-Path $repoRoot ('LocalOnly/Playtests/' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$gameRoot = Join-Path $runRoot 'Game'
New-Item -ItemType Directory -Path $gameRoot -Force | Out-Null
# Copy executable/runtime files so the installation is never modified by test preparation.
Get-ChildItem -LiteralPath $gameSource -File | Copy-Item -Destination $gameRoot
# Game asset trees are large; use read-only-by-convention junctions. Never recursively clean this run directory.
foreach ($assetDirectory in @('Data', 'Rendering')) {
    New-Item -ItemType Junction -Path (Join-Path $gameRoot $assetDirectory) -Target (Join-Path $gameSource $assetDirectory) | Out-Null
}
$modsRoot = Join-Path $gameRoot 'Mods'
New-Item -ItemType Directory -Path $modsRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'Build/ClashOfRim') -Destination (Join-Path $modsRoot 'ClashOfRim') -Recurse
Copy-Item -LiteralPath $harmonySource -Destination (Join-Path $modsRoot 'Harmony') -Recurse

$clients = @()
foreach ($name in @('A', 'B')) {
    $profileRoot = Join-Path $runRoot "Client$name"
    $configRoot = Join-Path $profileRoot 'Config'
    New-Item -ItemType Directory -Path $configRoot -Force | Out-Null
    @'
<?xml version="1.0" encoding="utf-8"?>
<ModsConfigData>
  <version>1.7.4977</version>
  <activeMods>
    <li>brrainz.harmony</li>
    <li>ludeon.rimworld</li>
    <li>airslight.clashofrim</li>
  </activeMods>
  <knownExpansions />
</ModsConfigData>
'@ | Set-Content -LiteralPath (Join-Path $configRoot 'ModsConfig.xml') -Encoding utf8NoBOM
    $modsConfigPath = Join-Path $configRoot 'ModsConfig.xml'
    $modsConfig = [xml](Get-Content -LiteralPath $modsConfigPath -Raw)
    $modsConfig.ModsConfigData.version = $gameVersion
    # Otherwise RimWorld treats installed DLC as newly discovered and enables it on first boot.
    foreach ($packageId in $knownExpansions) {
        $item = $modsConfig.CreateElement('li')
        $item.InnerText = $packageId
        $modsConfig.SelectSingleNode('/ModsConfigData/knownExpansions').AppendChild($item) | Out-Null
    }
    $modsConfig.Save($modsConfigPath)
    @'
<?xml version="1.0" encoding="utf-8"?>
<PrefsData>
  <screenWidth>1280</screenWidth>
  <screenHeight>800</screenHeight>
  <fullscreen>False</fullscreen>
  <volumeMaster>0</volumeMaster>
  <devMode>True</devMode>
</PrefsData>
'@ | Set-Content -LiteralPath (Join-Path $configRoot 'Prefs.xml') -Encoding utf8NoBOM
    $clients += [ordered]@{ name = $name; profile = $profileRoot; processId = $null; startedAtUtc = $null }
}
$manifest = [ordered]@{
    createdAtUtc = [DateTime]::UtcNow.ToString('o')
    gameSource = $gameSource
    gameVersion = $gameVersion
    gameRoot = $gameRoot
    gitCommit = (& git -C $repoRoot rev-parse HEAD)
    gitDirty = [bool](& git -C $repoRoot status --porcelain)
    clientSha256 = (Get-FileHash -LiteralPath (Join-Path $modsRoot 'ClashOfRim/Assemblies/AIRsLight.ClashOfRim.dll')).Hash
    quickTest = [bool]$QuickTest
    clients = $clients
    assetJunctions = @('Data', 'Rendering')
}
$manifestPath = Join-Path $runRoot 'playtest.json'
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
for ($index = 0; $index -lt $LaunchClients; $index++) {
    $client = $clients[$index]
    $arguments = @('"-savedatafolder=' + $client.profile + '"')
    if ($QuickTest) { $arguments += '-quicktest' }
    $process = Start-Process -FilePath (Join-Path $gameRoot 'RimWorld.exe') -WorkingDirectory $gameRoot -ArgumentList $arguments -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $runRoot ("client" + $client.name + '-stdout.log')) `
        -RedirectStandardError (Join-Path $runRoot ("client" + $client.name + '-stderr.log'))
    $client.processId = $process.Id
    $client.startedAtUtc = $process.StartTime.ToUniversalTime().ToString('o')
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
}
Write-Host "Isolated playtest prepared: $manifestPath"
Write-Host 'Launch is not a passing test. Inspect game windows, logs, and gameplay outcomes before recording validation.'
