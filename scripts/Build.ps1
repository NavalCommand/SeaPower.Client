param([string]$Preset='windows-msvc-release',[string]$CMake='cmake',[string]$BuildCMakeSource)
$ErrorActionPreference='Stop'
& "$PSScriptRoot/Build-Native.ps1" -Preset $Preset -CMake $CMake -BuildCMakeSource $BuildCMakeSource
