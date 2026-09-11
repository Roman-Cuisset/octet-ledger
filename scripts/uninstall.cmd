@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"
if errorlevel 1 (
  echo.
  echo Uninstallation failed.
  pause
  exit /b 1
)
echo.
echo Uninstallation complete.
pause
