param(
    [string]$Configuration = "Debug",
    [switch]$NoRestore,
    [switch]$Clean,
    [switch]$IncludeSymbols = $true,
    [switch]$SkipServerPlugins
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$packageId = "AIRsLight.ClashOfRim.ThirdPartyCompat"
$packageName = "ClashOfRim.ThirdPartyCompat"
$packageRoot = Join-Path $repoRoot "Build\$packageName"
$versionRoot = Join-Path $packageRoot "1.6"
$assemblyRoot = Join-Path $versionRoot "Assemblies"
$serverPluginRoot = Join-Path $repoRoot "Build\ServerPlugins"
$modProject = Join-Path $repoRoot "ThirdPartyCompat\Source\ClashOfRim.ThirdPartyCompat.csproj"
$serverPluginProjectsRoot = Join-Path $repoRoot "ThirdPartyCompat\ServerPlugins"
$aboutSource = Join-Path $repoRoot "ThirdPartyCompat\About\About.xml"
$previewSource = Join-Path $repoRoot "ThirdPartyCompat\About\Preview.png"
$languagesSource = Join-Path $repoRoot "ThirdPartyCompat\Languages"
$defsSource = Join-Path $repoRoot "ThirdPartyCompat\Defs"

function Invoke-DotNetBuild {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath
    )

    $args = @("build", $ProjectPath, "-c", $Configuration)
    if ($NoRestore) {
        $args += "--no-restore"
    }

    dotnet @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed for $ProjectPath with exit code $LASTEXITCODE"
    }
}

function Copy-RequiredFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Source,
        [Parameter(Mandatory = $true)]
        [string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source)) {
        throw "Required build output missing: $Source"
    }

    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Assert-MainModDependency {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AboutPath
    )

    [xml]$about = Get-Content -LiteralPath $AboutPath
    $dependencies = @($about.ModMetaData.modDependencies.li | ForEach-Object { $_.packageId } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $loadAfter = @($about.ModMetaData.loadAfter.li | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($dependencies -notcontains "AIRsLight.ClashOfRim") {
        throw "Third-party compatibility About.xml must declare AIRsLight.ClashOfRim in modDependencies."
    }

    if ($loadAfter -notcontains "AIRsLight.ClashOfRim") {
        throw "Third-party compatibility About.xml must load after AIRsLight.ClashOfRim."
    }
}

Assert-MainModDependency -AboutPath $aboutSource

if ($Clean -and (Test-Path -LiteralPath $packageRoot)) {
    $resolvedBuildRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "Build"))
    $resolvedPackageRoot = [System.IO.Path]::GetFullPath($packageRoot)
    if (-not $resolvedPackageRoot.StartsWith($resolvedBuildRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean outside Build directory: $resolvedPackageRoot"
    }

    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}

if (-not $SkipServerPlugins -and $Clean -and (Test-Path -LiteralPath $serverPluginRoot)) {
    $resolvedBuildRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "Build"))
    $resolvedServerPluginRoot = [System.IO.Path]::GetFullPath($serverPluginRoot)
    if (-not $resolvedServerPluginRoot.StartsWith($resolvedBuildRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean outside Build directory: $resolvedServerPluginRoot"
    }

    Remove-Item -LiteralPath $serverPluginRoot -Recurse -Force
}

Invoke-DotNetBuild -ProjectPath $modProject
$serverPluginProjects = @()
if (-not $SkipServerPlugins) {
    $serverPluginProjects = Get-ChildItem -Path $serverPluginProjectsRoot -Recurse -Filter "*.ServerPlugin.csproj" |
        Sort-Object FullName
    if ($serverPluginProjects.Count -eq 0) {
        throw "No third-party compatibility server plugin projects found under $serverPluginProjectsRoot"
    }

    foreach ($project in $serverPluginProjects) {
        Invoke-DotNetBuild -ProjectPath $project.FullName
    }
}

New-Item -ItemType Directory -Force -Path (Join-Path $packageRoot "About") | Out-Null
New-Item -ItemType Directory -Force -Path $assemblyRoot | Out-Null
if (-not $SkipServerPlugins) {
    New-Item -ItemType Directory -Force -Path $serverPluginRoot | Out-Null
}

Copy-RequiredFile -Source $aboutSource -Destination (Join-Path $packageRoot "About\About.xml")
if (Test-Path -LiteralPath $previewSource) {
    Copy-Item -LiteralPath $previewSource -Destination (Join-Path $packageRoot "About\Preview.png") -Force
}
Copy-RequiredFile -Source (Join-Path $repoRoot "Assemblies\$packageId\$packageId.dll") -Destination (Join-Path $assemblyRoot "$packageId.dll")

if (Test-Path -LiteralPath $languagesSource) {
    $languagesTarget = Join-Path $packageRoot "Languages"
    if (Test-Path -LiteralPath $languagesTarget) {
        Remove-Item -LiteralPath $languagesTarget -Recurse -Force
    }

    Copy-Item -LiteralPath $languagesSource -Destination $languagesTarget -Recurse -Force
}

if (Test-Path -LiteralPath $defsSource) {
    $defsTarget = Join-Path $packageRoot "Defs"
    if (Test-Path -LiteralPath $defsTarget) {
        Remove-Item -LiteralPath $defsTarget -Recurse -Force
    }

    Copy-Item -LiteralPath $defsSource -Destination $defsTarget -Recurse -Force
}

if ($IncludeSymbols) {
    $modPdb = Join-Path $repoRoot "Assemblies\$packageId\$packageId.pdb"
    if (Test-Path -LiteralPath $modPdb) {
        Copy-Item -LiteralPath $modPdb -Destination (Join-Path $assemblyRoot "$packageId.pdb") -Force
    }
}

if (-not $SkipServerPlugins) {
    foreach ($project in $serverPluginProjects) {
        [xml]$projectXml = Get-Content -LiteralPath $project.FullName
        $assemblyName = [string]$projectXml.Project.PropertyGroup.AssemblyName
        if ([string]::IsNullOrWhiteSpace($assemblyName)) {
            $assemblyName = [System.IO.Path]::GetFileNameWithoutExtension($project.Name)
        }

        $outputDirectory = Join-Path $repoRoot "Assemblies\$assemblyName"
        Copy-RequiredFile -Source (Join-Path $outputDirectory "$assemblyName.dll") -Destination (Join-Path $serverPluginRoot "$assemblyName.dll")

        $deps = Join-Path $outputDirectory "$assemblyName.deps.json"
        if (Test-Path -LiteralPath $deps) {
            Copy-Item -LiteralPath $deps -Destination (Join-Path $serverPluginRoot "$assemblyName.deps.json") -Force
        }

        if ($IncludeSymbols) {
            $pdb = Join-Path $outputDirectory "$assemblyName.pdb"
            if (Test-Path -LiteralPath $pdb) {
                Copy-Item -LiteralPath $pdb -Destination (Join-Path $serverPluginRoot "$assemblyName.pdb") -Force
            }
        }
    }
}

@"
<loadFolders>
  <v1.6>
    <li>/</li>
    <li>1.6</li>
  </v1.6>
</loadFolders>
"@ | Set-Content -LiteralPath (Join-Path $packageRoot "LoadFolders.xml") -Encoding UTF8

Write-Host "Third-party compatibility mod package generated: $packageRoot"
if ($SkipServerPlugins) {
    Write-Host "Server plugin package skipped."
}
else {
    Write-Host "Server plugin package generated: $serverPluginRoot"
}
