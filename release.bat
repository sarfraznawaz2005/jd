@echo off
REM Interactive release script (TASK-177): shows the current version, asks for the new one, then
REM bumps/commits/pushes/tags and waits for CI + the release build. See build/release.ps1 for details.
setlocal
where pwsh >nul 2>nul
if %errorlevel%==0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\release.ps1"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\release.ps1"
)
set EXIT_CODE=%errorlevel%
echo.
pause
exit /b %EXIT_CODE%
