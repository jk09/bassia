<#
.SYNOPSIS
	Installs the Bassia release package this script is part of, for the current user (no elevation).

.DESCRIPTION
	Checks git and the .NET and ASP.NET Core runtimes the package needs, then installs it with the version manager
	shipped beside it (scripts/bassia-versions.ps1 install -Package): side by side with other versions under
	%LOCALAPPDATA%\Bassia (or $env:BASSIA_HOME), made the default and put on the user PATH. Use
	scripts/bassia-versions.ps1 list | use | uninstall afterwards.

.EXAMPLE
	.\install.ps1
	.\install.ps1 -Check
	.\install.ps1 -NoUse -Root D:\tools\bassia
#>
[CmdletBinding()]
param(
	# Only verify the requirements.
	[switch] $Check,

	# The install root; defaults to $env:BASSIA_HOME or %LOCALAPPDATA%\Bassia.
	[string] $Root,

	# Keep the current default version instead of switching to this one.
	[switch] $NoUse,

	# Do not add <root>\current to the user PATH.
	[switch] $NoModifyPath,

	# Reinstall a version that is already installed.
	[switch] $Force
)

$ErrorActionPreference = 'Stop'
$arguments = @{ Package = $PSScriptRoot }
if ($Root) { $arguments.Root = $Root }
if ($NoUse) { $arguments.NoUse = $true }
if ($NoModifyPath) { $arguments.NoModifyPath = $true }
if ($Force) { $arguments.Force = $true }
$command = if ($Check) { 'check' } else { 'install' }
& (Join-Path $PSScriptRoot 'scripts\bassia-versions.ps1') $command @arguments
