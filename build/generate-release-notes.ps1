#!/usr/bin/env pwsh
#
# JustDownload release notes generator (TASK-176).
#
# Groups the commits since the previous release tag into a Keep-a-Changelog-style body (Added / Fixed /
# Changed / Performance / Documentation), parsed from this repo's existing Conventional Commit messages
# (CLAUDE.md §8 — every commit is already `type(scope): subject`) rather than hand-maintaining a changelog
# file. Internal-only commit types (chore/ci/test/build/style/revert) are excluded from the public notes;
# anything that doesn't parse as a Conventional Commit falls back into "Other" so nothing is silently
# dropped. release.yml runs this before publishing and passes the result as the release body.
#
#   ./build/generate-release-notes.ps1 -Tag v1.0.1 -OutFile build/installer/out/release-notes.md
#
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Tag,

    [string] $PreviousTag,

    # owner/repo, for the "Full Changelog" compare link — defaults to the origin remote.
    [string] $Repository,

    [Parameter(Mandatory = $true)]
    [string] $OutFile
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')

Push-Location $repoRoot
try {
    if ([string]::IsNullOrWhiteSpace($PreviousTag)) {
        # All tags, newest first; the one right after $Tag in that order is the previous release.
        $allTags = (git tag --sort=-version:refname) -split "`n" | Where-Object { $_ }
        $index = [array]::IndexOf($allTags, $Tag)
        if ($index -ge 0 -and $index + 1 -lt $allTags.Count) {
            $PreviousTag = $allTags[$index + 1]
        }
    }

    $range = if ($PreviousTag) { "$PreviousTag..$Tag" } else { $Tag }
    Write-Host "==> summarizing commits in range $range" -ForegroundColor Cyan

    # %x1f (unit separator) as the field delimiter — never appears in a commit subject.
    $rawLog = git log $range --no-merges --pretty=format:"%s"
    $subjects = if ($rawLog) { $rawLog -split "`n" } else { @() }

    $sections = [ordered]@{
        Added         = [System.Collections.Generic.List[string]]::new()
        Fixed         = [System.Collections.Generic.List[string]]::new()
        Changed       = [System.Collections.Generic.List[string]]::new()
        Performance   = [System.Collections.Generic.List[string]]::new()
        Documentation = [System.Collections.Generic.List[string]]::new()
        Other         = [System.Collections.Generic.List[string]]::new()
    }
    $typeToSection = @{
        feat     = 'Added'
        fix      = 'Fixed'
        refactor = 'Changed'
        perf     = 'Performance'
        docs     = 'Documentation'
    }
    $internalTypes = @('chore', 'ci', 'test', 'build', 'style', 'revert')

    foreach ($subject in $subjects) {
        if ($subject -match '^(?<type>[a-z]+)(\((?<scope>[^)]*)\))?:\s*(?<desc>.+)$') {
            $type = $Matches['type']
            $scope = $Matches['scope']
            $desc = $Matches['desc'].Trim()
            $desc = $desc.Substring(0, 1).ToUpperInvariant() + $desc.Substring(1)
            $line = if ($scope) { "- **${scope}:** $desc" } else { "- $desc" }

            if ($internalTypes -contains $type) {
                continue # not user-facing, e.g. `chore(ci): ...`
            }
            elseif ($typeToSection.ContainsKey($type)) {
                $sections[$typeToSection[$type]].Add($line)
            }
            else {
                $sections['Other'].Add("- $desc")
            }
        }
        elseif (-not [string]::IsNullOrWhiteSpace($subject)) {
            $sections['Other'].Add("- $subject") # doesn't parse as a Conventional Commit — surface it rather than drop it
        }
    }

    $body = [System.Text.StringBuilder]::new()
    $anySection = $false
    foreach ($name in $sections.Keys) {
        if ($sections[$name].Count -gt 0) {
            $anySection = $true
            [void]$body.AppendLine("### $name").AppendLine()
            foreach ($line in $sections[$name]) { [void]$body.AppendLine($line) }
            [void]$body.AppendLine()
        }
    }
    if (-not $anySection) {
        [void]$body.AppendLine('No user-facing changes.').AppendLine()
    }

    if ([string]::IsNullOrWhiteSpace($Repository)) {
        $originUrl = git config --get remote.origin.url
        if ($originUrl -match '[:/](?<repo>[^/]+/[^/]+?)(\.git)?$') {
            $Repository = $Matches['repo']
        }
    }
    if ($Repository -and $PreviousTag) {
        [void]$body.AppendLine("**Full Changelog**: https://github.com/$Repository/compare/$PreviousTag...$Tag")
    }

    New-Item -ItemType Directory -Force -Path (Split-Path $OutFile -Parent) | Out-Null
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($OutFile, $body.ToString(), $utf8NoBom)
    Write-Host "==> wrote $OutFile" -ForegroundColor Green
}
finally {
    Pop-Location
}
