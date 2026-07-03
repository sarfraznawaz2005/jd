#!/usr/bin/env pwsh
#
# JustDownload Linux packaging build (TASK-078).
#
# Publishes the linux-x64 app (framework-dependent + ReadyToRun, docs/publishing.md — same model as every
# other platform) and packages it as an AppImage, a .deb, and a .rpm. Unlike build/publish.ps1, this script
# is NOT cross-platform: dpkg-deb, rpmbuild, and appimagetool are Linux-native tools with no Windows
# equivalent, so it must run on a Linux host or Linux CI runner (pwsh itself is cross-platform, but the
# packaging tools it shells out to are not). Preflight checks fail fast per-format if a tool is missing
# rather than silently skipping it.
#
# Host-manifest registration (AC2) needs no packaging-time work: JustDownload.App already registers the
# per-browser native-messaging manifests at first launch via INativeHostInstaller.Install() (App.axaml.cs),
# and NativeHostManifestLocations already resolves the correct per-user Linux paths — that path is
# unit-tested independently of this script (JustDownload.Tests/NativeMessaging). What this script *does*
# own is deregistration on package removal, via the same `--uninstall-cleanup` switch the Windows MSI's
# uninstall custom action uses (JustDownload.App/Services/UninstallCleanup.cs) — wired into deb's prerm and
# rpm's %preun below.
#
# IMPORTANT — AppImage runtime prerequisite: JustDownload ships framework-dependent (docs/publishing.md),
# so the AppImage still requires the .NET 8 Runtime to be installed on the host system. This is consistent
# with how Windows/macOS builds are documented, but it means the AppImage is *not* the traditional
# zero-dependency "runs on literally any Linux" artifact AppImages are usually marketed as. See
# docs/publishing.md's Linux section for the tradeoff and the self-contained-AppImage alternative.
#
#   ./build/build-linux-packages.ps1                       # AppImage + deb + rpm
#   ./build/build-linux-packages.ps1 -Formats deb,rpm       # a subset
#
[CmdletBinding()]
param(
    [ValidateSet('appimage', 'deb', 'rpm')]
    [string[]] $Formats = @('appimage', 'deb', 'rpm'),
    [string] $ProductVersion,
    [string] $OutDir = (Join-Path $PSScriptRoot 'linux' 'out')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$linuxDir = Join-Path $PSScriptRoot 'linux'
$appProject = Join-Path $repoRoot 'JustDownload.App' 'JustDownload.App.csproj'
$publishDir = Join-Path $linuxDir 'publish'
$OutDir = [System.IO.Path]::GetFullPath($OutDir)
$pkgName = 'justdownload'
$binaryName = 'JustDownload.App'
$hostBinaryName = 'JustDownload.NativeHost'
$iconSizes = @(16, 32, 48, 64, 128, 256)
# Microsoft's apt/yum feed package name for the shared runtime JustDownload needs at runtime
# (docs/publishing.md: framework-dependent, not self-contained).
$runtimeDep = 'dotnet-runtime-8.0'

if (-not $IsLinux) {
    throw 'build-linux-packages.ps1 must run on a Linux host: it shells out to dpkg-deb/rpmbuild/appimagetool, none of which have a Windows equivalent. Cross-publish the linux-x64 bits from Windows with build/publish.ps1 if you just need the raw bundle.'
}

if ([string]::IsNullOrWhiteSpace($ProductVersion)) {
    Write-Host '==> resolving ProductVersion from Directory.Build.props (single source of truth)' -ForegroundColor Cyan
    $ProductVersion = (dotnet msbuild $appProject -getProperty:Version -nologo | Select-Object -Last 1).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($ProductVersion)) {
        throw 'Failed to resolve ProductVersion via dotnet msbuild -getProperty:Version; pass -ProductVersion explicitly.'
    }
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

Write-Host '==> publishing linux-x64 (framework-dependent + R2R)' -ForegroundColor Cyan
dotnet publish $appProject -p:PublishProfile=linux-x64 -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

# dotnet publish doesn't set the executable bit on Linux binaries; every packaging format below needs it.
chmod +x (Join-Path $publishDir $binaryName)
chmod +x (Join-Path $publishDir $hostBinaryName)
if ($LASTEXITCODE -ne 0) { throw 'chmod +x on published binaries failed' }

# Stages a common install tree (/opt/justdownload + desktop file + icons + a /usr/bin symlink) shared by
# deb and rpm; AppImage uses its own AppDir layout (below) because AppImages are not FHS-rooted.
function New-InstallTree {
    param([string] $Root)

    if (Test-Path $Root) { Remove-Item -Recurse -Force $Root }
    $optDir = Join-Path $Root 'opt' $pkgName
    New-Item -ItemType Directory -Force -Path $optDir | Out-Null
    Copy-Item (Join-Path $publishDir '*') $optDir -Recurse

    $binDir = Join-Path $Root 'usr' 'bin'
    New-Item -ItemType Directory -Force -Path $binDir | Out-Null
    New-Item -ItemType SymbolicLink -Path (Join-Path $binDir $pkgName) -Target "/opt/$pkgName/$binaryName" | Out-Null

    $appsDir = Join-Path $Root 'usr' 'share' 'applications'
    New-Item -ItemType Directory -Force -Path $appsDir | Out-Null
    Copy-Item (Join-Path $linuxDir 'justdownload.desktop') (Join-Path $appsDir 'justdownload.desktop')

    foreach ($size in $iconSizes) {
        $iconDir = Join-Path $Root 'usr' 'share' 'icons' 'hicolor' "${size}x${size}" 'apps'
        New-Item -ItemType Directory -Force -Path $iconDir | Out-Null
        Copy-Item (Join-Path $linuxDir 'icons' "icon-$size.png") (Join-Path $iconDir 'justdownload.png')
    }

    return $optDir
}

function Build-AppImage {
    $tool = Get-Command appimagetool -ErrorAction SilentlyContinue
    if (-not $tool) { throw 'appimagetool not found on PATH. Install it from https://github.com/AppImage/AppImageKit (or your distro package) and retry.' }

    Write-Host '==> building AppImage' -ForegroundColor Cyan
    $appDir = Join-Path $linuxDir 'AppDir'
    if (Test-Path $appDir) { Remove-Item -Recurse -Force $appDir }
    $binDir = Join-Path $appDir 'usr' 'bin'
    New-Item -ItemType Directory -Force -Path $binDir | Out-Null
    Copy-Item (Join-Path $publishDir '*') $binDir -Recurse
    chmod +x (Join-Path $binDir $binaryName)
    chmod +x (Join-Path $binDir $hostBinaryName)

    Copy-Item (Join-Path $linuxDir 'justdownload.desktop') (Join-Path $appDir 'justdownload.desktop')
    Copy-Item (Join-Path $linuxDir 'icons' 'icon-256.png') (Join-Path $appDir 'justdownload.png')
    foreach ($size in $iconSizes) {
        $iconDir = Join-Path $appDir 'usr' 'share' 'icons' 'hicolor' "${size}x${size}" 'apps'
        New-Item -ItemType Directory -Force -Path $iconDir | Out-Null
        Copy-Item (Join-Path $linuxDir 'icons' "icon-$size.png") (Join-Path $iconDir 'justdownload.png')
    }

    $appRun = Join-Path $appDir 'AppRun'
    @'
#!/bin/sh
HERE="$(dirname "$(readlink -f "${0}")")"
exec "${HERE}/usr/bin/JustDownload.App" "$@"
'@ -replace "`r`n", "`n" | Set-Content -Path $appRun -NoNewline
    chmod +x $appRun

    $env:ARCH = 'x86_64'
    $outFile = Join-Path $OutDir "JustDownload-$ProductVersion-x86_64.AppImage"
    if (Test-Path $outFile) { Remove-Item -Force $outFile }
    appimagetool $appDir $outFile
    if ($LASTEXITCODE -ne 0) { throw 'appimagetool failed' }
    Write-Host "Built $outFile" -ForegroundColor Green
}

function Build-Deb {
    $tool = Get-Command dpkg-deb -ErrorAction SilentlyContinue
    if (-not $tool) { throw 'dpkg-deb not found on PATH (install the dpkg-dev package).' }

    Write-Host '==> building .deb' -ForegroundColor Cyan
    $pkgRoot = Join-Path $linuxDir 'deb-root'
    $optDir = New-InstallTree -Root $pkgRoot

    $debianDir = Join-Path $pkgRoot 'DEBIAN'
    New-Item -ItemType Directory -Force -Path $debianDir | Out-Null

    $installedSizeKb = [math]::Ceiling((Get-ChildItem -Recurse -File $pkgRoot | Measure-Object Length -Sum).Sum / 1KB)
    @"
Package: $pkgName
Version: $ProductVersion
Section: net
Priority: optional
Architecture: amd64
Depends: $runtimeDep
Installed-Size: $installedSizeKb
Maintainer: JustDownload contributors
Homepage: https://github.com/sarfraznawaz2005/jd
Description: Extremely light & fast cross-platform download manager
 JustDownload is a free, open-source download manager with dynamic segmentation,
 pause/resume with crash recovery, HLS support, and a browser extension.
"@ -replace "`r`n", "`n" | Set-Content -Path (Join-Path $debianDir 'control') -NoNewline

    # Mirrors the Windows MSI's uninstall custom action (build/installer/Product.wxs): deregister the
    # native-messaging manifests before the binary that Install() depends on is removed.
    @"
#!/bin/sh
set -e
if [ "`$1" = "remove" ] || [ "`$1" = "purge" ]; then
    /opt/$pkgName/$binaryName --uninstall-cleanup || true
fi
"@ -replace "`r`n", "`n" | Set-Content -Path (Join-Path $debianDir 'prerm') -NoNewline
    chmod +x (Join-Path $debianDir 'prerm')

    @'
#!/bin/sh
set -e
update-desktop-database -q /usr/share/applications 2>/dev/null || true
gtk-update-icon-cache -q /usr/share/icons/hicolor 2>/dev/null || true
'@ -replace "`r`n", "`n" | Set-Content -Path (Join-Path $debianDir 'postinst') -NoNewline
    chmod +x (Join-Path $debianDir 'postinst')

    chmod +x (Join-Path $optDir $binaryName)
    chmod +x (Join-Path $optDir $hostBinaryName)

    $outFile = Join-Path $OutDir "$pkgName`_$ProductVersion`_amd64.deb"
    if (Test-Path $outFile) { Remove-Item -Force $outFile }
    dpkg-deb --build --root-owner-group $pkgRoot $outFile
    if ($LASTEXITCODE -ne 0) { throw 'dpkg-deb failed' }
    Write-Host "Built $outFile" -ForegroundColor Green
}

function Build-Rpm {
    $tool = Get-Command rpmbuild -ErrorAction SilentlyContinue
    if (-not $tool) { throw 'rpmbuild not found on PATH (install the rpm-build package).' }

    Write-Host '==> building .rpm' -ForegroundColor Cyan
    $stageDir = Join-Path $linuxDir 'rpm-stage'
    $optDir = New-InstallTree -Root $stageDir
    chmod +x (Join-Path $optDir $binaryName)
    chmod +x (Join-Path $optDir $hostBinaryName)

    $topDir = Join-Path $linuxDir 'rpm-topdir'
    if (Test-Path $topDir) { Remove-Item -Recurse -Force $topDir }
    foreach ($sub in @('BUILD', 'RPMS', 'SOURCES', 'SPECS', 'SRPMS')) {
        New-Item -ItemType Directory -Force -Path (Join-Path $topDir $sub) | Out-Null
    }

    $env:JD_STAGE = $stageDir
    $env:JD_VERSION = $ProductVersion

    $specPath = Join-Path $topDir 'SPECS' "$pkgName.spec"
    Copy-Item (Join-Path $linuxDir "$pkgName.spec") $specPath

    rpmbuild --define "_topdir $topDir" -bb $specPath
    if ($LASTEXITCODE -ne 0) { throw 'rpmbuild failed' }

    $builtRpm = Get-ChildItem -Recurse -Path (Join-Path $topDir 'RPMS') -Filter '*.rpm' | Select-Object -First 1
    if (-not $builtRpm) { throw 'rpmbuild reported success but produced no .rpm' }
    $outFile = Join-Path $OutDir $builtRpm.Name
    Copy-Item $builtRpm.FullName $outFile -Force
    Write-Host "Built $outFile" -ForegroundColor Green
}

$builders = @{ appimage = ${function:Build-AppImage}; deb = ${function:Build-Deb}; rpm = ${function:Build-Rpm} }
$failures = @()
foreach ($format in $Formats) {
    try {
        & $builders[$format]
    }
    catch {
        Write-Host "FAILED building $format`: $($_.Exception.Message)" -ForegroundColor Red
        $failures += $format
    }
}

if ($failures.Count -gt 0) {
    throw "Failed formats: $($failures -join ', ')"
}
Write-Host "All requested Linux packages built in $OutDir" -ForegroundColor Green
