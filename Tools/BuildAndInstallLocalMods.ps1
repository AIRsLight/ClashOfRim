param(
    [string]$Configuration = "Debug",
    [string]$RimWorldPath = "",
    [switch]$NoRestore,
    [switch]$Clean,
    [switch]$IncludeSymbols = $true
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$mainPackageName = "ClashOfRim"
$compatPackageName = "ClashOfRim.ThirdPartyCompat"

function Get-RegistryValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    $item = Get-ItemProperty -LiteralPath $Path -ErrorAction SilentlyContinue
    if ($null -eq $item) {
        return $null
    }

    return $item.$Name
}

function Get-SteamPathFromRegistry {
    $paths = @(
        "HKCU:\Software\Valve\Steam",
        "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam",
        "HKLM:\SOFTWARE\Valve\Steam"
    )

    foreach ($path in $paths) {
        foreach ($name in @("SteamPath", "InstallPath")) {
            $value = Get-RegistryValue -Path $path -Name $name
            if (-not [string]::IsNullOrWhiteSpace($value) -and (Test-Path -LiteralPath $value)) {
                return [System.IO.Path]::GetFullPath($value)
            }
        }
    }

    return $null
}

function Get-RimWorldPathFromUninstallRegistry {
    $paths = @(
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 294100",
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 294100",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 294100"
    )

    foreach ($path in $paths) {
        $installLocation = Get-RegistryValue -Path $path -Name "InstallLocation"
        if (-not [string]::IsNullOrWhiteSpace($installLocation) -and (Test-Path -LiteralPath $installLocation)) {
            return [System.IO.Path]::GetFullPath($installLocation)
        }
    }

    return $null
}

function Read-SteamLibraryPaths {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SteamPath
    )

    $libraryFile = Join-Path $SteamPath "steamapps\libraryfolders.vdf"
    if (-not (Test-Path -LiteralPath $libraryFile)) {
        return @($SteamPath)
    }

    $paths = New-Object System.Collections.Generic.List[string]
    $paths.Add($SteamPath)
    foreach ($line in Get-Content -LiteralPath $libraryFile) {
        if ($line -match '^\s*"path"\s*"(.+)"\s*$') {
            $path = $matches[1] -replace "\\\\", "\"
            if (Test-Path -LiteralPath $path) {
                $paths.Add([System.IO.Path]::GetFullPath($path))
            }
        }
    }

    return $paths | Select-Object -Unique
}

function Get-RimWorldPathFromSteamLibraries {
    $steamPath = Get-SteamPathFromRegistry
    if ([string]::IsNullOrWhiteSpace($steamPath)) {
        return $null
    }

    foreach ($libraryPath in Read-SteamLibraryPaths -SteamPath $steamPath) {
        $candidate = Join-Path $libraryPath "steamapps\common\RimWorld"
        if ((Test-Path -LiteralPath $candidate) -and (Test-Path -LiteralPath (Join-Path $candidate "RimWorldWin64.exe"))) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    return $null
}

function Resolve-RimWorldPath {
    if (-not [string]::IsNullOrWhiteSpace($RimWorldPath)) {
        if (-not (Test-Path -LiteralPath $RimWorldPath)) {
            throw "RimWorld path does not exist: $RimWorldPath"
        }

        return [System.IO.Path]::GetFullPath($RimWorldPath)
    }

    $path = Get-RimWorldPathFromUninstallRegistry
    if (-not [string]::IsNullOrWhiteSpace($path)) {
        return $path
    }

    $path = Get-RimWorldPathFromSteamLibraries
    if (-not [string]::IsNullOrWhiteSpace($path)) {
        return $path
    }

    throw "Could not find RimWorld install path from registry. Pass -RimWorldPath explicitly."
}

function Copy-ModPackage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackageName,
        [Parameter(Mandatory = $true)]
        [string]$SourceRoot,
        [Parameter(Mandatory = $true)]
        [string]$ModsRoot
    )

    if (-not (Test-Path -LiteralPath (Join-Path $SourceRoot "About\About.xml"))) {
        throw "Build package is missing About.xml: $SourceRoot"
    }

    $destination = Join-Path $ModsRoot $PackageName
    $resolvedModsRoot = [System.IO.Path]::GetFullPath($ModsRoot)
    $resolvedDestination = [System.IO.Path]::GetFullPath($destination)
    if (-not $resolvedDestination.StartsWith($resolvedModsRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to install outside Mods directory: $resolvedDestination"
    }

    if (Test-Path -LiteralPath $destination) {
        Remove-Item -LiteralPath $destination -Recurse -Force
    }

    Copy-Item -LiteralPath $SourceRoot -Destination $destination -Recurse -Force
    Write-Host "Installed $PackageName -> $destination"
}

$rimWorldRoot = Resolve-RimWorldPath
$modsRoot = Join-Path $rimWorldRoot "Mods"
if (-not (Test-Path -LiteralPath $modsRoot)) {
    New-Item -ItemType Directory -Force -Path $modsRoot | Out-Null
}

$clientBuildScript = Join-Path $PSScriptRoot "BuildClientMod.ps1"
$compatBuildScript = Join-Path $PSScriptRoot "BuildThirdPartyCompatPackage.ps1"

$clientBuildParameters = @{
    Configuration = $Configuration
}
& $clientBuildScript @clientBuildParameters

$compatBuildParameters = @{
    Configuration = $Configuration
    SkipServerPlugins = $true
}
if ($NoRestore) {
    $compatBuildParameters.NoRestore = $true
}
if ($Clean) {
    $compatBuildParameters.Clean = $true
}
if ($IncludeSymbols) {
    $compatBuildParameters.IncludeSymbols = $true
}
& $compatBuildScript @compatBuildParameters

Copy-ModPackage -PackageName $mainPackageName -SourceRoot (Join-Path $repoRoot "Build\$mainPackageName") -ModsRoot $modsRoot
Copy-ModPackage -PackageName $compatPackageName -SourceRoot (Join-Path $repoRoot "Build\$compatPackageName") -ModsRoot $modsRoot

Write-Host "RimWorld path: $rimWorldRoot"
Write-Host "Local mod install completed."
