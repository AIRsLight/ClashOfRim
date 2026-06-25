param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [switch]$NoRestore,
    [switch]$SkipThirdPartyCompat
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "Tools/ClashOfRim.NetworkServer/ClashOfRim.NetworkServer.csproj"
$serverSourceRoot = Join-Path $repoRoot "Tools/ClashOfRim.NetworkServer"
$buildRoot = Join-Path $repoRoot "Build"
$packageRoot = Join-Path $buildRoot "ClashOfRim.NetworkServer"
$publishRoot = Join-Path $packageRoot $RuntimeIdentifier
$serverPluginRoot = Join-Path $buildRoot "ServerPlugins"
$serverPluginProjectsRoot = Join-Path $repoRoot "ThirdPartyCompat/ServerPlugins"

function Assert-ChildPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Parent,
        [Parameter(Mandatory = $true)]
        [string]$Child
    )

    $resolvedParent = [System.IO.Path]::GetFullPath($Parent)
    $resolvedChild = [System.IO.Path]::GetFullPath($Child)
    $parentWithSeparator = $resolvedParent.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $isParent = [string]::Equals($resolvedChild, $resolvedParent, [System.StringComparison]::OrdinalIgnoreCase)
    $isChild = $resolvedChild.StartsWith($parentWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)
    if (-not ($isParent -or $isChild)) {
        throw "Refusing to operate outside expected directory: $resolvedChild"
    }
}

function Invoke-DotNetBuild {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath
    )

    $buildArgs = @("build", $ProjectPath, "-c", $Configuration)
    if ($NoRestore) {
        $buildArgs += "--no-restore"
    }

    dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed for $ProjectPath with exit code $LASTEXITCODE"
    }
}

function Copy-OptionalFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Source,
        [Parameter(Mandatory = $true)]
        [string]$Destination
    )

    if (Test-Path -LiteralPath $Source) {
        Copy-Item -LiteralPath $Source -Destination $Destination -Force
    }
}

function Build-ThirdPartyServerPlugins {
    if (-not (Test-Path -LiteralPath $serverPluginProjectsRoot)) {
        Write-Host "Third-party compatibility server plugin source not found; skipping plugin build."
        return
    }

    if (Test-Path -LiteralPath $serverPluginRoot) {
        Assert-ChildPath -Parent $buildRoot -Child $serverPluginRoot
        Remove-Item -LiteralPath $serverPluginRoot -Recurse -Force
    }

    $serverPluginProjects = @(Get-ChildItem -Path $serverPluginProjectsRoot -Recurse -Filter "*.ServerPlugin.csproj" | Sort-Object FullName)
    if ($serverPluginProjects.Count -eq 0) {
        Write-Host "No third-party compatibility server plugin projects found; skipping plugin build."
        return
    }

    foreach ($project in $serverPluginProjects) {
        Invoke-DotNetBuild -ProjectPath $project.FullName
    }

    New-Item -ItemType Directory -Force -Path $serverPluginRoot | Out-Null
    foreach ($project in $serverPluginProjects) {
        [xml]$projectXml = Get-Content -LiteralPath $project.FullName
        $assemblyName = [string]$projectXml.Project.PropertyGroup.AssemblyName
        if ([string]::IsNullOrWhiteSpace($assemblyName)) {
            $assemblyName = [System.IO.Path]::GetFileNameWithoutExtension($project.Name)
        }

        $outputDirectory = Join-Path $repoRoot "Assemblies/$assemblyName"
        $dll = Join-Path $outputDirectory "$assemblyName.dll"
        if (-not (Test-Path -LiteralPath $dll)) {
            throw "Required server plugin output missing: $dll"
        }

        Copy-Item -LiteralPath $dll -Destination (Join-Path $serverPluginRoot "$assemblyName.dll") -Force
        Copy-OptionalFile -Source (Join-Path $outputDirectory "$assemblyName.deps.json") -Destination (Join-Path $serverPluginRoot "$assemblyName.deps.json")
        Copy-OptionalFile -Source (Join-Path $outputDirectory "$assemblyName.pdb") -Destination (Join-Path $serverPluginRoot "$assemblyName.pdb")
    }

    Write-Host "Third-party compatibility server plugins generated: $serverPluginRoot"
}

Assert-ChildPath -Parent $buildRoot -Child $publishRoot

if (-not $SkipThirdPartyCompat) {
    Build-ThirdPartyServerPlugins
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

if (-not $SkipThirdPartyCompat -and (Test-Path -LiteralPath $serverPluginRoot)) {
    New-Item -ItemType Directory -Force -Path $pluginsTarget | Out-Null
    Copy-Item -Path (Join-Path $serverPluginRoot "*") -Destination $pluginsTarget -Force
}

Write-Host "Server package generated: $publishRoot"
