param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [switch]$NoRestore,
    [switch]$SkipThirdPartyCompat
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "Tools\ClashOfRim.NetworkServer\ClashOfRim.NetworkServer.csproj"
$serverSourceRoot = Join-Path $repoRoot "Tools\ClashOfRim.NetworkServer"
$buildRoot = Join-Path $repoRoot "Build"
$packageRoot = Join-Path $buildRoot "ClashOfRim.NetworkServer"
$publishRoot = Join-Path $packageRoot $RuntimeIdentifier

$resolvedBuildRoot = [System.IO.Path]::GetFullPath($buildRoot)
$resolvedPublishRoot = [System.IO.Path]::GetFullPath($publishRoot)
if (-not $resolvedPublishRoot.StartsWith($resolvedBuildRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to publish outside Build directory: $resolvedPublishRoot"
}

if (-not $SkipThirdPartyCompat) {
    $thirdPartyBuildScript = Join-Path $repoRoot "Tools\BuildThirdPartyCompatPackage.ps1"
    if (Test-Path -LiteralPath $thirdPartyBuildScript) {
        $thirdPartyBuildArgs = @{
            Configuration = $Configuration
            Clean = $true
        }
        if ($NoRestore) {
            $thirdPartyBuildArgs.NoRestore = $true
        }

        & $thirdPartyBuildScript @thirdPartyBuildArgs
        if ($LASTEXITCODE -ne 0) {
            throw "third-party compatibility package build failed with exit code $LASTEXITCODE"
        }
    }
}

if (Test-Path -LiteralPath $publishRoot) {
    Remove-Item -LiteralPath $publishRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null

$publishArgs = @(
    "publish",
    $projectPath,
    "-c", $Configuration,
    "-r", $RuntimeIdentifier,
    "--self-contained", "false",
    "-o", $publishRoot
)
if ($NoRestore) {
    $publishArgs += "--no-restore"
}

dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

foreach ($runtimeDirectory in @("Data", "Logs")) {
    $runtimePath = Join-Path $publishRoot $runtimeDirectory
    if (Test-Path -LiteralPath $runtimePath) {
        Remove-Item -LiteralPath $runtimePath -Recurse -Force
    }
}

Get-ChildItem -LiteralPath $publishRoot -File -Filter "appsettings*.json" -ErrorAction SilentlyContinue |
    Where-Object { -not [string]::Equals($_.Name, "appsettings.example.json", [System.StringComparison]::OrdinalIgnoreCase) } |
    Remove-Item -Force

$localizationSource = Join-Path $serverSourceRoot "Localization"
$localizationTarget = Join-Path $publishRoot "Localization"
if (Test-Path -LiteralPath $localizationTarget) {
    Remove-Item -LiteralPath $localizationTarget -Recurse -Force
}
Copy-Item -LiteralPath $localizationSource -Destination $localizationTarget -Recurse -Force

$configSource = Join-Path $serverSourceRoot "Config"
if (Test-Path -LiteralPath $configSource) {
    $configTarget = Join-Path $publishRoot "Config"
    if (Test-Path -LiteralPath $configTarget) {
        Remove-Item -LiteralPath $configTarget -Recurse -Force
    }
    Copy-Item -LiteralPath $configSource -Destination $configTarget -Recurse -Force
}

$pluginsTarget = Join-Path $publishRoot "Plugins"
if (Test-Path -LiteralPath $pluginsTarget) {
    Remove-Item -LiteralPath $pluginsTarget -Recurse -Force
}

$pluginSources = @()
$centralPluginSource = Join-Path $buildRoot "ServerPlugins"
if (Test-Path -LiteralPath $centralPluginSource) {
    $pluginSources += $centralPluginSource
}

$pluginSources += Get-ChildItem -Path $buildRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { -not [string]::Equals($_.Name, "ServerPlugins", [System.StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object { Join-Path $_.FullName "ServerPlugins" } |
    Where-Object { Test-Path -LiteralPath $_ }

$pluginSources = @($pluginSources | Select-Object -Unique)
if ($pluginSources) {
    New-Item -ItemType Directory -Force -Path $pluginsTarget | Out-Null
    foreach ($pluginSource in $pluginSources) {
        Copy-Item -Path (Join-Path $pluginSource "*") -Destination $pluginsTarget -Force
    }
}

Write-Host "Windows server package generated: $publishRoot"
