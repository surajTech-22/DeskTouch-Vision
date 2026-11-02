@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-DeskTouch.ps1"
if errorlevel 1 (
  echo.
  echo Installation failed. See %%LOCALAPPDATA%%\DeskTouchVision\logs\install.log
  pause
)
endlocal
