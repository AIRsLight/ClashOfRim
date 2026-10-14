param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [switch]$NoRestore,
    [switch]$SkipThirdPartyCompat
)

# Server-only packaging remains usable with the .NET 8 SDK.
& (Join-Path $PSScriptRoot "BuildServerPackage.ps1") @PSBoundParameters
