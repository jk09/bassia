<#
.SYNOPSIS
	Installs a Bassia package the way a user would and checks that it works; CI runs it on each package's platform.

.DESCRIPTION
	Unpacks the archive into a temporary folder and checks, against a temporary install root and home (the machine's
	own profile and PATH are left alone):

	1. the installer refuses to install when the .NET runtimes are missing (a stand-in dotnet that lists none);
	2. the installer installs the package, and bassia version reports the packaged version;
	3. the bundled weave-driver runs, and a monorepo's integration triage picks it up with nothing else on PATH;
	4. the version manager lists the installed version, and the installer (or the version manager) uninstalls it.

.EXAMPLE
	./packaging/Test-BassiaPackage.ps1 -Archive dist/bassia-v1.2.0-linux-x64.tar.gz
#>
[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[string] $Archive
)

Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'

$Archive = (Resolve-Path -LiteralPath $Archive).Path
$work = Join-Path ([IO.Path]::GetTempPath()) "bassia-package-test-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
$null = New-Item -ItemType Directory -Path $work

function Assert-That([bool] $Condition, [string] $Message) {
	if (-not $Condition) { throw "Test-BassiaPackage: FAILED: $Message" }
	Write-Host "Test-BassiaPackage: ok: $Message"
}

# Runs a command, echoing its output; returns the output and the exit code.
function Invoke-Logged([string] $FilePath, [string[]] $Arguments) {
	$ErrorActionPreference = 'Continue'
	$output = @(& $FilePath @Arguments 2>&1 | ForEach-Object { "$_" })
	$code = $LASTEXITCODE
	$output | ForEach-Object { Write-Host "  | $_" }
	[pscustomobject]@{ Output = $output; Text = $output -join "`n"; ExitCode = $code }
}

$savedPath = $env:PATH
$savedHome = $env:HOME
try {
	if ($Archive.EndsWith('.zip')) { Expand-Archive -LiteralPath $Archive -DestinationPath $work }
	else { & tar -xzf $Archive -C $work; if ($LASTEXITCODE -ne 0) { throw "Cannot unpack $Archive." } }
	$package = @(Get-ChildItem -LiteralPath $work -Directory)[0].FullName
	$manifest = Get-Content -LiteralPath (Join-Path $package 'package.toml') -Raw
	$id = [regex]::Match($manifest, '(?m)^id = "(.+)"').Groups[1].Value
	$version = [regex]::Match($manifest, '(?m)^version = "(.+)"').Groups[1].Value
	Write-Host "Test-BassiaPackage: $id ($version) from $Archive"

	$root = Join-Path $work 'root'
	$fakeHome = Join-Path $work 'home'
	$null = New-Item -ItemType Directory -Path $fakeHome
	$env:HOME = $fakeHome
	$installer = if ($IsWindows) { Join-Path $package 'install.ps1' } else { Join-Path $package 'install.sh' }
	function Invoke-Installer([string[]] $Arguments) {
		if ($IsWindows) {
			Invoke-Logged 'pwsh' (@('-NoProfile', '-File', $installer, '-Root', $root, '-NoModifyPath') + $Arguments)
		}
		else {
			Invoke-Logged 'bash' (@($installer, '--root', $root, '--no-modify-path') + $Arguments)
		}
	}

	# 1. Missing runtimes: a dotnet that lists no runtime comes first on PATH.
	$fakeBin = Join-Path $work 'fake-dotnet'
	$null = New-Item -ItemType Directory -Path $fakeBin
	if ($IsWindows) { Set-Content -LiteralPath (Join-Path $fakeBin 'dotnet.cmd') -Value '@exit /b 0' }
	else {
		Set-Content -LiteralPath (Join-Path $fakeBin 'dotnet') -Value "#!/bin/sh`nexit 0"
		& chmod +x (Join-Path $fakeBin 'dotnet')
	}
	$env:PATH = $fakeBin + [IO.Path]::PathSeparator + $savedPath
	$refused = Invoke-Installer @()
	$env:PATH = $savedPath
	Assert-That ($refused.ExitCode -ne 0) 'the installer fails without the .NET runtimes'
	Assert-That ($refused.Text -match 'missing: Microsoft\.AspNetCore\.App') 'it names the missing ASP.NET Core runtime'
	Assert-That (-not (Test-Path -LiteralPath (Join-Path $root "versions/$id"))) 'it installs nothing'

	# 2. Install.
	$installed = Invoke-Installer @()
	Assert-That ($installed.ExitCode -eq 0) 'the installer installs the package'
	$current = Join-Path $root 'current'
	$bassia = Join-Path $current $(if ($IsWindows) { 'Bassia.exe' } else { 'bassia' })
	$reported = Invoke-Logged $bassia @('version')
	Assert-That ($reported.ExitCode -eq 0 -and $reported.Text -match [regex]::Escape("version = `"$version`"")) "bassia version reports $version"

	# 3. weave: the driver runs, and bassia finds it next to itself although PATH does not have it.
	$driver = Join-Path $current $(if ($IsWindows) { 'weave-driver.exe' } else { 'weave-driver' })
	$weave = Invoke-Logged $driver @('--version')
	Assert-That ($weave.ExitCode -eq 0 -and $weave.Text -match '^weave-driver \d') 'the bundled weave-driver runs'

	$mono = Join-Path $work 'mono'
	$upstream = Join-Path $work 'upstream'
	$null = New-Item -ItemType Directory -Path $mono, $upstream
	$git = @('-c', 'user.name=Bassia CI', '-c', 'user.email=ci@bassia.invalid')
	$null = & git -C $upstream init -q -b main
	Set-Content -LiteralPath (Join-Path $upstream 'lib.py') -Value "def a():`n    return 1`n"
	$null = & git -C $upstream add .
	$null = & git -C $upstream @git commit -q -m 'Initial commit'
	Push-Location $mono
	try {
		$env:GIT_AUTHOR_NAME = $env:GIT_COMMITTER_NAME = 'Bassia CI'
		$env:GIT_AUTHOR_EMAIL = $env:GIT_COMMITTER_EMAIL = 'ci@bassia.invalid'
		Assert-That ((Invoke-Logged $bassia @('init')).ExitCode -eq 0) 'bassia init creates a monorepo'
		Assert-That ((Invoke-Logged $bassia @('component', 'add', '-url', $upstream, '-name', 'lib')).ExitCode -eq 0) 'bassia component add registers a component'
		$run = Invoke-Logged $bassia @('run', 'start', '-select', 'lib', '-run', 'cd lib && git --version > VERSION.txt')
		Assert-That ($run.ExitCode -eq 0 -and $run.Text -match 'result_status = "pushed"') 'bassia run start records a run that pushes a result'
		$plan = Invoke-Logged $bassia @('integration', 'plan', '-runs', 'all')
		Assert-That ($plan.ExitCode -eq 0 -and $plan.Text -match 'structural_merge = ".*weave-driver') 'the integration triage uses the bundled weave-driver'
	}
	finally {
		Pop-Location
	}

	# 4. The version manager sees it; uninstall removes it.
	if (Get-Command pwsh -ErrorAction SilentlyContinue) {
		$list = Invoke-Logged 'pwsh' @('-NoProfile', '-File', (Join-Path $package 'scripts/bassia-versions.ps1'), 'list', '-Root', $root)
		Assert-That ($list.Text -match [regex]::Escape($id)) 'bassia-versions.ps1 list shows the installed version'
	}
	if ($IsWindows) {
		$removed = Invoke-Logged 'pwsh' @('-NoProfile', '-File', (Join-Path $package 'scripts/bassia-versions.ps1'), 'uninstall', '-All', '-Root', $root)
	}
	else {
		$removed = Invoke-Logged 'bash' @($installer, '--root', $root, '--uninstall', 'all')
	}
	Assert-That ($removed.ExitCode -eq 0 -and -not (Test-Path -LiteralPath $root)) 'uninstalling removes the install root'
	Write-Host "Test-BassiaPackage: $id passed"
}
finally {
	$env:PATH = $savedPath
	$env:HOME = $savedHome
	Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
