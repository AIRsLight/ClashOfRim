#requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$RimWorldPath,
    [Parameter(Mandatory)][string]$HarmonyPath,
    [Parameter(Mandatory)][string]$WorkshopRoot,
    [Parameter(Mandatory)][string]$RunsDirectory,
    [string[]]$Profile,
    [switch]$FullMatrix,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
if ((!$Profile.Count -and !$FullMatrix) -or ($Profile.Count -and $FullMatrix)) {
    throw 'Select targeted profiles with -Profile, or explicitly request every profile with -FullMatrix.'
}
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'RimTest/tools/RunStorage.ps1')
. (Join-Path $PSScriptRoot 'ModPackageUtility.ps1')
. (Join-Path $PSScriptRoot 'RimTestModUtility.ps1')
$sdk = Get-ClientDotNet
$specs = @(Get-Content (Join-Path $PSScriptRoot 'RimTestThirdPartyProfiles.json') -Raw | ConvertFrom-Json | Where-Object { !$Profile -or $_.id -in $Profile })
if (!$specs.Count -or ($Profile -and @($Profile | Where-Object { $_ -notin $specs.id }).Count)) { throw 'Unknown or empty profile selection.' }
if (!$SkipBuild) {
    & (Join-Path $PSScriptRoot 'BuildClientMod.ps1') -Configuration Release -RimWorldManagedDir $RimWorldPath
    & (Join-Path $PSScriptRoot 'BuildThirdPartyCompatPackage.ps1') -Configuration Release -RimWorldManagedDir $RimWorldPath -SkipServerPlugins
    & (Join-Path $root 'RimTest/tools/Build.ps1') -DotNet $sdk -RimWorldManagedDir $RimWorldPath
    foreach ($project in @('ClashOfRim.RimTestBridge','ClashOfRim.GameTests')) {
        & $sdk build (Join-Path $PSScriptRoot $project) -c Release "-p:RimWorldManagedDir=$RimWorldPath"
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
    }
}
$matrix = Join-Path ([IO.Path]::GetFullPath($RunsDirectory)) ('startup-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$null = Register-RimTestArtifactStorage -RunRoot $matrix -Directories @('Runner','Bridge')
$matrixPassed = $false
try {
    $bridge = Join-Path $matrix 'Bridge'
    New-Item -ItemType Directory -Force (Join-Path $bridge 'Assemblies') | Out-Null
    foreach ($dir in @('About','Defs')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot "ClashOfRim.RimTestBridge/$dir") -Destination $bridge -Recurse }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ClashOfRim.RimTestBridge/bin/Release/net10.0/ClashOfRim.RimTestBridge.dll') -Destination (Join-Path $bridge 'Assemblies')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ClashOfRim.GameTests/bin/Release/net8.0') -Destination (Join-Path $matrix 'Runner') -Recurse
    $results = [Collections.Generic.List[object]]::new()
    foreach ($spec in $specs) {
        Write-Host "PROFILE $($spec.id)"
        $manifest = $null; $failure = $null; $passed = $false; $errors = @()
        $caseRoot = Join-Path $matrix $spec.id
        New-Item -ItemType Directory -Force $caseRoot | Out-Null
        $profilePath = Join-Path $caseRoot 'profile.json'
        $spec | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $profilePath -Encoding utf8NoBOM
        try {
            $entries = @(Resolve-RimTestWorkshopMods -WorkshopRoot $WorkshopRoot -PackageIds $spec.mods)
            # This is an explicit compatibility probe: preserve unsupported packages unchanged and record their actual load failure.
            $entries | ForEach-Object { [pscustomobject]@{ id = [string]$_.meta.packageId; path = $_.path; versions = @($_.meta.supportedVersions.li) } } |
                ConvertTo-Json -AsArray -Depth 5 | Set-Content (Join-Path $caseRoot 'installed.json') -Encoding utf8NoBOM
            $mods = @((Join-Path $root 'Build/ClashOfRim')) + @($entries.path) + @((Join-Path $root 'Build/ClashOfRim.ThirdPartyCompat'), $bridge)
            $manifest = & (Join-Path $root 'RimTest/tools/Start-Game.ps1') -RimWorldPath $RimWorldPath -HarmonyPath $HarmonyPath -RunsDirectory $caseRoot -AdditionalMod $mods -Headless -AllowDebug
            $instance = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
            $files = @(Get-ChildItem -LiteralPath (Join-Path $instance.gameRoot 'Mods') -Recurse -File | Where-Object { $_.Extension -eq '.dll' -or $_.Name -in @('About.xml','LoadFolders.xml') } | ForEach-Object {
                [pscustomobject]@{ path = [IO.Path]::GetRelativePath($instance.gameRoot, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
            })
            @{ commit = (git -C $root rev-parse HEAD); workingTree = @(git -C $root status --porcelain); files = $files } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $caseRoot 'inputs.json') -Encoding utf8NoBOM
            & dotnet (Join-Path $matrix 'Runner/ClashOfRim.GameTests.dll') --third-party-startup $instance.pipe $caseRoot $profilePath
            if ($LASTEXITCODE -ne 0) { throw 'Startup admission failed; inspect report and native logs.' }
            $passed = $true
        }
        catch { $failure = $_.Exception.Message; Write-Warning "$($spec.id): $failure" }
        finally {
            if ($manifest) {
                try { & (Join-Path $root 'RimTest/tools/Stop-Game.ps1') -Manifest $manifest -DeferStorage }
                catch { $passed = $false; $failure = "Cleanup failed: $_" }
                $errors = @(Get-ChildItem -LiteralPath (Split-Path $manifest -Parent) -File | Where-Object Name -in @('stdout.log','stderr.log') |
                    Select-String -Pattern '^\d{2}:\d{2}:\d{2}\s+(ERROR|FATAL)\b' | Where-Object { $_.Line -notmatch 'Resolution too small \(0x0\), resetting UI scale to 1\.' } | ForEach-Object Line)
                if ($errors.Count) { $passed = $false; $failure = (@($failure, 'Native process log contains errors.') | Where-Object { $_ }) -join ' ' }
                try { Complete-RimTestRunStorage -RunRoot (Split-Path $manifest -Parent) -Outcome $(if ($passed) { 'Passed' } else { 'Failed' }) }
                catch { $passed = $false; $failure = "$failure Storage cleanup failed: $_" }
            }
            $results.Add([pscustomobject]@{ id = $spec.id; passed = $passed; manifest = $manifest; failure = $failure; nativeErrors = $errors; gameplayEffectsTested = $false })
            @{ planned = @($specs.id); complete = $results.Count -eq $specs.Count; results = $results.ToArray() } | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $matrix 'matrix-report.json') -Encoding utf8NoBOM
        }
    }
    Write-Host "Evidence: $matrix"
    if (@($results | Where-Object { !$_.passed }).Count) { throw 'One or more third-party startup profiles failed. All profiles were attempted.' }
    $matrixPassed = $true
} finally {
    Complete-RimTestRunStorage -RunRoot $matrix -Outcome $(if ($matrixPassed) { 'Passed' } else { 'Failed' })
}
