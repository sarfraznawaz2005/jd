@echo off
REM Builds the unpacked production build for this PC (win-x64) via build.ps1.
REM Output: artifacts\win-x64\JustDownload.App.exe (gitignored). No installer is made.
setlocal
where pwsh >nul 2>nul
if not %errorlevel%==0 (
    echo PowerShell 7 ^(pwsh^) is required: build.ps1 uses PowerShell 7 syntax.
    echo Install it with:  winget install Microsoft.PowerShell
    pause
    exit /b 1
)
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
set EXIT_CODE=%errorlevel%
echo.
if not %EXIT_CODE%==0 echo Build FAILED with exit code %EXIT_CODE%.
pause
exit /b %EXIT_CODE%
