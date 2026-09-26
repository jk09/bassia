<#
.SYNOPSIS
Runs `bassia run start` in a monorepo created by New-TestMonorepo.ps1, driving Claude Code non-interactively with the
given prompt.

.EXAMPLE
./scripts/Invoke-AgentRun.ps1 -Monorepo $r -Prompt 'write hello world in C# as example/HelloWorld.cs'

.EXAMPLE
./scripts/Invoke-AgentRun.ps1 -Monorepo $r -Prompt 'add a README section' -Select 'example@tag-base' -Agent 'claude -p --model sonnet'
#>
[CmdletBinding()]
param(
	[Parameter(Mandatory)] [string] $Monorepo,
	[Parameter(Mandatory)] [string] $Prompt,
	# <component>@<tag>[,<component>@<tag>...]; defaults to what New-TestMonorepo.ps1 sets up.
	[string] $Select = 'example@tag-base',
	# The agent command; the prompt is appended as its last, quoted argument. Non-interactive claude needs
	# permission to edit files, otherwise it answers inline and the run changes nothing.
	[string] $Agent = 'claude -p --permission-mode acceptEdits'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Bassia.common.ps1')

if (-not (Test-Path -LiteralPath (Join-Path $Monorepo '.bassia') -PathType Container)) {
	throw "'$Monorepo' is not a Bassia monorepo (no .bassia folder). Create one with New-TestMonorepo.ps1."
}

# The -run value is handed to cmd.exe, which strips only the outer quotes, so the prompt keeps its own.
$command = "$Agent `"$($Prompt.Replace('"', '\"'))`""

$result = Invoke-Bassia -C $Monorepo run start -select $Select -run $command

Write-Host ''
Write-Host "Run $($result.run_id): $($result.status)"
foreach ($component in $result.component) {
	$where = if ($component.result_tag) { " -> $($component.result_tag) in $(Join-Path $Monorepo $component.name)" } else { '' }
	Write-Host "  $($component.name)@$($component.commitish): $($component.result_status)$where"
}

$pushed = $result.component | Where-Object { $_.result_status -eq 'pushed' }
if ($pushed) {
	Write-Host ''
	Write-Host 'Inspect a result with:'
	foreach ($component in $pushed) {
		Write-Host "  git -C '$(Join-Path $Monorepo $component.name)' log -1 --format=%B $($component.result_tag)"
	}
}

$result
