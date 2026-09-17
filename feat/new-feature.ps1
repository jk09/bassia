[CmdletBinding()]
param()

$proposedDirectory = Join-Path $PSScriptRoot 'proposed'
$templatePath = Join-Path $PSScriptRoot 'template.md'

if (-not (Test-Path -LiteralPath $templatePath -PathType Leaf)) {
	throw "Feature template not found: $templatePath"
}

do {
	$featureId = "xam-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
	$featureDirectory = Join-Path $proposedDirectory $featureId
} while (Test-Path -LiteralPath $featureDirectory)

$null = New-Item -ItemType Directory -Path $featureDirectory
Copy-Item -LiteralPath $templatePath -Destination (Join-Path $featureDirectory 'FEAT.md')

Write-Output $featureDirectory