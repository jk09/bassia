@echo off
setlocal EnableExtensions

rem Installs this Bassia package for the current user; see install.ps1. Usage: install.cmd [-Check] [-NoUse] [-Root <dir>] ...
rem Prefers PowerShell 7 and falls back to Windows PowerShell; neither needs administrator rights.

where pwsh >nul 2>nul
if %ERRORLEVEL%==0 (
  pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
)
exit /b %ERRORLEVEL%
