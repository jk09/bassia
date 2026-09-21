<#
.SYNOPSIS
Creates a throwaway Bassia monorepo for hand-checking: a random folder in the temp directory, initialized with
`bassia init`, with the jk09/example component added and its HEAD tagged so `bassia agent -select` can use it.

.OUTPUTS
The monorepo's root path, so it can be captured: $r = ./scripts/New-TestMonorepo.ps1
#>
[CmdletBinding()]
param(
	[string] $ComponentUrl = 'https://github.com/jk09/example',
	[string] $Tag = 'tag-base'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Bassia.common.ps1')

$root = Join-Path ([IO.Path]::GetTempPath()) "bassia-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
$null = New-Item -ItemType Directory -Path $root

$null = Invoke-Bassia init $root
$component = Invoke-Bassia -C $root add-component $ComponentUrl
$componentDir = $component.path

& git -C $componentDir tag -a $Tag -m "baseline for bassia agent hand-checks"
if ($LASTEXITCODE -ne 0) { throw "git tag failed in '$componentDir'" }

Write-Host ''
Write-Host "Monorepo: $root"
Write-Host "Component '$($component.name)' at '$componentDir', HEAD tagged '$Tag'."
Write-Host ''
Write-Host 'Run an agent against it with:'
Write-Host "  $(Join-Path $PSScriptRoot 'Invoke-AgentRun.ps1') -Monorepo '$root' -Prompt 'write hello world in C# as $($component.name)/HelloWorld.cs'"
Write-Host "or with the raw CLI:"
Write-Host "  $script:BassiaExe -C '$root' agent -select $($component.name)@$Tag -run 'claude -p --permission-mode acceptEdits ""<prompt>""'"

$root
