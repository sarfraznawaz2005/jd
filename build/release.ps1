#!/usr/bin/env pwsh
#
# JustDownload interactive release script (TASK-177).
#
# Shows the current version (Directory.Build.props, the single source of truth per TASK-159), asks for the
# new version, then walks the whole release process end to end: bump -> sanity build -> commit -> push ->
# wait for ci.yml to go green -> tag -> push the tag -> wait for release.yml to build/sign/publish -> print
# the finished release. Pauses for an explicit Y/n before push and before tagging (both wide-impact,
# public-facing actions) so nothing irreversible happens without the person running this confirming it in
# the moment; answering "n" at either point leaves things in a safe, resumable state and tells you the
# manual command to finish later.
#
# Requires the GitHub CLI (gh), authenticated (`gh auth login`), since it drives everything through the
# same commands a maintainer would type by hand — this just removes the tedium of babysitting each step.
#
#   ./build/release.ps1
#   (or double-click release.bat at the repo root)
#
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
$pollIntervalSeconds = 15
$pollTimeoutSeconds = 1200

function Read-CurrentVersion {
    $content = [System.IO.File]::ReadAllText($propsPath)
    if ($content -match '<Version>(?<v>[^<]+)</Version>') { return $Matches['v'] }
    throw "Could not find <Version>...</Version> in $propsPath"
}

function Confirm-Step([string] $message) {
    $answer = Read-Host "$message [Y/n]"
    return ($answer -eq '' -or $answer -match '^[Yy]')
}

# Polls a workflow's runs for the given commit until one reaches a terminal state, printing a dot per poll.
function Wait-ForWorkflowRun([string] $repository, [string] $workflowFile, [string] $sha) {
    $elapsed = 0
    while ($elapsed -lt $pollTimeoutSeconds) {
        $response = gh api "repos/$repository/actions/workflows/$workflowFile/runs?head_sha=$sha" | ConvertFrom-Json
        if ($response.workflow_runs.Count -gt 0) {
            $run = $response.workflow_runs[0]
            if ($run.status -eq 'completed') {
                Write-Host ''
                return $run
            }
        }
        Start-Sleep -Seconds $pollIntervalSeconds
        $elapsed += $pollIntervalSeconds
        Write-Host '.' -NoNewline
    }
    Write-Host ''
    throw "Timed out after $($pollTimeoutSeconds / 60) minutes waiting for $workflowFile. Check https://github.com/$repository/actions."
}

Push-Location $repoRoot
try {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw 'GitHub CLI (gh) not found on PATH -- install it from https://cli.github.com, then gh auth login.'
    }
    # `gh auth status` fails if ANY configured credential source is bad (e.g. a stray invalid GITHUB_TOKEN
    # env var), even when gh is otherwise fully functional via a working keyring login. Check what actually
    # matters instead: can gh reach *this* repo.
    $repository = (gh repo view --json nameWithOwner -q .nameWithOwner 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repository)) {
        throw "gh can't reach this repository -- run 'gh auth login' and confirm 'gh repo view' works."
    }
    $repository = $repository.Trim()

    $branch = (git rev-parse --abbrev-ref HEAD).Trim()
    if ($branch -ne 'master') {
        Write-Host "You're on '$branch', not 'master' (JustDownload releases from master)." -ForegroundColor Yellow
        if (-not (Confirm-Step 'Continue anyway?')) { Write-Host 'Aborted.'; exit 1 }
    }

    $dirty = git status --porcelain
    if ($dirty) {
        Write-Host 'Note: other uncommitted changes exist (this script only stages Directory.Build.props):' -ForegroundColor Yellow
        $dirty | ForEach-Object { Write-Host "  $_" }
    }

    $current = Read-CurrentVersion
    $parts = $current -split '\.'
    $suggested = "$($parts[0]).$($parts[1]).$([int]$parts[2] + 1)"
    Write-Host ''
    Write-Host "Current version: $current" -ForegroundColor Cyan
    $newVersion = Read-Host "New version to release [$suggested]"
    if ([string]::IsNullOrWhiteSpace($newVersion)) { $newVersion = $suggested }
    if ($newVersion -notmatch '^\d+\.\d+\.\d+$') { throw "'$newVersion' isn't a valid version (expected X.Y.Z)." }
    if ($newVersion -eq $current) { throw "That's already the current version ($current)." }
    if (git tag -l "v$newVersion") { throw "Tag v$newVersion already exists." }

    $updated = [System.Text.RegularExpressions.Regex]::Replace(
        [System.IO.File]::ReadAllText($propsPath), '<Version>[^<]+</Version>', "<Version>$newVersion</Version>")
    [System.IO.File]::WriteAllText($propsPath, $updated, [System.Text.UTF8Encoding]::new($false))
    Write-Host "==> Directory.Build.props: $current -> $newVersion" -ForegroundColor Green
    git --no-pager diff -- $propsPath

    if (-not (Confirm-Step 'Commit and push this version bump?')) {
        Write-Host 'Left the file edited but uncommitted -- commit it yourself when ready.'
        exit 0
    }

    Write-Host '==> sanity build (dotnet build -c Release)' -ForegroundColor Cyan
    dotnet build -c Release | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Build failed -- not committing. Fix it, then re-run this script.' }

    git add $propsPath
    git commit -m "chore: bump version to $newVersion"
    git push origin $branch
    $sha = (git rev-parse HEAD).Trim()

    Write-Host "==> waiting for CI on $($sha.Substring(0, 7))..." -ForegroundColor Cyan
    $ciRun = Wait-ForWorkflowRun -repository $repository -workflowFile 'ci.yml' -sha $sha
    if ($ciRun.conclusion -ne 'success') {
        throw "CI finished with '$($ciRun.conclusion)' -- $($ciRun.html_url). Not tagging; fix the build and re-run."
    }
    Write-Host "==> CI green: $($ciRun.html_url)" -ForegroundColor Green

    if (-not (Confirm-Step "Tag v$newVersion and push it to trigger the release build?")) {
        Write-Host "Skipped tagging. Run this when ready: git tag v$newVersion && git push origin v$newVersion"
        exit 0
    }

    git tag "v$newVersion"
    git push origin "v$newVersion"

    Write-Host '==> waiting for the release build...' -ForegroundColor Cyan
    $releaseRun = Wait-ForWorkflowRun -repository $repository -workflowFile 'release.yml' -sha $sha
    if ($releaseRun.conclusion -ne 'success') {
        throw "Release build finished with '$($releaseRun.conclusion)' -- $($releaseRun.html_url)"
    }

    Write-Host ''
    Write-Host "Released v$newVersion" -ForegroundColor Green
    gh release view "v$newVersion"
}
catch {
    Write-Host ''
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    Pop-Location
}
