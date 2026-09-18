@echo off
setlocal EnableExtensions

rem Merges the currently checked out feature branch into the integration branch below.
rem Usage: merge-feature-branch.cmd [base-branch]

set "BASE=main"
if not "%~1"=="" set "BASE=%~1"

git rev-parse --is-inside-work-tree >nul 2>&1
if errorlevel 1 (
  echo ERROR: not inside a git repository.
  exit /b 1
)

set "FEATURE="
for /f "delims=" %%b in ('git rev-parse --abbrev-ref HEAD') do set "FEATURE=%%b"
if not defined FEATURE (
  echo ERROR: could not determine the current branch.
  exit /b 1
)
if "%FEATURE%"=="HEAD" (
  echo ERROR: HEAD is detached; check out the feature branch first.
  exit /b 1
)
if "%FEATURE%"=="%BASE%" (
  echo ERROR: already on the base branch '%BASE%'; check out a feature branch first.
  exit /b 1
)

echo [1/4] Checking the working tree...
set "DIRTY="
for /f "delims=" %%s in ('git status --porcelain') do set "DIRTY=1"
if defined DIRTY (
  echo ERROR: uncommitted or untracked changes present; commit or stash them first.
  git status --short
  exit /b 1
)
echo       clean.

echo [2/4] Checking branches '%FEATURE%' -^> '%BASE%'...
git show-ref --verify --quiet "refs/heads/%BASE%"
if errorlevel 1 (
  echo ERROR: base branch '%BASE%' does not exist locally.
  exit /b 1
)
git merge-base --is-ancestor HEAD "%BASE%"
if not errorlevel 1 (
  echo ERROR: '%FEATURE%' is already merged into '%BASE%'; nothing to do.
  exit /b 1
)
echo       ok.

echo [3/4] git checkout %BASE%
git checkout "%BASE%"
if errorlevel 1 (
  echo ERROR: could not check out '%BASE%'.
  exit /b 1
)

echo [4/4] git merge --no-ff --no-edit %FEATURE%
git merge --no-ff --no-edit "%FEATURE%"
if errorlevel 1 (
  echo ERROR: merge failed; aborting and returning to '%FEATURE%'.
  git merge --abort
  git checkout "%FEATURE%"
  exit /b 1
)

echo.
echo Merged '%FEATURE%' into '%BASE%'. Now on:
git log --oneline -1
exit /b 0
