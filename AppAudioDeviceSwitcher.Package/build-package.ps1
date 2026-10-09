param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$msbuild = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
$project = Join-Path $PSScriptRoot "AppAudioDeviceSwitcher.Package.wapproj"

if (-not (Test-Path $msbuild)) {
    throw "MSBuild not found at $msbuild"
}

if ($Clean) {
    & $msbuild $project /t:Clean /p:Configuration=$Configuration /p:Platform=x64
}

& $msbuild $project `
    /restore `
    /t:Build `
    /p:Configuration=$Configuration `
    /p:Platform=x64 `
    /p:UapAppxPackageBuildMode=SideloadOnly `
    /p:GenerateAppInstallerFile=false `
    /p:AppxBundle=Never `
    /p:AppxPackageSigningEnabled=false
