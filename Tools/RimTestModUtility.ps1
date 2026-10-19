function Resolve-RimTestWorkshopMods {
    param([Parameter(Mandatory)][string]$WorkshopRoot, [Parameter(Mandatory)][string[]]$PackageIds)
    $catalog = @{}
    foreach ($dir in Get-ChildItem -LiteralPath $WorkshopRoot -Directory) {
        $about = Join-Path $dir.FullName 'About/About.xml'
        if (!(Test-Path -LiteralPath $about)) { continue }
        [xml]$meta = Get-Content -LiteralPath $about -Raw
        $id = [string]$meta.ModMetaData.packageId
        if ($id) { $catalog[$id] = @($catalog[$id]) + @([pscustomobject]@{ path = $dir.FullName; meta = $meta.ModMetaData }) }
    }
    foreach ($id in $PackageIds) {
        $matches = @($catalog[$id] | Where-Object { $_ })
        if ($matches.Count -ne 1) { throw "Expected one installed package: $id; found $($matches.Count)." }
        $matches[0]
    }
}
