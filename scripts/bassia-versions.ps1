<#
.SYNOPSIS
	Builds, installs, selects and removes side-by-side versions of the Bassia CLI for the current user.

.DESCRIPTION
	A version manager in the spirit of nvm: every installed version lives in its own folder under the install root,
	and a directory link `current` points at the default one. Only `current` is on the user's PATH, so switching the
	default retargets the link and never touches PATH again. A single shell can use another version without changing
	the default by dot-sourcing `activate.ps1` / `activate.sh` from the install root, which also reads a
	`.bassia-version` file. Nothing needs elevated privileges.

	Install root: $env:BASSIA_HOME, else %LOCALAPPDATA%\Bassia on Windows, ${XDG_DATA_HOME:-~/.local/share}/bassia
	elsewhere.

	  <root>/
	    versions/<id>/          one framework-dependent (or self-contained) publish per version, plus bassia-install.toml
	    current -> versions/<id>   junction (Windows) or symlink: the default version, on the user's PATH
	    activate.ps1, activate.sh   per-shell override
	    env.sh                  (Unix) puts <root>/current on PATH; sourced from the shell profiles

	A version id is the release tag (v1.2.0), or <ref>-<short commit> for an untagged build (main-e23d631); a Debug
	build appends -debug.

.EXAMPLE
	./scripts/bassia-versions.ps1 install                    # latest v<semver> release tag, Release build, made the default
	./scripts/bassia-versions.ps1 install v0.2.0 -Configuration Debug -NoUse
	./scripts/bassia-versions.ps1 install main               # an untagged build of a branch or commit
	./scripts/bassia-versions.ps1 list
	./scripts/bassia-versions.ps1 use v0.1.0
	./scripts/bassia-versions.ps1 uninstall v0.1.0
	./scripts/bassia-versions.ps1 uninstall -All             # remove every version and the PATH entry
#>
[CmdletBinding()]
param(
	# check | available | install | list | use | uninstall
	[Parameter(Mandatory, Position = 0)]
	[ValidateSet('check', 'available', 'install', 'list', 'use', 'uninstall')]
	[string] $Command,

	# install/check: a release tag (v1.2.0 or 1.2.0), 'latest' (the default), or any branch/commit. use/uninstall: an
	# installed version id.
	[Parameter(Position = 1)]
	[string] $Version,

	# install: the build configuration.
	[ValidateSet('Release', 'Debug')]
	[string] $Configuration = 'Release',

	# install/check/available: let 'latest' pick a pre-release tag (v1.3.0-rc.1) too.
	[switch] $Prerelease,

	# install: publish self-contained for the current runtime instead of framework-dependent.
	[switch] $SelfContained,

	# install: keep the current default instead of switching to the newly installed version.
	[switch] $NoUse,

	# install: rebuild over an existing install. uninstall: remove the default version too.
	[switch] $Force,

	# install/check/available: do not fetch tags from origin first.
	[switch] $NoFetch,

	# install: do not register <root>/current on the user's PATH.
	[switch] $NoModifyPath,

	# uninstall: remove every version, the activation scripts and the PATH registration.
	[switch] $All,

	# The Bassia source repository to build from; defaults to the repository containing this script.
	[string] $Source,

	# The install root; defaults to $env:BASSIA_HOME or the per-user location above.
	[string] $Root
)

Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'
$env:DOTNET_NOLOGO = '1'

$script:OnWindows = ($PSVersionTable.PSEdition -eq 'Desktop') -or $IsWindows
$script:ReleaseTagPattern = '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$'
$script:ProfileMarker = '# bassia: added by bassia-versions.ps1'

# ----- helpers ------------------------------------------------------------------------------------------------------

function Write-Step([string] $Message) { Write-Host "bassia-versions: $Message" }

# Runs a native command, capturing stdout and stderr; throws on a non-zero exit unless -AllowFailure.
function Invoke-Native {
	param([string] $FilePath, [string[]] $Arguments, [switch] $AllowFailure)

	$ErrorActionPreference = 'Continue'
	$output = & $FilePath @Arguments 2>&1
	$code = $LASTEXITCODE
	$stdout = @($output | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" })
	$stderr = @($output | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" })
	if ($code -ne 0 -and -not $AllowFailure) {
		$detail = (@($stderr) + @($stdout) | Select-Object -Last 40) -join [Environment]::NewLine
		throw "'$FilePath $($Arguments -join ' ')' failed with exit code $code.$([Environment]::NewLine)$detail"
	}
	[pscustomobject]@{ ExitCode = $code; Output = $stdout; Error = $stderr }
}

function Invoke-Git([string[]] $Arguments, [switch] $AllowFailure) {
	Invoke-Native -FilePath 'git' -Arguments (@('-C', $script:SourceRepo) + $Arguments) -AllowFailure:$AllowFailure
}

function Get-InstallRoot {
	if ($Root) { return [IO.Path]::GetFullPath($Root) }
	if ($env:BASSIA_HOME) { return [IO.Path]::GetFullPath($env:BASSIA_HOME) }
	if ($script:OnWindows) { return Join-Path $env:LOCALAPPDATA 'Bassia' }
	$data = if ($env:XDG_DATA_HOME) { $env:XDG_DATA_HOME } else { Join-Path $HOME '.local/share' }
	Join-Path $data 'bassia'
}

function Get-SourceRepo {
	$start = if ($Source) { $Source } else { $PSScriptRoot }
	if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'git was not found on PATH; Bassia needs git to build and to run.' }
	$result = Invoke-Native -FilePath 'git' -Arguments @('-C', $start, 'rev-parse', '--show-toplevel') -AllowFailure
	if ($result.ExitCode -ne 0) { throw "'$start' is not inside a git clone of the Bassia repository." }
	[IO.Path]::GetFullPath($result.Output[0])
}

# A version id becomes a folder name: no path separators, no leading dot, nothing a shell would trip over.
function Test-VersionId([string] $Id) {
	$Id -match '^[0-9A-Za-z][0-9A-Za-z._+-]*$' -and $Id -notmatch '\.\.'
}

function ConvertTo-VersionIdPart([string] $Text) {
	($Text -replace '[^0-9A-Za-z._-]+', '-').Trim('-', '.')
}

# ----- release tags -------------------------------------------------------------------------------------------------

function ConvertTo-Release([string] $Tag) {
	if ($Tag -cnotmatch $script:ReleaseTagPattern) { return $null }
	$pre = if ($Matches[4]) { $Matches[4] } else { '' }
	[pscustomobject]@{
		Tag        = $Tag
		Major      = [long] $Matches[1]
		Minor      = [long] $Matches[2]
		Patch      = [long] $Matches[3]
		Prerelease = $pre
		Version    = "$($Matches[1]).$($Matches[2]).$($Matches[3])$(if ($pre) { "-$pre" })"
	}
}

# Semantic-version precedence (semver.org, section 11).
function Compare-Release($A, $B) {
	foreach ($part in 'Major', 'Minor', 'Patch') {
		if ($A.$part -ne $B.$part) { return [Math]::Sign($A.$part - $B.$part) }
	}
	if ($A.Prerelease -eq $B.Prerelease) { return 0 }
	if (-not $A.Prerelease) { return 1 }
	if (-not $B.Prerelease) { return -1 }
	$x = $A.Prerelease.Split('.')
	$y = $B.Prerelease.Split('.')
	for ($i = 0; $i -lt [Math]::Min($x.Count, $y.Count); $i++) {
		$xNumeric = $x[$i] -match '^\d+$'
		$yNumeric = $y[$i] -match '^\d+$'
		if ($xNumeric -and $yNumeric) {
			$c = ([decimal] $x[$i]).CompareTo([decimal] $y[$i])
		}
		elseif ($xNumeric) { $c = -1 }
		elseif ($yNumeric) { $c = 1 }
		else { $c = [string]::CompareOrdinal($x[$i], $y[$i]) }
		if ($c -ne 0) { return [Math]::Sign($c) }
	}
	[Math]::Sign($x.Count - $y.Count)
}

function Update-Tags {
	if ($NoFetch) { return }
	$remotes = @((Invoke-Git @('remote')).Output)
	if ($remotes -notcontains 'origin') { return }
	$fetch = Invoke-Git @('fetch', '--tags', '--quiet', 'origin') -AllowFailure
	if ($fetch.ExitCode -ne 0) {
		Write-Warning "Could not fetch tags from origin; using the local tags. $($fetch.Error -join ' ')"
	}
}

# The release tags, newest first.
function Get-Releases([switch] $IncludePrerelease) {
	$list = [System.Collections.Generic.List[object]]::new()
	foreach ($tag in (Invoke-Git @('tag', '--list', 'v*')).Output) {
		$release = ConvertTo-Release $tag
		if ($release -and ($IncludePrerelease -or -not $release.Prerelease)) { $list.Add($release) }
	}
	$list.Sort([Comparison[object]] { param($a, $b) Compare-Release $b $a })
	, $list
}

# Resolves an install request to the commit to build and the version id/labels it gets.
function Resolve-Target([string] $Requested) {
	if (-not $Requested -or $Requested -eq 'latest') {
		$releases = Get-Releases -IncludePrerelease:$Prerelease
		if ($releases.Count -eq 0) {
			$kind = if ($Prerelease) { 'release or pre-release' } else { 'release' }
			throw "No $kind tag (v<major>.<minor>.<patch>) found in '$script:SourceRepo'. Tag a release first, name a pre-release with -Prerelease, or install a branch or commit by name."
		}
		$Requested = $releases[0].Tag
	}
	elseif ($Requested -match '^\d+\.\d+\.\d+' -and (Invoke-Git @('rev-parse', '--verify', '--quiet', "refs/tags/v$Requested") -AllowFailure).ExitCode -eq 0) {
		$Requested = "v$Requested"
	}

	$commit = Invoke-Git @('rev-parse', '--verify', '--quiet', "$Requested^{commit}") -AllowFailure
	if ($commit.ExitCode -ne 0) {
		$releases = Get-Releases -IncludePrerelease
		$known = ($releases | Select-Object -First 10 | ForEach-Object Tag) -join ', '
		if (-not $known) { $known = 'none' }
		throw "'$Requested' is neither a tag nor a commit in '$script:SourceRepo'. Release tags: $known."
	}
	$sha = $commit.Output[0]
	$short = $sha.Substring(0, 7)

	$isTag = (Invoke-Git @('rev-parse', '--verify', '--quiet', "refs/tags/$Requested") -AllowFailure).ExitCode -eq 0
	$release = if ($isTag) { ConvertTo-Release $Requested } else { $null }
	if ($release) {
		$id = $release.Tag
		$assemblyVersion = $release.Version
		$label = "$($release.Version)+$short"
	}
	else {
		$part = ConvertTo-VersionIdPart $Requested
		if ($part -eq $short -or $sha.StartsWith($Requested)) { $id = $short } else { $id = "$part-$short" }
		$assemblyVersion = '0.0.0'
		$label = "0.0.0-$(($part -replace '[^0-9A-Za-z-]', '-'))+$short"
	}
	if ($Configuration -eq 'Debug') { $id = "$id-debug" }
	if (-not (Test-VersionId $id)) { throw "'$Requested' does not make a usable version id ('$id')." }

	[pscustomobject]@{
		Requested       = $Requested
		Id              = $id
		IsRelease       = [bool] $release
		Commit          = $sha
		ShortCommit     = $short
		AssemblyVersion = $assemblyVersion
		Label           = $label
	}
}

# ----- requirements -------------------------------------------------------------------------------------------------

function ConvertTo-SdkVersion([string] $Text) {
	if ($Text -match '^(\d+)\.(\d+)\.(\d+)') { return [version] "$($Matches[1]).$($Matches[2]).$($Matches[3])" }
	$null
}

function Get-InstalledSdks {
	if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { return @() }
	$list = Invoke-Native -FilePath 'dotnet' -Arguments @('--list-sdks') -AllowFailure
	@($list.Output | ForEach-Object {
			if ($_ -match '^(\S+)\s+\[(.+)\]$') {
				[pscustomobject]@{ Text = $Matches[1]; Version = ConvertTo-SdkVersion $Matches[1]; Path = $Matches[2] }
			}
		} | Where-Object { $_ -and $_.Version })
}

# What the commit needs: global.json's sdk.version if it pins one, else the SDK of the CLI's target framework.
function Get-Requirement([string] $Commit) {
	$globalJson = Invoke-Git @('show', "$($Commit):global.json") -AllowFailure
	if ($globalJson.ExitCode -eq 0) {
		$sdk = ($globalJson.Output -join "`n" | ConvertFrom-Json).sdk
		if ($sdk -and $sdk.version) {
			return [pscustomobject]@{ Minimum = ConvertTo-SdkVersion $sdk.version; Source = "global.json pins SDK $($sdk.version)" }
		}
	}
	$project = Invoke-Git @('show', "$($Commit):Bassia/Bassia.csproj") -AllowFailure
	if ($project.ExitCode -ne 0) { throw "Commit $Commit has no Bassia/Bassia.csproj; it is not a buildable Bassia version." }
	$text = $project.Output -join "`n"
	if ($text -notmatch '<TargetFramework>net(\d+)\.(\d+)</TargetFramework>') {
		throw "Cannot read the target framework from Bassia/Bassia.csproj at $Commit."
	}
	[pscustomobject]@{
		Minimum = [version] "$($Matches[1]).$($Matches[2]).100"
		Source  = "Bassia targets net$($Matches[1]).$($Matches[2])"
	}
}

function Test-Requirement($Target) {
	$requirement = Get-Requirement $Target.Commit
	$sdks = @(Get-InstalledSdks)
	$suitable = @($sdks | Where-Object { $_.Version -ge $requirement.Minimum } | Sort-Object Version -Descending)
	$gitVersion = (Invoke-Native -FilePath 'git' -Arguments @('--version')).Output[0]

	$hint = if ($script:OnWindows) {
		"Install it for the current user (no elevation): winget install --scope user Microsoft.DotNet.SDK.$($requirement.Minimum.Major), or & ([scriptblock]::Create((irm https://dot.net/v1/dotnet-install.ps1))) -Channel $($requirement.Minimum.Major).0 and add %LOCALAPPDATA%\Microsoft\dotnet to PATH."
	}
	else {
		"Install it for the current user (no elevation): curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel $($requirement.Minimum.Major).0 and add ~/.dotnet to PATH; or use your package manager."
	}

	[pscustomobject]@{
		Target       = $Target
		Required     = $requirement
		Sdks         = $sdks
		Sdk          = if ($suitable.Count) { $suitable[0] } else { $null }
		Git          = $gitVersion
		Satisfied    = $suitable.Count -gt 0
		Hint         = $hint
	}
}

function Write-Requirement($Check) {
	Write-Step "version $($Check.Target.Id): commit $($Check.Target.ShortCommit) ($($Check.Target.Requested))"
	Write-Step "requires .NET SDK >= $($Check.Required.Minimum) ($($Check.Required.Source)) and git"
	Write-Step "found:   $($Check.Git)"
	if ($Check.Sdks.Count -eq 0) { Write-Step 'found:   no .NET SDK (dotnet is not on PATH or has no SDK)' }
	else { Write-Step "found:   .NET SDK $(($Check.Sdks | ForEach-Object Text) -join ', ')" }
	if ($Check.Satisfied) { Write-Step "ok:      builds with .NET SDK $($Check.Sdk.Text)" }
	else { Write-Step "missing: a .NET SDK >= $($Check.Required.Minimum). $($Check.Hint)" }
}

# ----- install root -------------------------------------------------------------------------------------------------

function Get-VersionsDirectory { Join-Path $script:InstallRoot 'versions' }
function Get-CurrentLink { Join-Path $script:InstallRoot 'current' }

function Get-LinkTarget([string] $Path) {
	$item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
	if (-not $item) { return $null }
	$target = @($item.Target) | Select-Object -First 1
	if (-not $target) { return $null }
	$target
}

function Get-DefaultVersion {
	$target = Get-LinkTarget (Get-CurrentLink)
	if ($target) { Split-Path -Leaf ($target.TrimEnd('\', '/')) } else { $null }
}

# Removes a junction or symlink itself, never what it points at.
function Remove-Link([string] $Path) {
	$item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
	if (-not $item) { return }
	if (-not $item.LinkType) { throw "'$Path' is not a link; refusing to remove it." }
	if ($script:OnWindows) { [IO.Directory]::Delete($Path, $false) } else { [IO.File]::Delete($Path) }
}

function Set-DefaultVersion([string] $Id) {
	$target = Join-Path (Get-VersionsDirectory) $Id
	$link = Get-CurrentLink
	Remove-Link $link
	$type = if ($script:OnWindows) { 'Junction' } else { 'SymbolicLink' }
	$null = New-Item -ItemType $type -Path $link -Target $target
}

function Read-InstallRecord([string] $Directory) {
	$record = [ordered]@{}
	$file = Join-Path $Directory 'bassia-install.toml'
	if (Test-Path -LiteralPath $file) {
		foreach ($line in Get-Content -LiteralPath $file) {
			if ($line -match '^\s*([a-z_]+)\s*=\s*"(.*)"\s*$') { $record[$Matches[1]] = $Matches[2] -replace '\\(.)', '$1' }
			elseif ($line -match '^\s*([a-z_]+)\s*=\s*(\S+)\s*$') { $record[$Matches[1]] = $Matches[2] }
		}
	}
	$record
}

function Get-InstalledVersions {
	$directory = Get-VersionsDirectory
	if (-not (Test-Path -LiteralPath $directory)) { return @() }
	$default = Get-DefaultVersion
	@(Get-ChildItem -LiteralPath $directory -Directory | Where-Object { -not $_.Name.StartsWith('.') } | ForEach-Object {
			$record = Read-InstallRecord $_.FullName
			[pscustomobject]@{
				Id            = $_.Name
				Default       = $_.Name -eq $default
				Session       = $_.Name -eq $env:BASSIA_VERSION
				Ref           = $record['ref']
				Commit        = $record['commit']
				Configuration = $record['configuration']
				Installed     = $record['installed_at']
				Path          = $_.FullName
			}
		})
}

function ConvertTo-TomlString([string] $Value) {
	'"' + ($Value -replace '\\', '\\' -replace '"', '\"') + '"'
}

function Get-BassiaExecutable([string] $Directory) {
	$name = if ($script:OnWindows) { 'Bassia.exe' } else { 'Bassia' }
	Join-Path $Directory $name
}

# The activation scripts and (on Unix) env.sh, regenerated on every install so they follow this script's version.
function Write-RootScripts {
	$root = $script:InstallRoot
	$activatePs1 = @'
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
'@
	Set-Content -LiteralPath (Join-Path $root 'activate.ps1') -Value $activatePs1 -Encoding utf8

	if ($script:OnWindows) { return }

	$quotedRoot = "'" + ($root -replace "'", "'\''") + "'"
	$activateSh = @'
# Use an installed Bassia version in this shell only (bash, zsh); the default (<root>/current) stays as it is.
# Usage: . <root>/activate.sh [<version>]   - without a version, the nearest .bassia-version file names it.
# Undo with: bassia_deactivate
_bassia_root=__ROOT__
_bassia_version="${1:-}"
if [ -z "$_bassia_version" ]; then
	_bassia_dir="$PWD"
	while [ -n "$_bassia_dir" ]; do
		if [ -f "$_bassia_dir/.bassia-version" ]; then
			_bassia_version="$(head -n 1 "$_bassia_dir/.bassia-version" | tr -d ' \t\r')"
			break
		fi
		if [ "$_bassia_dir" = "/" ]; then break; fi
		_bassia_dir="$(dirname "$_bassia_dir")"
	done
	unset _bassia_dir
fi
if [ -z "$_bassia_version" ]; then
	echo "bassia: name a version, or add a .bassia-version file to this folder or a parent." >&2
elif [ ! -d "$_bassia_root/versions/$_bassia_version" ]; then
	echo "bassia: version '$_bassia_version' is not installed. Installed: $(ls "$_bassia_root/versions" 2>/dev/null | tr '\n' ' ')" >&2
else
	if command -v bassia_deactivate >/dev/null 2>&1; then bassia_deactivate; fi
	_BASSIA_OLD_PATH="$PATH"
	PATH="$_bassia_root/versions/$_bassia_version:$PATH"
	BASSIA_VERSION="$_bassia_version"
	export PATH BASSIA_VERSION
	bassia_deactivate() {
		PATH="$_BASSIA_OLD_PATH"
		export PATH
		unset BASSIA_VERSION _BASSIA_OLD_PATH
		unset -f bassia_deactivate
	}
	echo "bassia: using $_bassia_version in this shell (bassia_deactivate to undo)"
fi
unset _bassia_root _bassia_version
'@
	Set-Content -LiteralPath (Join-Path $root 'activate.sh') -Value ($activateSh.Replace('__ROOT__', $quotedRoot) -replace "`r`n", "`n") -Encoding utf8 -NoNewline

	$envSh = @'
# Puts the default Bassia version on PATH. Sourced from the shell profiles by bassia-versions.ps1.
BASSIA_HOME=__ROOT__
export BASSIA_HOME
case ":$PATH:" in
	*":$BASSIA_HOME/current:"*) ;;
	*) PATH="$BASSIA_HOME/current:$PATH"; export PATH ;;
esac
'@
	Set-Content -LiteralPath (Join-Path $root 'env.sh') -Value ($envSh.Replace('__ROOT__', $quotedRoot) -replace "`r`n", "`n") -Encoding utf8 -NoNewline
}

# ----- PATH registration --------------------------------------------------------------------------------------------

function Get-UnixProfiles {
	$profiles = @(Join-Path $HOME '.profile')
	foreach ($name in '.bashrc', '.zshrc') {
		$file = Join-Path $HOME $name
		if (Test-Path -LiteralPath $file) { $profiles += $file }
	}
	$profiles
}

function Register-Path {
	$current = Get-CurrentLink
	if ($script:OnWindows) {
		$key = Get-Item -LiteralPath 'HKCU:\Environment'
		$raw = [string] $key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
		$entries = @($raw -split ';' | Where-Object { $_ })
		if ($entries -notcontains $current) {
			Set-ItemProperty -LiteralPath 'HKCU:\Environment' -Name 'Path' -Type ExpandString -Value (($entries + $current) -join ';')
			Write-Step "added $current to the user PATH"
		}
		# Also broadcasts the environment change, so new terminals see the PATH entry.
		[Environment]::SetEnvironmentVariable('BASSIA_HOME', $script:InstallRoot, 'User')
	}
	else {
		$line = '. ' + "'" + ((Join-Path $script:InstallRoot 'env.sh') -replace "'", "'\''") + "' $script:ProfileMarker"
		foreach ($file in Get-UnixProfiles) {
			$existing = if (Test-Path -LiteralPath $file) { @(Get-Content -LiteralPath $file) } else { @() }
			if ($existing -contains $line) { continue }
			$kept = @($existing | Where-Object { $_ -notlike "*$script:ProfileMarker" })
			Set-Content -LiteralPath $file -Value (@($kept) + $line) -Encoding utf8
			Write-Step "sourced env.sh from $file"
		}
	}
	if (($env:PATH -split [IO.Path]::PathSeparator) -notcontains $current) {
		Write-Step "open a new terminal (or log in again) to get 'bassia' on PATH"
	}
}

function Unregister-Path {
	$current = Get-CurrentLink
	if ($script:OnWindows) {
		$key = Get-Item -LiteralPath 'HKCU:\Environment'
		$raw = [string] $key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
		$entries = @($raw -split ';' | Where-Object { $_ })
		if ($entries -contains $current) {
			Set-ItemProperty -LiteralPath 'HKCU:\Environment' -Name 'Path' -Type ExpandString -Value ((@($entries | Where-Object { $_ -ne $current })) -join ';')
			Write-Step "removed $current from the user PATH"
		}
		[Environment]::SetEnvironmentVariable('BASSIA_HOME', $null, 'User')
	}
	else {
		foreach ($file in '.profile', '.bashrc', '.zshrc' | ForEach-Object { Join-Path $HOME $_ }) {
			if (-not (Test-Path -LiteralPath $file)) { continue }
			$existing = @(Get-Content -LiteralPath $file)
			$kept = @($existing | Where-Object { $_ -notlike "*$script:ProfileMarker" })
			if ($kept.Count -ne $existing.Count) {
				Set-Content -LiteralPath $file -Value $kept -Encoding utf8
				Write-Step "removed the env.sh line from $file"
			}
		}
	}
}

# ----- commands -----------------------------------------------------------------------------------------------------

function Invoke-Check {
	$script:SourceRepo = Get-SourceRepo
	Update-Tags
	$check = Test-Requirement (Resolve-Target $Version)
	Write-Requirement $check
	if (-not $check.Satisfied) { exit 1 }
}

function Invoke-Available {
	$script:SourceRepo = Get-SourceRepo
	Update-Tags
	$installed = @(Get-InstalledVersions | ForEach-Object Id)
	$releases = Get-Releases -IncludePrerelease:$Prerelease
	if ($releases.Count -eq 0) { Write-Step "no release tags (v<major>.<minor>.<patch>) in $script:SourceRepo"; return }
	$latest = $releases | Where-Object { -not $_.Prerelease } | Select-Object -First 1
	foreach ($release in $releases) {
		$notes = @()
		if ($latest -and $release.Tag -eq $latest.Tag) { $notes += 'latest' }
		if ($installed -contains $release.Tag) { $notes += 'installed' }
		if ($installed -contains "$($release.Tag)-debug") { $notes += 'installed (debug)' }
		$date = (Invoke-Git @('log', '-1', '--format=%cs', "$($release.Tag)^{commit}")).Output[0]
		'{0,-24} {1}  {2}' -f $release.Tag, $date, ($notes -join ', ')
	}
}

function Invoke-Install {
	$script:SourceRepo = Get-SourceRepo
	Update-Tags
	$target = Resolve-Target $Version
	$check = Test-Requirement $target
	Write-Requirement $check
	if (-not $check.Satisfied) { throw "Cannot build $($target.Id): no .NET SDK >= $($check.Required.Minimum). $($check.Hint)" }

	$versions = Get-VersionsDirectory
	$destination = Join-Path $versions $target.Id
	if ((Test-Path -LiteralPath $destination) -and -not $Force) {
		Write-Step "$($target.Id) is already installed at $destination (pass -Force to rebuild it)"
	}
	else {
		$null = New-Item -ItemType Directory -Force -Path $versions
		Get-ChildItem -LiteralPath $versions -Directory -Filter '.staging-*' | Remove-Item -Recurse -Force
		$staging = Join-Path $versions ".staging-$($target.Id)-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
		$worktree = Join-Path ([IO.Path]::GetTempPath()) "bassia-build-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
		try {
			Write-Step "checking out $($target.Requested) ($($target.ShortCommit)) into a temporary worktree"
			$null = Invoke-Git @('worktree', 'add', '--detach', '--quiet', $worktree, $target.Commit)

			$publish = @(
				'publish', (Join-Path $worktree 'Bassia/Bassia.csproj'),
				'--configuration', $Configuration,
				'--output', $staging,
				'--nologo', '-v:q',
				"-p:Version=$($target.AssemblyVersion)",
				"-p:InformationalVersion=$($target.Label)",
				'-p:IncludeSourceRevisionInInformationalVersion=false'
			)
			if ($SelfContained) { $publish += @('--use-current-runtime', '--self-contained') }
			else { $publish += @('--no-self-contained') }
			Write-Step "building $($target.Id) ($Configuration, $(if ($SelfContained) { 'self-contained' } else { 'framework-dependent' })) with .NET SDK $($check.Sdk.Text)"
			$null = Invoke-Native -FilePath 'dotnet' -Arguments $publish

			$exe = Get-BassiaExecutable $staging
			if (-not (Test-Path -LiteralPath $exe)) { throw "The build produced no $(Split-Path -Leaf $exe) in $staging." }
			if (-not $script:OnWindows) {
				# A lowercase 'bassia' next to the apphost, unless the file system is case-insensitive.
				$lower = Join-Path $staging 'bassia'
				if (-not (Test-Path -LiteralPath $lower)) { $null = New-Item -ItemType SymbolicLink -Path $lower -Target 'Bassia' }
			}

			$smoke = Invoke-Native -FilePath $exe -Arguments @('version') -AllowFailure
			if ($smoke.ExitCode -ne 0 -or -not ($smoke.Output -match '^ok = true$')) {
				throw "The built bassia failed its smoke test (bassia version): $(($smoke.Output + $smoke.Error) -join ' ')"
			}

			$record = @(
				'# Written by bassia-versions.ps1 when this version was installed.'
				"id = $(ConvertTo-TomlString $target.Id)"
				"ref = $(ConvertTo-TomlString $target.Requested)"
				"commit = $(ConvertTo-TomlString $target.Commit)"
				"version = $(ConvertTo-TomlString $target.Label)"
				"configuration = $(ConvertTo-TomlString $Configuration)"
				"self_contained = $(if ($SelfContained) { 'true' } else { 'false' })"
				"sdk = $(ConvertTo-TomlString $check.Sdk.Text)"
				"source = $(ConvertTo-TomlString $script:SourceRepo)"
				"installed_at = $(ConvertTo-TomlString ([DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')))"
			)
			Set-Content -LiteralPath (Join-Path $staging 'bassia-install.toml') -Value $record -Encoding utf8

			if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
			Move-Item -LiteralPath $staging -Destination $destination
			Write-Step "installed $($target.Id) ($($target.Label)) at $destination"
		}
		finally {
			if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue }
			$null = Invoke-Git @('worktree', 'remove', '--force', $worktree) -AllowFailure
			if (Test-Path -LiteralPath $worktree) { Remove-Item -LiteralPath $worktree -Recurse -Force -ErrorAction SilentlyContinue }
			$null = Invoke-Git @('worktree', 'prune') -AllowFailure
		}
	}

	Write-RootScripts
	$default = Get-DefaultVersion
	if (-not $NoUse -or -not $default -or -not (Test-Path -LiteralPath (Join-Path $versions $default))) {
		Set-DefaultVersion $target.Id
		Write-Step "default version: $($target.Id)"
	}
	else {
		Write-Step "default version stays $default (bassia-versions.ps1 use $($target.Id) to switch)"
	}
	if (-not $NoModifyPath) { Register-Path }
}

function Invoke-List {
	$installed = @(Get-InstalledVersions)
	if ($installed.Count -eq 0) { Write-Step "no versions installed in $script:InstallRoot"; return }
	foreach ($v in $installed | Sort-Object Id) {
		$mark = if ($v.Session) { '>' } elseif ($v.Default) { '*' } else { ' ' }
		$commit = if ($v.Commit) { $v.Commit.Substring(0, [Math]::Min(7, $v.Commit.Length)) } else { '?' }
		'{0} {1,-28} {2,-8} {3,-8} {4}' -f $mark, $v.Id, $commit, $v.Configuration, $v.Installed
	}
	Write-Host "(* default, > active in this shell via activate; root: $script:InstallRoot)"
}

function Invoke-Use {
	if (-not $Version) {
		$default = Get-DefaultVersion
		if ($default) { Write-Step "default version: $default" } else { Write-Step 'no default version' }
		if ($env:BASSIA_VERSION) { Write-Step "this shell uses $env:BASSIA_VERSION (activate)" }
		return
	}
	$installed = @(Get-InstalledVersions | ForEach-Object Id)
	$id = $Version
	if ($installed -notcontains $id -and $installed -contains "v$id") { $id = "v$id" }
	if ($installed -notcontains $id) {
		throw "Version '$Version' is not installed. Installed: $(if ($installed) { $installed -join ', ' } else { 'none' })."
	}
	Set-DefaultVersion $id
	Write-Step "default version: $id"
	if ($env:BASSIA_VERSION -and $env:BASSIA_VERSION -ne $id) {
		Write-Step "note: this shell still uses $env:BASSIA_VERSION through activate (bassia_deactivate to drop it)"
	}
}

function Invoke-Uninstall {
	if ($All) {
		Remove-Link (Get-CurrentLink)
		Unregister-Path
		$versions = Get-VersionsDirectory
		if (Test-Path -LiteralPath $versions) {
			foreach ($dir in Get-ChildItem -LiteralPath $versions -Directory -Force) {
				Remove-Item -LiteralPath $dir.FullName -Recurse -Force
				if (-not $dir.Name.StartsWith('.')) { Write-Step "removed $($dir.Name)" }
			}
			Remove-Item -LiteralPath $versions -Force
		}
		foreach ($name in 'activate.ps1', 'activate.sh', 'env.sh') {
			$file = Join-Path $script:InstallRoot $name
			if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
		}
		if ((Test-Path -LiteralPath $script:InstallRoot) -and -not (Get-ChildItem -LiteralPath $script:InstallRoot -Force)) {
			Remove-Item -LiteralPath $script:InstallRoot -Force
		}
		Write-Step "Bassia is uninstalled from $script:InstallRoot"
		return
	}

	if (-not $Version) { throw 'Name the version to uninstall (bassia-versions.ps1 list shows them), or pass -All.' }
	$installed = @(Get-InstalledVersions | ForEach-Object Id)
	$id = $Version
	if ($installed -notcontains $id -and $installed -contains "v$id") { $id = "v$id" }
	if (-not (Test-VersionId $id) -or $installed -notcontains $id) {
		throw "Version '$Version' is not installed. Installed: $(if ($installed) { $installed -join ', ' } else { 'none' })."
	}
	$default = Get-DefaultVersion
	if ($id -eq $default) {
		if (-not $Force) {
			$others = @($installed | Where-Object { $_ -ne $id })
			$advice = if ($others) { "Switch first (use $($others[-1])) or pass -Force" } else { 'Pass -Force (it is the only version), or uninstall -All' }
			throw "$id is the default version. $advice."
		}
		Remove-Link (Get-CurrentLink)
		Write-Step "$id was the default; no version is the default now (bassia-versions.ps1 use <version>)"
	}
	Remove-Item -LiteralPath (Join-Path (Get-VersionsDirectory) $id) -Recurse -Force
	Write-Step "removed $id"
}

$script:InstallRoot = Get-InstallRoot
switch ($Command) {
	'check' { Invoke-Check }
	'available' { Invoke-Available }
	'install' { Invoke-Install }
	'list' { Invoke-List }
	'use' { Invoke-Use }
	'uninstall' { Invoke-Uninstall }
}
