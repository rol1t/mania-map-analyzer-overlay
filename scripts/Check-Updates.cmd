@echo off
setlocal
chcp 65001 >nul
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Update-ManiaMapAnalyzerOverlay.ps1" -CheckOnly
echo.
pause
endlocal
