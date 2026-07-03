#!/usr/bin/env pwsh
#
# JustDownload macOS packaging build (TASK-077).
#
# Publishes osx-x64/osx-arm64 (framework-dependent + ReadyToRun, docs/publishing.md — same model as every
# other platform), assembles a JustDownload.app bundle, code-signs it if a Developer ID is supplied, wraps
# it in a drag-to-Applications .dmg, and notarizes+staples that dmg if a notarytool keychain profile is
# supplied. Like build-linux-packages.ps1, this is NOT cross-platform: codesign/hdiutil/xcrun have no
# Windows equivalent, so it must run on a real Mac (or macOS CI runner).
#
# Signing/notarization are both optional and BOTH-OR-NEITHER in practice: Apple's notary service rejects
# unsigned bundles, so if -SigningIdentity is omitted, notarization is skipped too regardless of
# -NotarizeProfile, with a clear message (matching build-installer.ps1's "no cert configured" posture —
# this repo carries no Apple Developer ID, so the default run signs and notarizes nothing).
#
# Host-manifest registration (AC2) needs no packaging-time work here either, for the same reason as the
# Linux build: JustDownload.App already registers the per-browser native-messaging manifests at first
# launch (INativeHostInstaller.Install(), App.axaml.cs), and NativeHostManifestLocations already resolves
# the correct ~/Library/Application Support/<browser>/NativeMessagingHosts path — independently
# unit-tested. Unlike the .deb/.rpm/MSI, there is deliberately no scripted uninstall hook here: a
# drag-installed .app has no package manager to hang a prerm/%preun/custom-action off of, so deleting the
# .app by dragging it to the Trash leaves the (harmless, tiny) manifest files behind. See
# docs/publishing.md's macOS section for that tradeoff spelled out.
#
#   ./build/build-macos-packages.ps1
#   ./build/build-macos-packages.ps1 -Rids osx-arm64
#   ./build/build-macos-packages.ps1 -SigningIdentity "Developer ID Application: Name (TEAMID)" -NotarizeProfile jd-notary
#
[CmdletBinding()]
param(
    [ValidateSet('osx-x64', 'osx-arm64')]
    [string[]] $Rids = @('osx-x64', 'osx-arm64'),
    [string] $ProductVersion,
    [string] $OutDir = (Join-Path $PSScriptRoot 'macos' 'out'),
    [string] $SigningIdentity,
    [string] $NotarizeProfile
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$macosDir = Join-Path $PSScriptRoot 'macos'
$appProject = Join-Path $repoRoot 'JustDownload.App' 'JustDownload.App.csproj'
$binaryName = 'JustDownload.App'
$hostBinaryName = 'JustDownload.NativeHost'
$appName = 'JustDownload.app'
$OutDir = [System.IO.Path]::GetFullPath($OutDir)

if (-not $IsMacOS) {
    throw 'build-macos-packages.ps1 must run on macOS: it shells out to codesign/hdiutil/xcrun, none of which have a Windows equivalent. Cross-publish the osx-x64/osx-arm64 bits from Windows with build/publish.ps1 if you just need the raw bundle.'
}

if ([string]::IsNullOrWhiteSpace($ProductVersion)) {
    Write-Host '==> resolving ProductVersion from Directory.Build.props (single source of truth)' -ForegroundColor Cyan
    $ProductVersion = (dotnet msbuild $appProject -getProperty:Version -nologo | Select-Object -Last 1).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($ProductVersion)) {
        throw 'Failed to resolve ProductVersion via dotnet msbuild -getProperty:Version; pass -ProductVersion explicitly.'
    }
}

if ([string]::IsNullOrWhiteSpace($SigningIdentity)) {
    Write-Host 'Signing skipped: no -SigningIdentity supplied (this repo carries no Apple Developer ID certificate).' -ForegroundColor Yellow
    if (-not [string]::IsNullOrWhiteSpace($NotarizeProfile)) {
        Write-Host 'Notarization also skipped: Apple rejects unsigned bundles, so -NotarizeProfile is ignored without -SigningIdentity.' -ForegroundColor Yellow
    }
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Build-AppBundle {
    param([string] $Rid)

    Write-Host "==> publishing $Rid (framework-dependent + R2R)" -ForegroundColor Cyan
    $publishDir = Join-Path $macosDir "publish-$Rid"
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    dotnet publish $appProject -p:PublishProfile=$Rid -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $Rid" }

    Write-Host "==> assembling $appName for $Rid" -ForegroundColor Cyan
    $bundleRoot = Join-Path $macosDir "$Rid-bundle"
    if (Test-Path $bundleRoot) { Remove-Item -Recurse -Force $bundleRoot }
    $contentsDir = Join-Path $bundleRoot $appName 'Contents'
    $macOsDir = Join-Path $contentsDir 'MacOS'
    $resourcesDir = Join-Path $contentsDir 'Resources'
    New-Item -ItemType Directory -Force -Path $macOsDir, $resourcesDir | Out-Null

    # Framework-dependent (not self-contained/single-file): AppContext.BaseDirectory is Contents/MacOS, so
    # every managed assembly + native lib (SkiaSharp/HarfBuzzSharp/e_sqlite3 .dylibs) has to live right next
    # to the executable — the whole publish output goes in flat, matching AppLauncher's assumption
    # (JustDownload.Core/NativeMessaging/AppLauncher.cs: ResolveAppExecutable resolves "JustDownload.App"
    # next to the currently-running binary's own directory) that App and NativeHost are co-located.
    Copy-Item (Join-Path $publishDir '*') $macOsDir -Recurse
    chmod +x (Join-Path $macOsDir $binaryName)
    chmod +x (Join-Path $macOsDir $hostBinaryName)

    $infoPlist = Get-Content (Join-Path $macosDir 'Info.plist') -Raw
    $infoPlist.Replace('{{VERSION}}', $ProductVersion) | Set-Content -Path (Join-Path $contentsDir 'Info.plist') -NoNewline

    return $bundleRoot
}

function Sign-AppBundle {
    param([string] $BundleRoot)

    if ([string]::IsNullOrWhiteSpace($SigningIdentity)) { return }

    $tool = Get-Command codesign -ErrorAction SilentlyContinue
    if (-not $tool) { throw 'codesign not found (requires Xcode command line tools).' }

    Write-Host '==> code-signing (hardened runtime, Entitlements.plist)' -ForegroundColor Cyan
    $appPath = Join-Path $BundleRoot $appName
    $entitlements = Join-Path $macosDir 'Entitlements.plist'
    $macOsDir = Join-Path $appPath 'Contents' 'MacOS'

    # Sign inside-out: native libs, then the two executables, then the outer bundle. Apple's guidance
    # discourages --deep on anything but the simplest trees; this app has no nested frameworks, just a
    # flat Contents/MacOS, so explicit inside-out signing is straightforward and more correct than --deep.
    Get-ChildItem -Path $macOsDir -Filter '*.dylib' -ErrorAction SilentlyContinue | ForEach-Object {
        codesign --force --options runtime --timestamp --sign $SigningIdentity $_.FullName
        if ($LASTEXITCODE -ne 0) { throw "codesign failed for $($_.FullName)" }
    }
    foreach ($exe in @($hostBinaryName, $binaryName)) {
        codesign --force --options runtime --timestamp --entitlements $entitlements --sign $SigningIdentity (Join-Path $macOsDir $exe)
        if ($LASTEXITCODE -ne 0) { throw "codesign failed for $exe" }
    }
    codesign --force --options runtime --timestamp --entitlements $entitlements --sign $SigningIdentity $appPath
    if ($LASTEXITCODE -ne 0) { throw 'codesign failed for the app bundle' }

    codesign --verify --deep --strict $appPath
    if ($LASTEXITCODE -ne 0) { throw 'codesign --verify failed after signing' }
    Write-Host 'Signed and verified.' -ForegroundColor Green
}

function Build-Dmg {
    param([string] $BundleRoot, [string] $Rid)

    $tool = Get-Command hdiutil -ErrorAction SilentlyContinue
    if (-not $tool) { throw 'hdiutil not found (should ship with macOS).' }

    Write-Host "==> building .dmg for $Rid" -ForegroundColor Cyan
    $stageDir = Join-Path $macosDir "$Rid-dmg-stage"
    if (Test-Path $stageDir) { Remove-Item -Recurse -Force $stageDir }
    New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
    Copy-Item (Join-Path $BundleRoot $appName) $stageDir -Recurse
    # The classic drag-to-install affordance (AC1): the mounted volume shows the app next to a shortcut
    # to /Applications, so the user drags one onto the other.
    New-Item -ItemType SymbolicLink -Path (Join-Path $stageDir 'Applications') -Target '/Applications' | Out-Null

    $dmgPath = Join-Path $OutDir "JustDownload-$ProductVersion-$Rid.dmg"
    if (Test-Path $dmgPath) { Remove-Item -Force $dmgPath }
    hdiutil create -volname 'JustDownload' -srcfolder $stageDir -ov -format UDZO $dmgPath
    if ($LASTEXITCODE -ne 0) { throw 'hdiutil create failed' }
    Write-Host "Built $dmgPath" -ForegroundColor Green
    return $dmgPath
}

function Notarize-Dmg {
    param([string] $DmgPath)

    if ([string]::IsNullOrWhiteSpace($SigningIdentity) -or [string]::IsNullOrWhiteSpace($NotarizeProfile)) { return }

    $notarytool = Get-Command xcrun -ErrorAction SilentlyContinue
    if (-not $notarytool) { throw 'xcrun not found (requires Xcode command line tools).' }

    Write-Host "==> submitting $DmgPath for notarization (profile: $NotarizeProfile)" -ForegroundColor Cyan
    xcrun notarytool submit $DmgPath --keychain-profile $NotarizeProfile --wait
    if ($LASTEXITCODE -ne 0) { throw 'notarytool submit failed' }

    Write-Host '==> stapling the notarization ticket' -ForegroundColor Cyan
    xcrun stapler staple $DmgPath
    if ($LASTEXITCODE -ne 0) { throw 'stapler staple failed' }
    Write-Host 'Notarized and stapled.' -ForegroundColor Green
}

foreach ($rid in $Rids) {
    $bundleRoot = Build-AppBundle -Rid $rid
    Sign-AppBundle -BundleRoot $bundleRoot
    $dmgPath = Build-Dmg -BundleRoot $bundleRoot -Rid $rid
    Notarize-Dmg -DmgPath $dmgPath
}

Write-Host "All requested macOS packages built in $OutDir" -ForegroundColor Green
