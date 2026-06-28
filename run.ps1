<#
.SYNOPSIS
    Builds and launches the JustDownload desktop app (dev preview).

.DESCRIPTION
    "No cache": deletes the bin/ and obj/ output of the App and Core projects first, so the app is always
    built fresh from the current source — no stale Avalonia XAML or assemblies. Then runs JustDownload.App.

.PARAMETER Release
    Build & run in Release configuration (default is Debug, which enables the Avalonia dev tools — press F12).

.PARAMETER NoClean
    Skip the clean step (faster iteration when you trust the incremental build).

.EXAMPLE
    .\run.bat              # clean Debug build, then launch
    .\run.bat -Release     # clean Release build, then launch
    .\run.bat -NoClean     # incremental Debug build, then launch
#>
[CmdletBinding()]
param(
    [switch]$Release,
    [switch]$NoClean
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$config = if ($Release) { 'Release' } else { 'Debug' }
$appProject = Join-Path $root 'JustDownload.App\JustDownload.App.csproj'

Write-Host "JustDownload dev preview — $config" -ForegroundColor Cyan

if (-not $NoClean) {
    Write-Host 'Clearing build cache (bin/obj)...' -ForegroundColor DarkGray
    foreach ($proj in 'JustDownload.App', 'JustDownload.Core') {
        foreach ($dir in 'bin', 'obj') {
            $path = Join-Path $root "$proj\$dir"
            if (Test-Path $path) {
                Remove-Item -LiteralPath $path -Recurse -Force
            }
        }
    }
}

Write-Host "Building & launching $appProject ..." -ForegroundColor DarkGray
& dotnet run --project $appProject -c $config
exit $LASTEXITCODE
