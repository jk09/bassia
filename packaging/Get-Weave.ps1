<#
.SYNOPSIS
	Downloads the weave-driver pinned in weave.toml for a .NET runtime identifier and verifies its checksum.

.DESCRIPTION
	Fetches weave-driver-<target>.tar.gz from weave's GitHub release, refuses it unless its SHA-256 is the one pinned
	in weave.toml, and extracts weave-driver (weave-driver.exe on Windows) into the destination folder. Used by
	New-BassiaPackage.ps1 and by CI, which runs the tests that need the real weave with it.

.EXAMPLE
	./packaging/Get-Weave.ps1 -Rid linux-x64 -Destination ./tools
#>
[CmdletBinding()]
param(
	# The runtime identifier of the package: win-x64, linux-x64, linux-arm64, osx-x64 or osx-arm64.
	[Parameter(Mandatory)]
	[string] $Rid,

	# The folder that receives weave-driver; created if missing.
	[Parameter(Mandatory)]
	[string] $Destination
)

Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'

$targets = @{
	'linux-x64'   = 'x86_64-unknown-linux-gnu'
	'linux-arm64' = 'aarch64-unknown-linux-gnu'
	'osx-x64'     = 'x86_64-apple-darwin'
	'osx-arm64'   = 'aarch64-apple-darwin'
	'win-x64'     = 'x86_64-pc-windows-msvc'
}
if (-not $targets.ContainsKey($Rid)) { throw "weave has no build for '$Rid'; supported: $(($targets.Keys | Sort-Object) -join ', ')." }
$target = $targets[$Rid]

# weave.toml: version = "..." at the top, then target = "sha256" lines under [sha256].
$version = $null
$checksums = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $PSScriptRoot 'weave.toml')) {
	if ($line -match '^\s*version\s*=\s*"([^"]+)"') { $version = $Matches[1] }
	elseif ($line -match '^\s*([A-Za-z0-9_.-]+)\s*=\s*"([0-9a-f]{64})"') { $checksums[$Matches[1]] = $Matches[2] }
}
if (-not $version) { throw 'weave.toml names no version.' }
if (-not $checksums.ContainsKey($target)) { throw "weave.toml pins no checksum for $target." }

$asset = "weave-driver-$target.tar.gz"
$url = "https://github.com/Ataraxy-Labs/weave/releases/download/v$version/$asset"
$work = Join-Path ([IO.Path]::GetTempPath()) "bassia-weave-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
$null = New-Item -ItemType Directory -Path $work
try {
	$archive = Join-Path $work $asset
	Write-Host "Get-Weave: downloading $url"
	Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing
	$actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
	if ($actual -ne $checksums[$target]) {
		throw "$asset has SHA-256 $actual, but weave.toml pins $($checksums[$target]); refusing it."
	}

	$extracted = Join-Path $work 'x'
	$null = New-Item -ItemType Directory -Path $extracted
	# tar ships with Windows 10 1803+, macOS and Linux alike.
	& tar -xzf $archive -C $extracted
	if ($LASTEXITCODE -ne 0) { throw "Could not extract $asset." }
	$name = if ($Rid.StartsWith('win-')) { 'weave-driver.exe' } else { 'weave-driver' }
	$driver = Join-Path $extracted $name
	if (-not (Test-Path -LiteralPath $driver)) { throw "$asset has no $name." }

	$null = New-Item -ItemType Directory -Force -Path $Destination
	Copy-Item -LiteralPath $driver -Destination (Join-Path $Destination $name) -Force
	if (-not $Rid.StartsWith('win-')) { & chmod +x (Join-Path $Destination $name) }
	Write-Host "Get-Weave: weave-driver $version ($target) -> $(Join-Path $Destination $name)"
	$version
}
finally {
	Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
