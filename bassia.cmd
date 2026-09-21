@echo off
setlocal EnableExtensions

rem Runs the Bassia.exe produced by `dotnet build` without having to dig through bin/ folders.
rem Prefers the Debug build; falls back to Release. Usage: bassia.cmd [args...]

set "ROOT=%~dp0"
set "EXE="
if exist "%ROOT%Bassia\bin\Debug\net10.0\Bassia.exe"   set "EXE=%ROOT%Bassia\bin\Debug\net10.0\Bassia.exe"
if not defined EXE if exist "%ROOT%Bassia\bin\Release\net10.0\Bassia.exe" set "EXE=%ROOT%Bassia\bin\Release\net10.0\Bassia.exe"

if not defined EXE (
  echo ERROR: Bassia.exe not found; run `dotnet build` in "%ROOT%" first.
  exit /b 1
)

"%EXE%" %*
exit /b %ERRORLEVEL%
