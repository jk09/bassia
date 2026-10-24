# Use an installed Bassia version in this PowerShell session only; the default (<root>/current) stays as it is.
# Usage: . <root>/activate.ps1 [<version>]   - without a version, the nearest .bassia-version file names it.
# Undo with: bassia_deactivate
param([string] $Version)

if (-not $Version) {
	$dir = (Get-Location).ProviderPath
	while ($dir) {
		$file = Join-Path $dir '.bassia-version'
		if (Test-Path -LiteralPath $file -PathType Leaf) { $Version = (Get-Content -LiteralPath $file -TotalCount 1).Trim(); break }
		$parent = Split-Path -Parent $dir
		if ($parent -eq $dir) { break }
		$dir = $parent
	}
}
if (-not $Version) { Write-Error 'bassia: name a version, or add a .bassia-version file to this folder or a parent.'; return }
$versionDir = Join-Path (Join-Path $PSScriptRoot 'versions') $Version
if (-not (Test-Path -LiteralPath $versionDir -PathType Container)) {
	$installed = (Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'versions') -Directory -ErrorAction SilentlyContinue | Where-Object { -not $_.Name.StartsWith('.') } | ForEach-Object Name) -join ', '
	Write-Error "bassia: version '$Version' is not installed. Installed: $installed"
	return
}
if (Get-Command bassia_deactivate -ErrorAction SilentlyContinue) { bassia_deactivate }
$global:_BASSIA_OLD_PATH = $env:PATH
$env:PATH = $versionDir + [IO.Path]::PathSeparator + $env:PATH
$env:BASSIA_VERSION = $Version
function global:bassia_deactivate {
	$env:PATH = $global:_BASSIA_OLD_PATH
	Remove-Item Env:BASSIA_VERSION -ErrorAction SilentlyContinue
	Remove-Variable -Name _BASSIA_OLD_PATH -Scope Global -ErrorAction SilentlyContinue
	Remove-Item Function:bassia_deactivate -ErrorAction SilentlyContinue
}
Write-Host "bassia: using $Version in this session (bassia_deactivate to undo)"
