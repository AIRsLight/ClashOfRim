param(
    [string]$Configuration = "Debug",
    [switch]$NoRestore,
    [switch]$Clean,
    [switch]$IncludeSymbols = $true,
    [switch]$IncludeServerPlugins
)

$ErrorActionPreference = "Stop"

$script = Join-Path $PSScriptRoot "BuildThirdPartyCompatPackage.ps1"
$scriptParameters = @{
    Configuration = $Configuration
}
if ($NoRestore) {
    $scriptParameters.NoRestore = $true
}
if ($Clean) {
    $scriptParameters.Clean = $true
}
if ($IncludeSymbols) {
    $scriptParameters.IncludeSymbols = $true
}
if (-not $IncludeServerPlugins) {
    $scriptParameters.SkipServerPlugins = $true
}

& $script @scriptParameters
