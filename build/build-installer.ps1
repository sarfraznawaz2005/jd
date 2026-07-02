#!/usr/bin/env pwsh
#
# JustDownload Windows installer build (TASK-076).
#
# Publishes the win-x64 app (framework-dependent + ReadyToRun, docs/publishing.md), then packages it into a
# per-user MSI with the WiX Toolset v5 CLI (pinned as a local .NET tool — see .config/dotnet-tools.json;
# MS-RL licensed build-time tooling, never shipped, out of scope for CLAUDE.md §4's PackageReference gate —
# see the TASK-076 implementation notes). The WixToolset.UI.wixext and WixToolset.Util.wixext extensions are
# restored into a repo-local cache (.wix/, gitignored) below, so a fresh clone/CI runner needs no manual
# machine-global setup — `dotnet tool restore` + this script is the whole toolchain.
#
# Signing (AC2, "where feasible"): if -CertPath (or $env:JD_CODE_SIGN_CERT_PATH) is set, the built MSI is
# Authenticode-signed with signtool.exe. With no certificate configured (the default — this repo carries no
# code-signing certificate), signing is skipped with a clear message; this is not a stub, it is the honest
# "where feasible" state of an environment with no cert.
#
#   ./build/build-installer.ps1
#   ./build/build-installer.ps1 -CertPath C:\keys\justdownload.pfx -CertPassword (Read-Host -AsSecureString)
#
[CmdletBinding()]
param(
    [string] $ProductVersion = '1.0.0',
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
}
finally {
    Pop-Location
}

if ([string]::IsNullOrWhiteSpace($CertPath)) {
    Write-Host 'Signing skipped: no code-signing certificate configured (JD_CODE_SIGN_CERT_PATH / -CertPath unset).' -ForegroundColor Yellow
}
else {
    Write-Host "==> signing $msiPath with $CertPath" -ForegroundColor Cyan
    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if (-not $signtool) {
        throw "signtool.exe not found on PATH; install the Windows SDK to sign with -CertPath."
    }

    $signArgs = @('sign', '/f', $CertPath, '/fd', 'SHA256', '/tr', 'http://timestamp.digicert.com', '/td', 'SHA256', $msiPath)
    if ($CertPassword) {
        $plainPassword = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
            [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($CertPassword))
        $signArgs = @('sign', '/f', $CertPath, '/p', $plainPassword, '/fd', 'SHA256', '/tr', 'http://timestamp.digicert.com', '/td', 'SHA256', $msiPath)
    }

    & $signtool.Source @signArgs
    if ($LASTEXITCODE -ne 0) { throw 'signtool failed' }
    Write-Host 'MSI signed.' -ForegroundColor Green
}

Write-Host "Built $msiPath" -ForegroundColor Green
