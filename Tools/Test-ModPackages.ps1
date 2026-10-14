param([string]$BuildRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) "Build"))
$ErrorActionPreference = "Stop"
foreach ($name in @("ClashOfRim", "ClashOfRim.ThirdPartyCompat")) {
    $root = [IO.Path]::GetFullPath((Join-Path $BuildRoot $name))
    & (Join-Path $PSScriptRoot "Test-FrozenLegacy.ps1") -PackageRoot $root -PackageName $name
    [xml]$folders = Get-Content -LiteralPath (Join-Path $root "LoadFolders.xml")
    [xml]$about = Get-Content -LiteralPath (Join-Path $root "About/About.xml")
    foreach ($version in @("1.6", "1.7")) {
        $loadPaths = @($folders.loadFolders."v$version".li | ForEach-Object { [string]$_ })
        $expectedPath = if ($version -eq "1.6") { "1.6" } else { "/" }
        if ($loadPaths.Count -ne 1 -or $loadPaths[0] -ne $expectedPath) {
            throw "$name $version must load only $expectedPath; mixed version resources are forbidden."
        }
        if (@($about.ModMetaData.supportedVersions.li) -notcontains $version) {
            throw "$name About.xml does not declare $version."
        }
        $contentRoot = if ($version -eq "1.6") { Join-Path $root "1.6" } else { $root }
        $expectedAssemblies = if ($name -eq "ClashOfRim") {
            @("AIRsLight.ClashOfRim.dll", "ClashOfRim.Compatibility.dll", "ClashOfRim.Protocol.dll")
        } else { @("AIRsLight.ClashOfRim.ThirdPartyCompat.dll") }
        $actualAssemblies = @(Get-ChildItem -LiteralPath (Join-Path $contentRoot "Assemblies") -Filter *.dll -File -Recurse | ForEach-Object Name)
        if (Compare-Object $expectedAssemblies $actualAssemblies) {
            throw "$name $version contains unexpected or missing assemblies (including host/Harmony duplicates)."
        }
    }
    Write-Host "${name}: 1.6/1.7 content isolation verified."
}
