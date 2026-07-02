#!/usr/bin/env pwsh
#
# JustDownload Windows installer build (TASK-076, extended by TASK-159).
#
# Publishes the win-x64 app (framework-dependent + ReadyToRun, docs/publishing.md), then packages it into a
# per-user MSI with the WiX Toolset v5 CLI (pinned as a local .NET tool — see .config/dotnet-tools.json;
# MS-RL licensed build-time tooling, never shipped, out of scope for CLAUDE.md §4's PackageReference gate —
# see the TASK-076 implementation notes). The WixToolset.UI.wixext, WixToolset.Util.wixext and
# WixToolset.Bal.wixext extensions are restored into a repo-local cache (.wix/, gitignored) below, so a
# fresh clone/CI runner needs no manual machine-global setup — `dotnet tool restore` + this script is the
# whole toolchain. Finally it wraps that MSI in a Burn bootstrapper ("...-Setup.exe", TASK-159 AC0) that
# chains an install of the Microsoft .NET 8 Desktop Runtime, so a runtime-less machine is prompted/shown
# progress instead of the MSI silently installing an app that then fails to launch — see the extensive
# rationale comment at the top of build/installer/Bundle.wxs for how that bootstrapper is put together and
# why it deliberately does not try to pre-detect an installed runtime.
#
# ProductVersion (TASK-159, AC1): defaults to the solution-wide <Version> in Directory.Build.props (the one
# real version source in this repo — there is no CI build-number scheme to hook into), read back via
# `dotnet msbuild -getProperty:Version` so the installer version can never drift from what every csproj
# already reports. Pass -ProductVersion explicitly to override for a one-off build.
#
# Signing (AC2, "where feasible"): if -CertPath (or $env:JD_CODE_SIGN_CERT_PATH) is set, the built MSI and
# bootstrapper are Authenticode-signed with signtool.exe. With no certificate configured (the default — this
# repo carries no code-signing certificate), signing is skipped with a clear message; this is not a stub, it
# is the honest "where feasible" state of an environment with no cert.
#
#   ./build/build-installer.ps1
#   ./build/build-installer.ps1 -CertPath C:\keys\justdownload.pfx -CertPassword (Read-Host -AsSecureString)
#
[CmdletBinding()]
param(
    [string] $ProductVersion,
    [string] $OutDir = (Join-Path $PSScriptRoot 'installer' 'out'),
    [string] $CertPath = $env:JD_CODE_SIGN_CERT_PATH,
    [SecureString] $CertPassword
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$installerDir = Join-Path $PSScriptRoot 'installer'
$appProject = Join-Path $repoRoot 'JustDownload.App' 'JustDownload.App.csproj'
$publishDir = Join-Path $installerDir 'publish'
$OutDir = [System.IO.Path]::GetFullPath($OutDir)
$msiPath = Join-Path $OutDir 'JustDownload-win-x64.msi'
$bundlePath = Join-Path $OutDir 'JustDownload-win-x64-Setup.exe'
# WixToolset.Bal.wixext's NuGet package (unlike UI/Util) ships an assembly whose file name doesn't match
# the package/extension id — `wix.exe` resolves `-ext WixToolset.Bal.wixext` by looking for
# WixToolset.Bal.wixext.dll on disk and fails (WIX0144) even though `extension add` "succeeds". Verified by
# downloading the 5.0.2 nupkg directly: it only contains wixext5\WixToolset.BootstrapperApplications.wixext.dll.
# Reference the real, pinned path instead of the short name so `-ext` actually resolves.
$balExtensionPath = Join-Path $repoRoot '.wix' 'extensions' 'WixToolset.Bal.wixext' '5.0.2' 'wixext5' 'WixToolset.BootstrapperApplications.wixext.dll'

if ([string]::IsNullOrWhiteSpace($ProductVersion)) {
    Write-Host '==> resolving ProductVersion from Directory.Build.props (single source of truth)' -ForegroundColor Cyan
    $ProductVersion = (dotnet msbuild $appProject -getProperty:Version -nologo | Select-Object -Last 1).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($ProductVersion)) {
        throw 'Failed to resolve ProductVersion via dotnet msbuild -getProperty:Version; pass -ProductVersion explicitly.'
    }
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

Push-Location $repoRoot
try {
    Write-Host '==> restoring the pinned WiX CLI + extensions (repo-local, .wix/)' -ForegroundColor Cyan
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed' }
    dotnet tool run wix -- extension add WixToolset.UI.wixext/5.0.2
    if ($LASTEXITCODE -ne 0) { throw 'wix extension add WixToolset.UI.wixext failed' }
    dotnet tool run wix -- extension add WixToolset.Util.wixext/5.0.2
    if ($LASTEXITCODE -ne 0) { throw 'wix extension add WixToolset.Util.wixext failed' }
    dotnet tool run wix -- extension add WixToolset.Bal.wixext/5.0.2
    if ($LASTEXITCODE -ne 0) { throw 'wix extension add WixToolset.Bal.wixext failed' }
    if (-not (Test-Path $balExtensionPath)) { throw "Expected WixToolset.Bal.wixext payload not found at $balExtensionPath" }
}
finally {
    Pop-Location
}

Write-Host '==> publishing win-x64 (framework-dependent + R2R)' -ForegroundColor Cyan
dotnet publish $appProject -p:PublishProfile=win-x64 -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

Write-Host '==> building MSI with WiX v5' -ForegroundColor Cyan
Push-Location $repoRoot
try {
    dotnet tool run wix -- build (Join-Path $installerDir 'Product.wxs') `
        -arch x64 `
        -d "ProductVersion=$ProductVersion" `
        -d "PublishDir=$publishDir" `
        -ext WixToolset.UI.wixext `
        -ext WixToolset.Util.wixext `
        -o $msiPath
    if ($LASTEXITCODE -ne 0) { throw 'wix build failed' }

    Write-Host '==> building the .NET runtime bootstrapper (Burn) with WiX v5' -ForegroundColor Cyan
    dotnet tool run wix -- build (Join-Path $installerDir 'Bundle.wxs') `
        -arch x64 `
        -d "ProductVersion=$ProductVersion" `
        -d "MsiPath=$msiPath" `
        -d "InstallerDir=$installerDir" `
        -ext "$balExtensionPath" `
        -o $bundlePath
    if ($LASTEXITCODE -ne 0) { throw 'wix build (bundle) failed' }
}
finally {
    Pop-Location
}

if ([string]::IsNullOrWhiteSpace($CertPath)) {
    Write-Host 'Signing skipped: no code-signing certificate configured (JD_CODE_SIGN_CERT_PATH / -CertPath unset).' -ForegroundColor Yellow
}
else {
    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if (-not $signtool) {
        throw "signtool.exe not found on PATH; install the Windows SDK to sign with -CertPath."
    }

    foreach ($fileToSign in @($msiPath, $bundlePath)) {
        Write-Host "==> signing $fileToSign with $CertPath" -ForegroundColor Cyan
        $signArgs = @('sign', '/f', $CertPath, '/fd', 'SHA256', '/tr', 'http://timestamp.digicert.com', '/td', 'SHA256', $fileToSign)
        if ($CertPassword) {
            $plainPassword = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
                [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($CertPassword))
            $signArgs = @('sign', '/f', $CertPath, '/p', $plainPassword, '/fd', 'SHA256', '/tr', 'http://timestamp.digicert.com', '/td', 'SHA256', $fileToSign)
        }

        & $signtool.Source @signArgs
        if ($LASTEXITCODE -ne 0) { throw "signtool failed for $fileToSign" }
    }
    Write-Host 'MSI and bootstrapper signed.' -ForegroundColor Green
}

Write-Host "Built $msiPath" -ForegroundColor Green
Write-Host "Built $bundlePath" -ForegroundColor Green
