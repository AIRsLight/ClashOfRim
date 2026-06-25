param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$packageId = "AIRsLight.ClashOfRim"
$packageName = "ClashOfRim"
$packageRoot = Join-Path $repoRoot "Build\$packageName"
$versionRoot = Join-Path $packageRoot "1.6"
$assemblyRoot = Join-Path $versionRoot "Assemblies"
$languagesRoot = Join-Path $packageRoot "Languages"
$defsRoot = Join-Path $packageRoot "Defs"
$texturesRoot = Join-Path $packageRoot "Textures"

dotnet build (Join-Path $repoRoot "Source\Client\ClashOfRim.csproj") -c $Configuration --no-restore

New-Item -ItemType Directory -Force -Path (Join-Path $packageRoot "About") | Out-Null
New-Item -ItemType Directory -Force -Path $assemblyRoot | Out-Null

Copy-Item -LiteralPath (Join-Path $repoRoot "About\About.xml") -Destination (Join-Path $packageRoot "About\About.xml") -Force
$previewPath = Join-Path $repoRoot "About\Preview.png"
if (Test-Path -LiteralPath $previewPath) {
    Copy-Item -LiteralPath $previewPath -Destination (Join-Path $packageRoot "About\Preview.png") -Force
}
if (Test-Path -LiteralPath $languagesRoot) {
    Remove-Item -LiteralPath $languagesRoot -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $repoRoot "Languages") -Destination $languagesRoot -Recurse -Force
if (Test-Path -LiteralPath $defsRoot) {
    Remove-Item -LiteralPath $defsRoot -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $repoRoot "Defs") -Destination $defsRoot -Recurse -Force
if (Test-Path -LiteralPath (Join-Path $repoRoot "Textures")) {
    if (Test-Path -LiteralPath $texturesRoot) {
        Remove-Item -LiteralPath $texturesRoot -Recurse -Force
    }
    Copy-Item -LiteralPath (Join-Path $repoRoot "Textures") -Destination $texturesRoot -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $repoRoot "Assemblies\$packageId.dll") -Destination (Join-Path $assemblyRoot "$packageId.dll") -Force

$compatAssembly = Join-Path $repoRoot "Assemblies\ClashOfRim.Compatibility.dll"
if (Test-Path -LiteralPath $compatAssembly) {
    Copy-Item -LiteralPath $compatAssembly -Destination (Join-Path $assemblyRoot "ClashOfRim.Compatibility.dll") -Force
}

$pdbPath = Join-Path $repoRoot "Assemblies\$packageId.pdb"
if (Test-Path -LiteralPath $pdbPath) {
    Copy-Item -LiteralPath $pdbPath -Destination (Join-Path $assemblyRoot "$packageId.pdb") -Force
}

@"
<loadFolders>
  <v1.6>
    <li>/</li>
    <li>1.6</li>
  </v1.6>
</loadFolders>
"@ | Set-Content -LiteralPath (Join-Path $packageRoot "LoadFolders.xml") -Encoding UTF8

Write-Host "Client mod package generated: $packageRoot"
