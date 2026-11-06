<#
.SYNOPSIS
	Starts a throwaway PostgreSQL on a CI runner and exports its connection string for Bassia.
.DESCRIPTION
	Local monorepos (`bassia init`) and clones keep their state and trees in a local PostgreSQL. A hosted runner has the
	server binaries (Windows, Linux) or can install them (macOS, Homebrew) but no running server. This initialises a data
	folder under RUNNER_TEMP with trust authentication, starts it on a free port, and appends BASSIA_POSTGRES and
	BASSIA_TEST_POSTGRES to GITHUB_ENV, so later steps of the job see them.
#>
[CmdletBinding()]
param([int] $Port = 54329)

$ErrorActionPreference = 'Stop'
$exe = { param($name) if ($IsWindows) { "$name.exe" } else { $name } }

function Find-Bin {
	$onPath = Get-Command (& $exe 'pg_ctl') -ErrorAction SilentlyContinue
	if ($onPath) { return Split-Path $onPath.Source }
	$roots = if ($IsWindows) { @('C:\Program Files\PostgreSQL') } elseif ($IsMacOS) { @('/opt/homebrew/opt', '/usr/local/opt') } else { @('/usr/lib/postgresql') }
	foreach ($root in $roots) {
		if (-not (Test-Path $root)) { continue }
		$candidate = Get-ChildItem $root -Directory | Sort-Object Name -Descending |
			ForEach-Object { Join-Path $_.FullName 'bin' } | Where-Object { Test-Path (Join-Path $_ (& $exe 'pg_ctl')) } | Select-Object -First 1
		if ($candidate) { return $candidate }
	}
	return $null
}

$bin = Find-Bin
if (-not $bin -and $IsMacOS) {
	brew install postgresql@16
	if ($LASTEXITCODE -ne 0) { throw 'brew install postgresql@16 failed' }
	$bin = Find-Bin
}
if (-not $bin) { throw 'No PostgreSQL server binaries (initdb, pg_ctl) found on this runner.' }

$data = Join-Path ($env:RUNNER_TEMP ?? [IO.Path]::GetTempPath()) 'bassia-ci-pg'
$log = "$data.log"
if (Test-Path $data) { Remove-Item $data -Recurse -Force }
& (Join-Path $bin (& $exe 'initdb')) -D $data -U postgres -A trust -E UTF8 --locale=C | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'initdb failed' }
& (Join-Path $bin (& $exe 'pg_ctl')) -D $data -l $log -o "-p $Port -c max_connections=500 -c listen_addresses=127.0.0.1 -c unix_socket_directories=" -w -t 60 start
if ($LASTEXITCODE -ne 0) { Get-Content $log -ErrorAction SilentlyContinue; throw 'pg_ctl start failed' }

$connection = "Host=127.0.0.1;Port=$Port;Username=postgres;Database=postgres"
Write-Host "PostgreSQL is up: $connection"
if ($env:GITHUB_ENV) {
	"BASSIA_POSTGRES=$connection" | Out-File -Append -Encoding utf8 $env:GITHUB_ENV
	"BASSIA_TEST_POSTGRES=$connection" | Out-File -Append -Encoding utf8 $env:GITHUB_ENV
}
