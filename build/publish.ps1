#!/usr/bin/env pwsh
#
# JustDownload per-OS publish + package (TASK-075).
#
# Publishes the Avalonia app framework-dependent + ReadyToRun for each target RID
# (see JustDownload.App/Properties/PublishProfiles and docs/publishing.md), zips
# each bundle into the distributable installer artifact, and checks the COMPRESSED
# size against the K3 budget (<=40 MB). PowerShell 7+ runs this on Windows, macOS,
# and Linux.
#
#   ./build/publish.ps1                       # all RIDs
#   ./build/publish.ps1 -Rids win-x64         # a subset
#   ./build/publish.ps1 -OutDir ./artifacts   # custom output
#
[CmdletBinding()]
param(
    [string[]] $Rids = @('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64'),
    [string] $OutDir = (Join-Path $PSScriptRoot '..' 'artifacts'),
    [int] $BudgetMb = 40
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$appProject = Join-Path $repoRoot 'JustDownload.App' 'JustDownload.App.csproj'
$OutDir = [System.IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$results = @()
foreach ($rid in $Rids) {
    Write-Host "==> publishing $rid (framework-dependent + R2R)" -ForegroundColor Cyan
    $bundleDir = Join-Path $OutDir $rid
    if (Test-Path $bundleDir) { Remove-Item -Recurse -Force $bundleDir }

    dotnet publish $appProject -p:PublishProfile=$rid -o $bundleDir
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }

    $zipPath = Join-Path $OutDir "JustDownload-$rid.zip"
    if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
    Compress-Archive -Path (Join-Path $bundleDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

    $extractedMb = [math]::Round((Get-ChildItem -Recurse -File $bundleDir | Measure-Object Length -Sum).Sum / 1MB, 1)
    $zipMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
    $results += [pscustomobject]@{ RID = $rid; ExtractedMB = $extractedMb; InstallerMB = $zipMb; Budget = "<=$BudgetMb" }
}

Write-Host ""
$results | Format-Table -AutoSize | Out-String | Write-Host

$over = $results | Where-Object { $_.InstallerMB -gt $BudgetMb }
if ($over) {
    Write-Error ("installer exceeds {0} MB budget: {1}" -f $BudgetMb, ($over.RID -join ', '))
    exit 1
}
Write-Host "All installers within the $BudgetMb MB budget." -ForegroundColor Green
