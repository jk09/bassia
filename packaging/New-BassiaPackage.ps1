<#
.SYNOPSIS
	Builds the installable Bassia package for one platform: the CLI, weave's weave-driver and the installers.

.DESCRIPTION
	Publishes the CLI framework-dependent for the runtime identifier (the user's machine provides the .NET runtime and
	git), adds the weave-driver pinned in weave.toml, the installers and the version manager, and archives the lot as
	bassia-<id>-<rid>.tar.gz (bassia-<id>-<rid>.zip for Windows) with a .sha256 file beside it. CI runs it for every
	platform; it runs the same way on a developer machine with the .NET SDK and PowerShell 7.

	The version id follows bassia-versions.ps1: the release tag (v1.2.0) when the commit is tagged v<semver> and that
	tag is the ref being built, else <ref>-<short commit> (main-e23d631) with assembly version 0.0.0.

	Package layout (see README.md in this folder, which ships as the package's README.md):

	  bassia-<id>-<rid>/
	    bassia/                       the version folder: Bassia apphost and assemblies, weave-driver, bassia symlink
	    package.toml                  id, version, commit, rid, .NET and weave versions
	    install.sh | install.ps1, install.cmd
	    scripts/bassia-versions.ps1   the version manager (list, use, uninstall); install.ps1 runs it
	    scripts/templates/            activate.ps1, activate.sh, env.sh
	    README.md, LICENSE, third-party/weave/

.EXAMPLE
	./packaging/New-BassiaPackage.ps1 -Rid linux-x64
	./packaging/New-BassiaPackage.ps1 -Rid win-x64 -Ref v1.2.0 -OutputDirectory dist
#>
[CmdletBinding()]
param(
	# win-x64, linux-x64, linux-arm64, osx-x64 or osx-arm64; defaults to this machine's.
	[string] $Rid,

	# The ref being built: a v<semver> release tag, or a branch name for an untagged build. Defaults to the tag at HEAD,
	# else the current branch.
	[string] $Ref,

	# Where the archive and its .sha256 file go.
	[string] $OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist')
)

Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$repo = Split-Path -Parent $PSScriptRoot
$releaseTagPattern = '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$'

function Invoke-Checked([string] $FilePath, [string[]] $Arguments) {
	& $FilePath @Arguments
	if ($LASTEXITCODE -ne 0) { throw "'$FilePath $($Arguments -join ' ')' failed with exit code $LASTEXITCODE." }
}

function Get-GitOutput([string[]] $Arguments) {
	$output = & git -C $repo @Arguments 2>$null
	if ($LASTEXITCODE -ne 0) { return $null }
	"$output".Trim()
}

if (-not $Rid) {
	$os = if ($IsWindows) { 'win' } elseif ($IsMacOS) { 'osx' } else { 'linux' }
	$Rid = "$os-$("$([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)".ToLowerInvariant())"
}
$onWindowsRid = $Rid.StartsWith('win-')

# ----- version ------------------------------------------------------------------------------------------------------

$commit = Get-GitOutput @('rev-parse', 'HEAD')
if (-not $commit) { throw "'$repo' is not a git clone; the package is stamped with its commit." }
$short = $commit.Substring(0, 7)
if (-not $Ref) {
	$Ref = Get-GitOutput @('describe', '--tags', '--exact-match', '--match', 'v*', 'HEAD')
	if (-not $Ref) { $Ref = Get-GitOutput @('rev-parse', '--abbrev-ref', 'HEAD') }
	if (-not $Ref -or $Ref -eq 'HEAD') { $Ref = $short }
}

if ($Ref -cmatch $releaseTagPattern) {
	$tagCommit = Get-GitOutput @('rev-parse', '--verify', '--quiet', "refs/tags/$Ref^{commit}")
	if ($tagCommit -and $tagCommit -ne $commit) { throw "Tag $Ref is commit $tagCommit, but HEAD is $commit." }
	$pre = if ($Matches[4]) { "-$($Matches[4])" } else { '' }
	$id = $Ref
	$assemblyVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3])"
	$label = "$assemblyVersion$pre+$short"
}
else {
	$part = ($Ref -replace '[^0-9A-Za-z._-]+', '-').Trim('-', '.')
	$id = if (-not $part -or $part -eq $short) { $short } else { "$part-$short" }
	$assemblyVersion = '0.0.0'
	$label = "0.0.0-$($part -replace '[^0-9A-Za-z-]', '-')+$short"
}
if ($id -notmatch '^[0-9A-Za-z][0-9A-Za-z._+-]*$' -or $id -match '\.\.') { throw "'$Ref' does not make a usable version id ('$id')." }

$project = Get-Content -LiteralPath (Join-Path $repo 'Bassia/Bassia.csproj') -Raw
if ($project -notmatch '<TargetFramework>net(\d+\.\d+)</TargetFramework>') { throw 'Cannot read the target framework from Bassia/Bassia.csproj.' }
$dotnet = $Matches[1]

# ----- build --------------------------------------------------------------------------------------------------------

$name = "bassia-$id-$Rid"
$work = Join-Path ([IO.Path]::GetTempPath()) "bassia-package-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
$stage = Join-Path $work $name
$payload = Join-Path $stage 'bassia'
try {
	Write-Host "New-BassiaPackage: $name ($label, commit $short, .NET $dotnet)"
	Invoke-Checked 'dotnet' @(
		'publish', (Join-Path $repo 'Bassia/Bassia.csproj'),
		'--configuration', 'Release',
		'--runtime', $Rid,
		'--no-self-contained',
		'--output', $payload,
		'--nologo', '-v:q',
		"-p:Version=$assemblyVersion",
		"-p:InformationalVersion=$label",
		'-p:IncludeSourceRevisionInInformationalVersion=false',
		'-p:DebugType=none'
	)
	$exe = Join-Path $payload $(if ($onWindowsRid) { 'Bassia.exe' } else { 'Bassia' })
	if (-not (Test-Path -LiteralPath $exe)) { throw "The publish produced no $(Split-Path -Leaf $exe)." }

	$weave = & (Join-Path $PSScriptRoot 'Get-Weave.ps1') -Rid $Rid -Destination $payload | Select-Object -Last 1

	if (-not $onWindowsRid) {
		Invoke-Checked 'chmod' @('+x', $exe)
		# A lowercase 'bassia' next to the apphost, unless the file system is case-insensitive (macOS), where it is the apphost.
		$null = New-Item -ItemType SymbolicLink -Path (Join-Path $payload 'bassia') -Target 'Bassia' -ErrorAction SilentlyContinue
	}

	$scripts = Join-Path $stage 'scripts'
	$null = New-Item -ItemType Directory -Path $scripts
	Copy-Item -LiteralPath (Join-Path $repo 'scripts/bassia-versions.ps1') -Destination $scripts
	Copy-Item -LiteralPath (Join-Path $repo 'scripts/templates') -Destination $scripts -Recurse
	Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $stage
	Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $stage
	Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'third-party') -Destination $stage -Recurse
	if ($onWindowsRid) {
		Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.ps1') -Destination $stage
		Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.cmd') -Destination $stage
	}
	else {
		Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.sh') -Destination $stage
		Invoke-Checked 'chmod' @('+x', (Join-Path $stage 'install.sh'))
	}

	Set-Content -LiteralPath (Join-Path $stage 'package.toml') -Encoding utf8 -Value @(
		'# The Bassia release package; read by install.sh and bassia-versions.ps1 -Package.'
		"id = `"$id`""
		"ref = `"$Ref`""
		"version = `"$label`""
		"commit = `"$commit`""
		"rid = `"$Rid`""
		"dotnet = `"$dotnet`""
		"weave = `"$weave`""
		"built_at = `"$([DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ'))`""
	)

	# ----- archive ------------------------------------------------------------------------------------------------------

	$null = New-Item -ItemType Directory -Force -Path $OutputDirectory
	$archive = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) $(if ($onWindowsRid) { "$name.zip" } else { "$name.tar.gz" })
	if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
	if ($onWindowsRid) { Compress-Archive -Path $stage -DestinationPath $archive }
	else {
		# COPYFILE_DISABLE keeps macOS tar from adding ._ files for extended attributes.
		$env:COPYFILE_DISABLE = '1'
		Invoke-Checked 'tar' @('-czf', $archive, '-C', $work, $name)
	}

	$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
	Set-Content -LiteralPath "$archive.sha256" -Value "$hash  $(Split-Path -Leaf $archive)" -Encoding ascii -NoNewline
	Write-Host "New-BassiaPackage: $archive ($([Math]::Round((Get-Item -LiteralPath $archive).Length / 1MB, 1)) MB, sha256 $hash)"
	$archive
}
finally {
	Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
