param(
    [string]$Configuration = "Debug",
    [string]$RimWorldManagedDir = "",
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ModPackageUtility.ps1")
$repoRoot = Split-Path -Parent $PSScriptRoot
$packageRoot = Join-Path $repoRoot "Build/ClashOfRim"
$buildArgs = @("build", (Join-Path $repoRoot "Source/Client/ClashOfRim.csproj"), "-c", $Configuration)
if ($RimWorldManagedDir) { $buildArgs += "-p:RimWorldManagedDir=$RimWorldManagedDir" }
if ($NoRestore) { $buildArgs += "--no-restore" }
$clientDotNet = Get-ClientDotNet
& $clientDotNet @buildArgs
if ($LASTEXITCODE -ne 0) { throw "Client build failed with exit code $LASTEXITCODE" }

Initialize-ModPackage -SourceRoot $repoRoot -PackageRoot $packageRoot
$assemblyRoot = Join-Path $packageRoot "Assemblies"
foreach ($name in @("AIRsLight.ClashOfRim", "ClashOfRim.Protocol", "ClashOfRim.Compatibility")) {
    $dll = Join-Path $repoRoot "Assemblies/$name.dll"
    if (-not (Test-Path -LiteralPath $dll)) { throw "Required client output missing: $dll" }
    Copy-Item -LiteralPath $dll -Destination $assemblyRoot
    $pdb = Join-Path $repoRoot "Assemblies/$name.pdb"
    if (Test-Path -LiteralPath $pdb) { Copy-Item -LiteralPath $pdb -Destination $assemblyRoot }
}
& (Join-Path $PSScriptRoot "Test-FrozenLegacy.ps1") -PackageRoot $packageRoot
Write-Host "Client package generated (active 1.7, frozen 1.6): $packageRoot"
