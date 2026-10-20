#requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$RimWorldPath,
    [Parameter(Mandatory)][string]$HarmonyPath,
    [string]$RunsDirectory,
    [string]$Profile,
    [string]$WorkshopRoot,
    [string[]]$AdditionalMod = @(),
    [string]$ServerPluginsDirectory,
    [switch]$SkipBuild,
    [switch]$Headless
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'ModPackageUtility.ps1')
. (Join-Path $PSScriptRoot 'RimTestModUtility.ps1')
$sdk = Get-ClientDotNet
$scenario = if ($Profile) { Get-Content -LiteralPath $Profile -Raw | ConvertFrom-Json -AsHashtable } else { @{ id = 'core-features'; features = $true } }
if ($scenario.mods) {
    if (!$WorkshopRoot) { throw 'This profile requires WorkshopRoot to resolve the declared third-party packages.' }
    $AdditionalMod = @(Resolve-RimTestWorkshopMods -WorkshopRoot $WorkshopRoot -PackageIds $scenario.mods | ForEach-Object path) + $AdditionalMod
}
if ($scenario.thirdPartyCompatibility) { $AdditionalMod += Join-Path $root 'Build/ClashOfRim.ThirdPartyCompat' }
if ($scenario.mvcfEquipmentVerbs) { $AdditionalMod += Join-Path $PSScriptRoot 'RimTestFixtures/MvcfEquipment' }
if ($scenario.thirdPartyPlugins -and !$ServerPluginsDirectory) { $ServerPluginsDirectory = Join-Path $root 'Build/ServerPlugins' }
if (!$SkipBuild) {
    & (Join-Path $PSScriptRoot 'BuildClientMod.ps1') -Configuration Release -RimWorldManagedDir $RimWorldPath
    if ($scenario.thirdPartyCompatibility) {
        & (Join-Path $PSScriptRoot 'BuildThirdPartyCompatPackage.ps1') -Configuration Release -RimWorldManagedDir $RimWorldPath -SkipServerPlugins:(-not $scenario.thirdPartyPlugins)
    }
    & (Join-Path $root 'RimTest/tools/Build.ps1') -DotNet $sdk -RimWorldManagedDir $RimWorldPath
    foreach ($project in @('ClashOfRim.RimTestBridge','ClashOfRim.GameTests')) {
        & $sdk build (Join-Path $PSScriptRoot $project) -c Release "-p:RimWorldManagedDir=$RimWorldPath"
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
    }
}
if (!$RunsDirectory) { $RunsDirectory = Join-Path $root 'LocalOnly/RimTest' }
$runRoot = Join-Path ([IO.Path]::GetFullPath($RunsDirectory)) ([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$profiles = @{}
foreach ($name in @('A','B')) {
    $profiles[$name] = @{ Language = 'English'; Culture = 'en-US'; Expansions = @() }
    if ($scenario.ContainsKey($name)) {
        $profiles[$name].Language = $scenario[$name].language
        $profiles[$name].Culture = $scenario[$name].culture
    }
    if ($scenario.ContainsKey('expansions')) { $profiles[$name].Expansions = $scenario.expansions }
}
$extension = Join-Path $runRoot 'Bridge'
$runner = Join-Path $runRoot 'Runner'
New-Item -ItemType Directory -Path (Join-Path $extension 'Assemblies'), (Join-Path $runRoot 'Server') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ClashOfRim.RimTestBridge/About') -Destination $extension -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ClashOfRim.RimTestBridge/Defs') -Destination $extension -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ClashOfRim.RimTestBridge/bin/Release/net10.0/ClashOfRim.RimTestBridge.dll') -Destination (Join-Path $extension 'Assemblies')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ClashOfRim.NetworkServer/Localization') -Destination (Join-Path $runRoot 'Server') -Recurse
if ($ServerPluginsDirectory) { Copy-Item -LiteralPath $ServerPluginsDirectory -Destination (Join-Path $runRoot 'Server/Plugins') -Recurse }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ClashOfRim.GameTests/bin/Release/net8.0') -Destination $runner -Recurse
$scenario | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $runRoot 'profile.json') -Encoding utf8NoBOM
$inputs = @((Join-Path $root 'Build/ClashOfRim/Assemblies'),
    (Join-Path ((Get-Content -LiteralPath (Join-Path $root 'RimTest/artifacts/latest-package.txt') -Raw).Trim()) 'Assemblies'),
    $runner,
    (Join-Path $extension 'Assemblies')) | Where-Object { Test-Path -LiteralPath $_ } |
    ForEach-Object { Get-ChildItem -LiteralPath $_ -File -Filter '*.dll' } |
    ForEach-Object { [pscustomobject]@{ path = $_.FullName; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } }
[pscustomobject]@{ repositoryCommit = (git -C $root rev-parse HEAD); frameworkCommit = (git -C (Join-Path $root 'RimTest') rev-parse HEAD);
    executionEnvironment = @{ dotnetProcessorCount = $env:DOTNET_PROCESSOR_COUNT };
    workingTree = @(git -C $root status --porcelain); frameworkWorkingTree = @(git -C (Join-Path $root 'RimTest') status --porcelain);
    additionalMods = @($AdditionalMod | ForEach-Object { [pscustomobject]@{ path = [IO.Path]::GetFullPath($_); files = @(Get-ChildItem -LiteralPath $_ -Recurse -File | Where-Object { $_.Extension -eq '.dll' -or $_.Name -in @('About.xml','LoadFolders.xml') } | ForEach-Object { [pscustomobject]@{ path = $_.FullName; sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash } }) } });
    binaries = @($inputs); profileSha256 = (Get-FileHash -LiteralPath (Join-Path $runRoot 'profile.json')).Hash } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runRoot 'inputs.json') -Encoding utf8NoBOM
Write-Host "Scenario: $($scenario.id); Evidence: $runRoot"
# These scenarios need the game's renderer even when OS input is unavailable.
$useHeadless = $Headless -and $scenario.stability -notin @('gravship', 'ui-layout', 'remote-map-performance')
& (Join-Path $root 'RimTest/tools/Invoke-GameGroup.ps1') -RimWorldPath $RimWorldPath -HarmonyPath $HarmonyPath `
    -RunsDirectory (Join-Path $runRoot 'Clients') -Names @('A','B') -Profiles $profiles -AdditionalMod (@((Join-Path $root 'Build/ClashOfRim')) + $AdditionalMod + @($extension)) -Headless:$useHeadless -AllowDebug -Run {
    param($group)
    @($group.Values | ForEach-Object { $_.manifest }) | ConvertTo-Json -AsArray | Set-Content (Join-Path $runRoot 'clients.json') -Encoding utf8NoBOM
    & dotnet (Join-Path $runner 'ClashOfRim.GameTests.dll') $runRoot $group['A'].pipe $group['B'].pipe (Join-Path $runRoot 'profile.json')
    if ($LASTEXITCODE -ne 0) { throw "Real-game suite failed. Evidence: $runRoot" }
    Write-Host "Evidence: $runRoot"
}
