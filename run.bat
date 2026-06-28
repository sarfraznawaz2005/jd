@echo off
REM Launches the JustDownload dev preview via run.ps1 (clean, no-cache build by default).
REM Usage:  run.bat            -> clean Debug build + launch
REM         run.bat -Release   -> clean Release build + launch
REM         run.bat -NoClean   -> incremental build + launch
setlocal
where pwsh >nul 2>nul
if %errorlevel%==0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" %*
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" %*
)
exit /b %errorlevel%
