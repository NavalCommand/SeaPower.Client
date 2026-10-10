param(
    [string]$Preset = 'windows-msvc-release',
    [string]$Configuration = 'Release',
    [string]$CMake = 'cmake'
)
$ErrorActionPreference = 'Stop'
& $CMake --build "$PSScriptRoot/../o/$Preset" --config $Configuration --target SeaPower.Client.Managed
if ($LASTEXITCODE) {
    throw 'SeaPower.Client.Managed failed; configure the selected preset before building.'
}
