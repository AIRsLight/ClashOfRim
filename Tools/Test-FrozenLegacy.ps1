param(
    [string]$PackageRoot = "",
    [ValidateSet("ClashOfRim", "ClashOfRim.ThirdPartyCompat")]
    [string]$PackageName = "ClashOfRim"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -LiteralPath (Join-Path $repoRoot "Legacy/1.6/manifest.json") -Raw | ConvertFrom-Json
$entries = @($manifest.files)
if ($PackageRoot) {
    $prefix = if ($PackageName -eq "ClashOfRim") { "1.6/" } else { "ThirdPartyCompat/1.6/" }
    $entries = @($entries | Where-Object { $_.path.StartsWith($prefix, [StringComparison]::Ordinal) })
    $expected = @($entries | ForEach-Object { $_.path.Substring($prefix.Length) } | Sort-Object)
    $contentRoot = Join-Path $PackageRoot "1.6"
    $actual = @(Get-ChildItem -LiteralPath $contentRoot -File -Recurse | ForEach-Object {
        [IO.Path]::GetRelativePath($contentRoot, $_.FullName).Replace('\', '/')
    } | Sort-Object)
} else {
    $expected = @($entries.path | Sort-Object)
    $actual = @(foreach ($root in $manifest.roots) {
        Get-ChildItem -LiteralPath (Join-Path $repoRoot $root) -File -Recurse | ForEach-Object {
            [IO.Path]::GetRelativePath($repoRoot, $_.FullName).Replace('\', '/')
        }
    }) | Where-Object { $_ -ne "Legacy/1.6/manifest.json" } | Sort-Object
}
if ($entries.Count -eq 0 -or (Compare-Object @($expected) @($actual))) {
    throw "Frozen 1.6 file inventory changed. Do not rebuild or modify the legacy payload."
}
foreach ($entry in $entries) {
    $path = if ($PackageRoot) { Join-Path $contentRoot $entry.path.Substring($prefix.Length) } else { Join-Path $repoRoot $entry.path }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) {
        throw "Frozen 1.6 hash mismatch: $($entry.path)"
    }
}
Write-Host "Frozen 1.6 verified: $($entries.Count) files."
