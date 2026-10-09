param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [string]$PackageVersion = "1.0.0.0",
    [switch]$Install
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot "publish\win-x64"
$layoutDir = Join-Path $PSScriptRoot "artifacts\layout"
$outputDir = Join-Path $PSScriptRoot "artifacts"
$manifestPath = Join-Path $PSScriptRoot "Package.appxmanifest"
$appxManifestPath = Join-Path $layoutDir "AppxManifest.xml"
$packagePath = Join-Path $outputDir "AppAudioDeviceSwitcher_$PackageVersion`_x64.msix"
$certCerPath = Join-Path $PSScriptRoot "AppAudioDeviceSwitcherDev.cer"
$certPfxPath = Join-Path $PSScriptRoot "AppAudioDeviceSwitcherDev.pfx"
$publisher = "CN=AppAudioDeviceSwitcherDev"
$packageName = "26748.AppAudioDeviceSwitcher"
$makeAppx = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\makeappx.exe"
$signTool = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"
$pfxPassword = ConvertTo-SecureString "AppAudioDeviceSwitcherDev" -AsPlainText -Force
$localNugetSource = Join-Path $env:USERPROFILE ".nuget\packages"

if (-not (Test-Path $makeAppx)) {
    throw "makeappx.exe not found at $makeAppx"
}

if (-not (Test-Path $signTool)) {
    throw "signtool.exe not found at $signTool"
}

$existingPackage = $null
if ($Install) {
    $existingPackage = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
    if ($existingPackage) {
        Remove-AppxPackage -Package $existingPackage.PackageFullName
    }
}

if (Test-Path $localNugetSource) {
    dotnet restore (Join-Path $repoRoot "AppAudioDeviceSwitcher.csproj") --source $localNugetSource --ignore-failed-sources
}

dotnet publish (Join-Path $repoRoot "AppAudioDeviceSwitcher.csproj") -c $Configuration -r win-x64 --self-contained false -o $publishDir --no-restore

if (Test-Path $layoutDir) {
    Remove-Item $layoutDir -Recurse -Force
}

New-Item -ItemType Directory -Force $layoutDir | Out-Null
New-Item -ItemType Directory -Force $outputDir | Out-Null

Copy-Item (Join-Path $publishDir "*") $layoutDir -Recurse -Force
Copy-Item (Join-Path $PSScriptRoot "Assets") (Join-Path $layoutDir "Assets") -Recurse -Force

$manifestContent = Get-Content $manifestPath -Raw
$manifestContent = [System.Text.RegularExpressions.Regex]::Replace(
    $manifestContent,
    '(<Identity\b[^>]*\bVersion=")([^"]+)(")',
    "`${1}$PackageVersion`${3}",
    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
Set-Content -Path $appxManifestPath -Value $manifestContent -Encoding UTF8

if (Test-Path $packagePath) {
    Remove-Item $packagePath -Force
}

if ($Install) {
    Add-AppxPackage -Register $appxManifestPath -ForceApplicationShutdown
    return
}

& $makeAppx pack /d $layoutDir /p $packagePath /o | Out-Host

$cert = New-SelfSignedCertificate `
    -Type Custom `
    -Subject $publisher `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -HashAlgorithm SHA256 `
    -KeyExportPolicy Exportable `
    -KeyUsage CertSign, DigitalSignature `
    -NotAfter (Get-Date).AddYears(5) `
    -TextExtension @(
        "2.5.29.19={critical}{text}CA=true",
        "2.5.29.37={text}1.3.6.1.5.5.7.3.3"
    )

Export-Certificate -Cert $cert -FilePath $certCerPath -Force | Out-Null
Export-PfxCertificate -Cert $cert -FilePath $certPfxPath -Password $pfxPassword -Force | Out-Null

& $signTool sign /fd SHA256 /f $certPfxPath /p "AppAudioDeviceSwitcherDev" $packagePath | Out-Host
