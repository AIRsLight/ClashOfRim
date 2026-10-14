function Get-ClientDotNet {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) {
        $version = & $command.Source --version
        if ($LASTEXITCODE -eq 0 -and [int]($version.Split('.')[0]) -ge 10) {
            return $command.Source
        }
    }
    # Optional repository-local SDK; never change the machine/user PATH.
    $binary = if ($IsWindows) { "dotnet.exe" } else { "dotnet" }
    $local = Join-Path $PSScriptRoot "../LocalOnly/toolchains/dotnet/$binary"
    if (Test-Path -LiteralPath $local) {
        $version = & $local --version
        if ($LASTEXITCODE -eq 0 -and [int]($version.Split('.')[0]) -ge 10) { return $local }
    }
    throw "RimWorld 1.7 client builds require .NET 10 SDK. Install it or add it to PATH."
}

function Assert-BuildChildPath {
    param([string]$Path)
    $root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../Build"))
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to replace a directory outside Build: $resolved"
    }
}

function Initialize-ModPackage {
    param([string]$SourceRoot, [string]$PackageRoot)
    & (Join-Path $PSScriptRoot "Test-FrozenLegacy.ps1")
    Assert-BuildChildPath $PackageRoot
    if (Test-Path -LiteralPath $PackageRoot) {
        Remove-Item -LiteralPath $PackageRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path (Join-Path $PackageRoot "Assemblies") | Out-Null
    foreach ($folder in @("About", "Defs", "Languages", "Textures", "Sounds", "Patches", "1.6")) {
        $source = Join-Path $SourceRoot $folder
        if (Test-Path -LiteralPath $source) {
            Copy-Item -LiteralPath $source -Destination $PackageRoot -Recurse
        }
    }
    Copy-Item -LiteralPath (Join-Path $SourceRoot "LoadFolders.xml") -Destination $PackageRoot
}
