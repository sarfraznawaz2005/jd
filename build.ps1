<#
.SYNOPSIS
    Builds the unpacked JustDownload production build for this PC (win-x64) — no installer, no other OS.

.DESCRIPTION
    Publishes JustDownload.App with the win-x64 profile (Release, framework-dependent + ReadyToRun) into
    artifacts\win-x64 (gitignored). The folder is wiped first so no stale files from an older build remain.

.EXAMPLE
    .\build.bat
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$appProject = Join-Path $PSScriptRoot 'JustDownload.App' 'JustDownload.App.csproj'
$outDir = Join-Path $PSScriptRoot 'artifacts' 'win-x64'

if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }

Write-Host '==> publishing win-x64 (Release, framework-dependent + R2R)' -ForegroundColor Cyan
dotnet publish $appProject -p:PublishProfile=win-x64 -o $outDir
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

Write-Host "Built $outDir" -ForegroundColor Green
