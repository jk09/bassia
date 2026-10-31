<#
.SYNOPSIS
	Emulates Bassia's Azure backend locally - Azurite for Blob Storage, PostgreSQL for Azure Database for PostgreSQL -
	and tests Bassia against it, git export included.

.DESCRIPTION
	Starts the two services in Docker containers or as local processes, prints their connection strings, runs the test
	suite against them, and runs an end-to-end smoke test: a bassia-server on Azurite + PostgreSQL, two clones, git
	import with history, a landing, git export (twice, incrementally), and checks of what ended up in each service.

	Modes:
	  docker   containers bassia-azurite (mcr.microsoft.com/azure-storage/azurite:3.37.0) and bassia-postgres
	           (postgres:16), with named volumes; needs a running Docker daemon.
	  native   Azurite 3.37.0 from npm (installed once into the state folder) under node, and PostgreSQL from its
	           installed server binaries (initdb, pg_ctl) with a data folder of its own; needs Node.js and PostgreSQL.
	  auto     docker when the daemon answers, else native.

	State (the mode, ports, process ids, data folders) lives in <root>/local-cloud, where <root> is the install root of
	bassia-versions.ps1: $env:BASSIA_HOME, else %LOCALAPPDATA%\Bassia on Windows, ${XDG_DATA_HOME:-~/.local/share}/bassia
	elsewhere. As root on Linux, PostgreSQL's data folder is under /var/tmp/bassia-local-cloud instead and the server
	runs as the 'postgres' user, since PostgreSQL refuses to run as root.

.EXAMPLE
	./scripts/bassia-local-cloud.ps1 check
	./scripts/bassia-local-cloud.ps1 up                    # -Mode auto|docker|native, -BlobPort 10000, -PostgresPort 55432
	./scripts/bassia-local-cloud.ps1 env -Shell sh         # export lines; also pwsh, cmd
	./scripts/bassia-local-cloud.ps1 test                  # dotnet test against the emulators; -Filter cloud for those tests only
	./scripts/bassia-local-cloud.ps1 smoke                 # -Bassia <exe> -Server <exe> to test installed builds; -Keep
	./scripts/bassia-local-cloud.ps1 down -Purge
#>
[CmdletBinding()]
param(
	[Parameter(Mandatory, Position = 0)]
	[ValidateSet('check', 'up', 'status', 'env', 'test', 'smoke', 'down')]
	[string] $Command,

	# up: how to run the services.
	[ValidateSet('auto', 'docker', 'native')]
	[string] $Mode = 'auto',

	# up: the Azurite blob port and the PostgreSQL port on 127.0.0.1.
	[int] $BlobPort = 10000,
	[int] $PostgresPort = 55432,

	# env: the shell syntax; default pwsh on Windows, sh elsewhere.
	[ValidateSet('pwsh', 'sh', 'cmd')]
	[string] $Shell,

	# test: a dotnet test --filter expression, or 'cloud' for the Azure and PostgreSQL tests only.
	[string] $Filter,

	# smoke: run these executables instead of building bassia and bassia-server from this repository (e.g. an
	# installed version: <root>/current/bassia).
	[string] $Bassia,
	[string] $Server,

	# smoke: use the existing Debug build without building first.
	[switch] $NoBuild,

	# smoke: keep the temporary folder, the database and the server's log for inspection.
	[switch] $Keep,

	# down: also delete the services' data (containers and volumes, or data folders).
	[switch] $Purge
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version 3

$script:OnWindows = $IsWindows -or $env:OS -eq 'Windows_NT'
$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$script:AzuriteVersion = '3.37.0'
$script:AzuriteImage = "mcr.microsoft.com/azure-storage/azurite:$($script:AzuriteVersion)"
$script:PostgresImage = 'postgres:16'
$script:AzuriteContainer = 'bassia-azurite'
$script:PostgresContainer = 'bassia-postgres'
# Azurite's well-known development account (public, documented by Microsoft; not a secret).
$script:DevAccount = 'devstoreaccount1'
$script:DevKey = 'Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw=='

# ----- state -----

function Get-InstallRoot {
	if ($env:BASSIA_HOME) { return $env:BASSIA_HOME }
	if ($script:OnWindows) { return Join-Path $env:LOCALAPPDATA 'Bassia' }
	$data = if ($env:XDG_DATA_HOME) { $env:XDG_DATA_HOME } else { Join-Path $HOME '.local/share' }
	Join-Path $data 'bassia'
}

$script:StateDir = Join-Path (Get-InstallRoot) 'local-cloud'
$script:StateFile = Join-Path $script:StateDir 'state.json'

function Test-Root { -not $script:OnWindows -and (& id -u) -eq '0' }

function Read-State {
	if (-not (Test-Path -LiteralPath $script:StateFile)) { return $null }
	Get-Content -LiteralPath $script:StateFile -Raw | ConvertFrom-Json
}

function Write-State($state) {
	$null = New-Item -ItemType Directory -Force -Path $script:StateDir
	$state | ConvertTo-Json | Set-Content -LiteralPath $script:StateFile -Encoding utf8
}

function Get-Connections($state) {
	[pscustomobject]@{
		Postgres = "Host=127.0.0.1;Port=$($state.PostgresPort);Username=postgres;Password=postgres;Database=postgres"
		AzureBlob = "DefaultEndpointsProtocol=http;AccountName=$($script:DevAccount);AccountKey=$($script:DevKey);BlobEndpoint=http://127.0.0.1:$($state.BlobPort)/$($script:DevAccount);"
	}
}

# ----- probing -----

function Find-Command([string] $name) {
	$found = Get-Command $name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
	if ($found) { $found.Source } else { $null }
}

function Test-DockerDaemon {
	if (-not (Find-Command docker)) { return $false }
	$null = & docker info --format '{{.ServerVersion}}' 2>$null
	$LASTEXITCODE -eq 0
}

# The folder holding initdb, pg_ctl, postgres and psql: on PATH, or in the usual install locations (newest first).
function Find-PostgresBin {
	$onPath = Find-Command 'pg_ctl'
	if ($onPath -and (Test-Path (Join-Path (Split-Path $onPath) ($script:OnWindows ? 'initdb.exe' : 'initdb')))) { return Split-Path $onPath }
	$patterns = if ($script:OnWindows) { @("$env:ProgramFiles\PostgreSQL\*\bin") }
		else { @('/usr/lib/postgresql/*/bin', '/usr/pgsql-*/bin', '/opt/homebrew/opt/postgresql@*/bin', '/usr/local/opt/postgresql@*/bin', '/Applications/Postgres.app/Contents/Versions/*/bin') }
	foreach ($pattern in $patterns) {
		$found = Get-Item -Path $pattern -ErrorAction SilentlyContinue |
			Where-Object { Test-Path (Join-Path $_.FullName ($script:OnWindows ? 'pg_ctl.exe' : 'pg_ctl')) } |
			Sort-Object { [int] ([regex]::Match($_.FullName, '(\d+)').Groups[1].Value) } -Descending |
			Select-Object -First 1
		if ($found) { return $found.FullName }
	}

	$null
}

function Test-Port([int] $port) {
	$client = [Net.Sockets.TcpClient]::new()
	try {
		$task = $client.ConnectAsync('127.0.0.1', $port)
		$task.Wait(500) -and $client.Connected
	}
	catch { $false }
	finally { $client.Dispose() }
}

function Test-Azurite([int] $port) {
	try {
		$null = Invoke-WebRequest -Uri "http://127.0.0.1:$port/$($script:DevAccount)?comp=list" -TimeoutSec 3 -SkipHttpErrorCheck
		$true
	}
	catch { $false }
}

# Runs SQL with psql and returns its unaligned output lines.
function Invoke-Psql($state, [string] $sql, [string] $database = 'postgres') {
	if ($state.Mode -eq 'docker') {
		$output = & docker exec $script:PostgresContainer psql -U postgres -d $database -v ON_ERROR_STOP=1 -tAc $sql 2>&1
	}
	else {
		$psql = Join-Path $state.PostgresBin ($script:OnWindows ? 'psql.exe' : 'psql')
		$env:PGPASSWORD = 'postgres'
		$output = & $psql -h 127.0.0.1 -p $state.PostgresPort -U postgres -d $database -v ON_ERROR_STOP=1 -tAc $sql 2>&1
	}

	if ($LASTEXITCODE -ne 0) { throw "psql failed: $($output -join "`n")" }
	@($output | ForEach-Object { "$_" })
}

function Test-Postgres($state) {
	if (-not (Test-Port $state.PostgresPort)) { return $false }
	try { (Invoke-Psql $state 'select 1') -contains '1' } catch { $false }
}

function Wait-Until([scriptblock] $condition, [string] $what, [int] $seconds = 60) {
	$deadline = [DateTime]::UtcNow.AddSeconds($seconds)
	while ([DateTime]::UtcNow -lt $deadline) {
		if (& $condition) { return }
		Start-Sleep -Milliseconds 500
	}

	throw "$what did not come up within $seconds s."
}

function Get-ServiceStatus($state) {
	[pscustomobject]@{
		Azurite = $null -ne $state -and (Test-Azurite $state.BlobPort)
		Postgres = $null -ne $state -and (Test-Postgres $state)
	}
}

function Assert-Up {
	$state = Read-State
	$status = Get-ServiceStatus $state
	if (-not ($status.Azurite -and $status.Postgres)) {
		$down = @(if (-not $status.Azurite) { 'Azurite' }; if (-not $status.Postgres) { 'PostgreSQL' }) -join ' and '
		throw "$down not running. Start the emulators first: ./scripts/bassia-local-cloud.ps1 up"
	}

	$state
}

# ----- unix helpers for running PostgreSQL as the postgres user when we are root -----

function Invoke-AsPostgres([string] $exe, [string[]] $arguments) {
	if (Test-Root) {
		$runuser = Find-Command 'runuser'
		if ($runuser) { $output = & $runuser -u postgres -- $exe @arguments 2>&1 }
		else {
			$line = (@($exe) + $arguments | ForEach-Object { "'" + ($_ -replace "'", "'\''") + "'" }) -join ' '
			$output = & su postgres -s /bin/sh -c $line 2>&1
		}
	}
	else {
		# Output into files, waiting for this process only: 'pg_ctl start' leaves the postmaster running with inherited
		# handles (on Windows), so a pipe (2>&1) would never reach its end and the call would hang.
		$out = [IO.Path]::GetTempFileName()
		$err = [IO.Path]::GetTempFileName()
		try {
			$process = Start-Process -FilePath $exe -ArgumentList ($arguments | ForEach-Object { "`"$_`"" }) -RedirectStandardOutput $out `
				-RedirectStandardError $err -NoNewWindow -PassThru
			# Not WaitForExit(): that also waits for the postmaster, which keeps the redirected files open.
			while (-not $process.HasExited) { Start-Sleep -Milliseconds 100 }
			return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = ((Get-Content -Raw $out) + (Get-Content -Raw $err)).Trim() }
		}
		finally { Remove-Item -LiteralPath $out, $err -Force -ErrorAction SilentlyContinue }
	}
	[pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output | ForEach-Object { "$_" }) -join "`n" }
}

# ----- commands -----

function Invoke-Check {
	$docker = Find-Command docker
	$daemon = Test-DockerDaemon
	$node = Find-Command node
	$npm = Find-Command npm
	$pgBin = Find-PostgresBin
	$git = Find-Command git
	$dotnet = Find-Command dotnet
	$native = $node -and $npm -and $pgBin
	$mode = if ($daemon) { 'docker' } elseif ($native) { 'native' } else { $null }

	$rows = @(
		[pscustomobject]@{ Requirement = 'docker daemon'; Found = $daemon ? "yes ($docker)" : ($docker ? 'client only, the daemon does not answer' : 'no'); For = 'up -Mode docker' }
		[pscustomobject]@{ Requirement = 'node + npm'; Found = ($node -and $npm) ? "yes ($(& node --version))" : 'no'; For = 'up -Mode native (Azurite)' }
		[pscustomobject]@{ Requirement = 'PostgreSQL server'; Found = $pgBin ? "yes ($pgBin)" : 'no'; For = 'up -Mode native (PostgreSQL)' }
		[pscustomobject]@{ Requirement = 'git'; Found = $git ? "yes ($git)" : 'no'; For = 'smoke (import, export)' }
		[pscustomobject]@{ Requirement = '.NET SDK'; Found = $dotnet ? "yes ($(& dotnet --version))" : 'no'; For = 'test, smoke' }
	)
	foreach ($row in $rows) { Write-Host ('{0,-18} {1,-60} {2}' -f $row.Requirement, $row.Found, $row.For) }
	if ($mode) { Write-Host "up -Mode auto would use: $mode" }
	if (-not $git) { Write-Host 'smoke needs git on PATH.' }
	if (-not $dotnet) { Write-Host 'test and smoke need the .NET SDK (see /bassia-install check).' }
	if (-not $mode) {
		Write-Host 'Neither mode is possible. Either start Docker (Docker Desktop on Windows/macOS, the docker service on Linux), or install'
		Write-Host 'Node.js (https://nodejs.org) and PostgreSQL 16 (Windows: winget install PostgreSQL.PostgreSQL.16; macOS: brew install postgresql@16;'
		Write-Host 'Debian/Ubuntu: sudo apt install postgresql).'
		exit 1
	}
}

function Start-Docker([int] $blobPort, [int] $pgPort) {
	foreach ($spec in @(
			@{ Name = $script:AzuriteContainer; Run = @('-p', "127.0.0.1:${blobPort}:10000", '-v', 'bassia-azurite-data:/data', $script:AzuriteImage,
					'azurite-blob', '--blobHost', '0.0.0.0', '--blobPort', '10000', '--location', '/data', '--skipApiVersionCheck', '--loose') }
			@{ Name = $script:PostgresContainer; Run = @('-p', "127.0.0.1:${pgPort}:5432", '-e', 'POSTGRES_PASSWORD=postgres',
					'-v', 'bassia-postgres-data:/var/lib/postgresql/data', $script:PostgresImage) })) {
		$existing = & docker ps -a --filter "name=^$($spec.Name)$" --format '{{.State}}'
		if ($existing -eq 'running') { Write-Host "$($spec.Name): already running."; continue }
		if ($existing) {
			Write-Host "$($spec.Name): starting the existing container."
			$null = & docker start $spec.Name
		}
		else {
			Write-Host "$($spec.Name): creating the container."
			$null = & docker run -d --name $spec.Name @($spec.Run)
		}

		if ($LASTEXITCODE -ne 0) { throw "docker could not start $($spec.Name)." }
	}
}

function Start-NativeAzurite($state) {
	if (Test-Azurite $state.BlobPort) { Write-Host 'Azurite: already running.'; return }
	$package = Join-Path $script:StateDir "azurite-$($script:AzuriteVersion)"
	$main = Join-Path $package 'node_modules/azurite/dist/src/blob/main.js'
	if (-not (Test-Path -LiteralPath $main)) {
		Write-Host "Azurite: installing azurite@$($script:AzuriteVersion) into $package."
		$npm = Find-Command npm
		$output = & $npm install --prefix $package "azurite@$($script:AzuriteVersion)" --no-audit --no-fund 2>&1
		if ($LASTEXITCODE -ne 0) { throw "npm install azurite failed:`n$($output -join "`n")" }
	}

	Stop-NativeAzurite
	$data = Join-Path $script:StateDir 'azurite-data'
	$null = New-Item -ItemType Directory -Force -Path $data
	$log = Join-Path $script:StateDir 'azurite.log'
	$arguments = @($main, '--blobHost', '127.0.0.1', '--blobPort', "$($state.BlobPort)", '--location', $data, '--skipApiVersionCheck', '--loose')
	$node = Find-Command node
	if ($script:OnWindows) {
		$process = Start-Process -FilePath $node -ArgumentList ($arguments | ForEach-Object { "`"$_`"" }) -RedirectStandardOutput $log -RedirectStandardError "$log.err" -WindowStyle Hidden -PassThru
	}
	else {
		# Detached through sh: its own session (setsid), no file descriptor of this shell inherited, so Azurite outlives
		# this script, its terminal and whatever runs it.
		$quote = { param($text) "'" + ($text -replace "'", "'\''") + "'" }
		$line = ((@($node) + $arguments) | ForEach-Object { & $quote $_ }) -join ' '
		$detach = (Find-Command setsid) ? 'setsid ' : 'nohup '
		& /bin/sh -c "$detach$line > $(& $quote $log) 2> $(& $quote "$log.err") < /dev/null &"
		$process = $null
		Wait-Until { $script:found = Get-Process -Name node -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($main) } | Select-Object -First 1; $null -ne $script:found } 'The Azurite process' 10
		$process = $script:found
	}

	$state.AzuritePid = $process.Id
	Write-Host "Azurite: started (pid $($process.Id), data in $data)."
}

function Start-NativePostgres($state) {
	if (Test-Postgres $state) { Write-Host 'PostgreSQL: already running.'; return }
	$bin = $state.PostgresBin
	$exe = { param($name) Join-Path $bin ($script:OnWindows ? "$name.exe" : $name) }
	$data = $state.PostgresData
	if (-not (Test-Path -LiteralPath (Join-Path $data 'PG_VERSION'))) {
		Write-Host "PostgreSQL: initializing $data."
		$null = New-Item -ItemType Directory -Force -Path $data
		if (Test-Root) {
			& chown -R postgres (Split-Path $data) | Out-Null
			& chmod 700 $data | Out-Null
		}

		$result = Invoke-AsPostgres (& $exe 'initdb') @('-D', $data, '-U', 'postgres', '-A', 'trust', '-E', 'UTF8', '--locale=C')
		if ($result.ExitCode -ne 0) { throw "initdb failed:`n$($result.Output)" }
	}

	$options = "-p $($state.PostgresPort) -c listen_addresses=127.0.0.1"
	if (-not $script:OnWindows) { $options += " -k $data" }
	$log = Join-Path $data 'server.log'
	$result = Invoke-AsPostgres (& $exe 'pg_ctl') @('-D', $data, '-l', $log, '-o', $options, '-w', '-t', '60', 'start')
	if ($result.ExitCode -ne 0) { throw "pg_ctl start failed:`n$($result.Output)`nSee $log." }
	Write-Host "PostgreSQL: started on port $($state.PostgresPort) (data in $data)."
}

# Stops any Azurite started from the state folder's package (also one whose pid was lost).
function Stop-NativeAzurite {
	$main = Join-Path $script:StateDir "azurite-$($script:AzuriteVersion)"
	Get-Process -Name node -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($main) } |
		ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
}

function Invoke-Up {
	$state = Read-State
	$status = Get-ServiceStatus $state
	if ($state -and $status.Azurite -and $status.Postgres) {
		Write-Host "Already up ($($state.Mode)): Azurite on $($state.BlobPort), PostgreSQL on $($state.PostgresPort)."
		Show-Env $state
		return
	}

	$mode = $Mode
	if ($state -and $Mode -eq 'auto') { $mode = $state.Mode }
	if ($mode -eq 'auto') { $mode = (Test-DockerDaemon) ? 'docker' : 'native' }
	if ($state -and $state.Mode -ne $mode) {
		throw "The emulators were set up in $($state.Mode) mode. Run 'down' (with -Purge to drop their data) before switching to $mode."
	}

	$blobPort = $state ? [int] $state.BlobPort : $BlobPort
	$pgPort = $state ? [int] $state.PostgresPort : $PostgresPort
	if ($state -and ($PSBoundParameters.ContainsKey('BlobPort') -or $PSBoundParameters.ContainsKey('PostgresPort')) -and ($blobPort -ne $BlobPort -or $pgPort -ne $PostgresPort)) {
		throw "The emulators were set up on ports $blobPort and $pgPort. Run 'down' first to change ports."
	}

	foreach ($port in @(@{ Port = $blobPort; Up = $status.Azurite; Name = 'Azurite' }, @{ Port = $pgPort; Up = $status.Postgres; Name = 'PostgreSQL' })) {
		if (-not $port.Up -and (Test-Port $port.Port)) {
			throw "Port $($port.Port) for $($port.Name) is taken by another program. Pick another with -$(($port.Name -eq 'Azurite') ? 'BlobPort' : 'PostgresPort')."
		}
	}

	$new = [ordered]@{ Mode = $mode; BlobPort = $blobPort; PostgresPort = $pgPort; AzuritePid = $null; PostgresBin = $null; PostgresData = $null }
	if ($mode -eq 'docker') {
		if (-not (Test-DockerDaemon)) { throw 'The Docker daemon does not answer. Start Docker, or use -Mode native.' }
		Start-Docker $blobPort $pgPort
	}
	else {
		if (-not (Find-Command node) -or -not (Find-Command npm)) { throw 'Native mode needs Node.js (node and npm on PATH) for Azurite.' }
		$bin = Find-PostgresBin
		if (-not $bin) { throw 'Native mode needs the PostgreSQL server binaries (initdb, pg_ctl). See: ./scripts/bassia-local-cloud.ps1 check' }
		$new.PostgresBin = $bin
		$new.PostgresData = (Test-Root) ? '/var/tmp/bassia-local-cloud/postgres' : (Join-Path $script:StateDir 'postgres')
		if ($state -and $state.AzuritePid) { $new.AzuritePid = $state.AzuritePid }
		$new = [pscustomobject] $new
		Start-NativeAzurite $new
		Start-NativePostgres $new
	}

	$new = [pscustomobject] $new
	Write-State $new
	Wait-Until { Test-Azurite $new.BlobPort } 'Azurite'
	Wait-Until { Test-Postgres $new } 'PostgreSQL' 90
	Write-Host "Up ($mode): Azurite blob service on 127.0.0.1:$($new.BlobPort), PostgreSQL on 127.0.0.1:$($new.PostgresPort)."
	Show-Env $new
}

function Invoke-Status {
	$state = Read-State
	if (-not $state) { Write-Host 'Not set up. Run: ./scripts/bassia-local-cloud.ps1 up'; exit 1 }
	$status = Get-ServiceStatus $state
	Write-Host "mode       $($state.Mode)"
	Write-Host "azurite    $($status.Azurite ? 'up' : 'down') on 127.0.0.1:$($state.BlobPort)"
	Write-Host "postgres   $($status.Postgres ? 'up' : 'down') on 127.0.0.1:$($state.PostgresPort)"
	Write-Host "state      $($script:StateDir)"
	if (-not ($status.Azurite -and $status.Postgres)) { exit 1 }
}

function Show-Env($state) {
	$connections = Get-Connections $state
	$vars = [ordered]@{
		BASSIA_TEST_POSTGRES = $connections.Postgres
		BASSIA_TEST_AZURE_BLOB = $connections.AzureBlob
		BASSIA_SERVER_STORAGE = 'azure'
		BASSIA_SERVER_AZURE_CONNECTION = $connections.AzureBlob
		BASSIA_SERVER_STATE = 'postgres'
		BASSIA_SERVER_POSTGRES = $connections.Postgres
	}
	$syntax = if ($Shell) { $Shell } elseif ($script:OnWindows) { 'pwsh' } else { 'sh' }
	foreach ($name in $vars.Keys) {
		switch ($syntax) {
			'pwsh' { Write-Output "`$env:$name = '$($vars[$name])'" }
			'sh' { Write-Output "export $name='$($vars[$name])'" }
			'cmd' { Write-Output "set `"$name=$($vars[$name])`"" }
		}
	}
}

function Invoke-Test {
	$state = Assert-Up
	$connections = Get-Connections $state
	$env:BASSIA_TEST_POSTGRES = $connections.Postgres
	$env:BASSIA_TEST_AZURE_BLOB = $connections.AzureBlob
	$arguments = @('test', (Join-Path $script:RepoRoot 'Bassia.slnx'))
	$expression = if ($Filter -eq 'cloud') { 'FullyQualifiedName~PostgresStateTests|FullyQualifiedName~ObjectStoreContractTests|FullyQualifiedName~SyncEfficiencyTests' } else { $Filter }
	if ($expression) { $arguments += @('--filter', $expression) }
	Write-Host "> dotnet $($arguments -join ' ')   (with BASSIA_TEST_POSTGRES and BASSIA_TEST_AZURE_BLOB)" -ForegroundColor DarkGray
	& dotnet @arguments
	exit $LASTEXITCODE
}

function Invoke-Down {
	$state = Read-State
	if (-not $state) { Write-Host 'Nothing to stop.'; return }
	if ($state.Mode -eq 'docker') {
		if (Test-DockerDaemon) {
			foreach ($name in @($script:AzuriteContainer, $script:PostgresContainer)) {
				if ($Purge) { $null = & docker rm -f $name 2>&1 } else { $null = & docker stop $name 2>&1 }
			}

			if ($Purge) { $null = & docker volume rm bassia-azurite-data bassia-postgres-data 2>&1 }
		}
		else { Write-Host 'The Docker daemon does not answer; the containers are not running.' }
	}
	else {
		if ($state.AzuritePid) { Stop-Process -Id $state.AzuritePid -Force -ErrorAction SilentlyContinue }
		Stop-NativeAzurite
		if ($state.PostgresData -and (Test-Path -LiteralPath (Join-Path $state.PostgresData 'postmaster.pid'))) {
			$pgctl = Join-Path $state.PostgresBin ($script:OnWindows ? 'pg_ctl.exe' : 'pg_ctl')
			$result = Invoke-AsPostgres $pgctl @('-D', $state.PostgresData, '-m', 'fast', '-w', 'stop')
			if ($result.ExitCode -ne 0) { Write-Warning "pg_ctl stop: $($result.Output)" }
		}

		if ($Purge) {
			if ($state.PostgresData) { Remove-Item -LiteralPath $state.PostgresData -Recurse -Force -ErrorAction SilentlyContinue }
			Remove-Item -LiteralPath (Join-Path $script:StateDir 'azurite-data') -Recurse -Force -ErrorAction SilentlyContinue
		}
	}

	if ($Purge) { Remove-Item -LiteralPath $script:StateFile -Force }
	Write-Host "Stopped ($($state.Mode))$($Purge ? '; data deleted' : '; data kept, up starts it again')."
}

# ----- smoke test -----

$script:Checks = [Collections.Generic.List[object]]::new()

function Assert-Check([string] $name, [bool] $passed, [string] $detail = '') {
	$script:Checks.Add([pscustomobject]@{ Check = $name; Passed = $passed; Detail = $detail })
	Write-Host ("{0}  {1}{2}" -f ($passed ? 'ok  ' : 'FAIL'), $name, ($detail ? " ($detail)" : '')) -ForegroundColor ($passed ? 'Green' : 'Red')
	if (-not $passed) { throw "Smoke check failed: $name $detail" }
}

function Invoke-Git([string] $dir, [string[]] $arguments) {
	$output = & git -C $dir -c core.autocrlf=false -c init.defaultBranch=main -c user.name=Smoke -c user.email=smoke@example.com @arguments 2>&1
	if ($LASTEXITCODE -ne 0) { throw "git $($arguments -join ' ') failed:`n$($output -join "`n")" }
	@($output | ForEach-Object { "$_" })
}

# A small git repository with $count commits, dated so that the two repositories' histories interleave.
function New-GitRepo([string] $dir, [string] $name, [int] $count, [int] $offset) {
	$null = New-Item -ItemType Directory -Force -Path $dir
	Invoke-Git $dir @('init', '-q') | Out-Null
	for ($index = 1; $index -le $count; $index++) {
		Set-Content -LiteralPath (Join-Path $dir 'README.md') -Value "# $name`n`nrevision $index" -NoNewline
		Set-Content -LiteralPath (Join-Path $dir "file$index.txt") -Value "$name $index" -NoNewline
		$env:GIT_AUTHOR_DATE = $env:GIT_COMMITTER_DATE = "2026-01-01T00:0$(2 * $index + $offset):00Z"
		Invoke-Git $dir @('add', '-A') | Out-Null
		Invoke-Git $dir @('commit', '-q', '-m', "$name commit $index") | Out-Null
	}

	Remove-Item Env:GIT_AUTHOR_DATE, Env:GIT_COMMITTER_DATE
}

function Get-FreePort {
	$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
	$listener.Start()
	try { $listener.LocalEndpoint.Port } finally { $listener.Stop() }
}

function Read-Text([string] $path) { (Get-Content -LiteralPath $path -Raw) -replace "`r`n", "`n" }

function Invoke-Smoke {
	$state = Assert-Up
	if (-not (Find-Command git)) { throw 'smoke needs git on PATH (bassia import and export run it).' }
	$connections = Get-Connections $state
	$exeSuffix = $script:OnWindows ? '.exe' : ''

	if (-not $Bassia -or -not $Server) {
		if (-not $NoBuild) {
			Write-Host '> dotnet build (Debug)' -ForegroundColor DarkGray
			foreach ($project in @('Bassia/Bassia.csproj', 'Bassia.Server/Bassia.Server.csproj')) {
				$output = & dotnet build (Join-Path $script:RepoRoot $project) -c Debug -v q -nologo 2>&1
				if ($LASTEXITCODE -ne 0) { throw "dotnet build $project failed:`n$($output -join "`n")" }
			}
		}
	}

	$bassiaExe = $Bassia ? (Get-Command $Bassia).Source : (Join-Path $script:RepoRoot "Bassia/bin/Debug/net10.0/Bassia$exeSuffix")
	$serverExe = $Server ? (Get-Command $Server).Source : (Join-Path $script:RepoRoot "Bassia.Server/bin/Debug/net10.0/bassia-server$exeSuffix")
	foreach ($exe in @($bassiaExe, $serverExe)) { if (-not (Test-Path -LiteralPath $exe)) { throw "Not found: $exe" } }
	$script:BassiaExe = $bassiaExe
	. (Join-Path $PSScriptRoot 'Bassia.common.ps1')

	$stamp = '{0:yyyyMMddHHmmss}{1:x4}' -f [DateTime]::UtcNow, (Get-Random -Maximum 65536)
	$database = "bassia_smoke_$stamp"
	$blobContainer = "smoke-$stamp"
	$temp = Join-Path ([IO.Path]::GetTempPath()) "bassia-smoke-$stamp"
	$null = New-Item -ItemType Directory -Path $temp
	$serverProcess = $null
	$passed = $false
	Write-Host "Smoke test in ${temp}: database $database, blob container $blobContainer."

	try {
		# Two git repositories with interleaved histories.
		New-GitRepo (Join-Path $temp 'git/api') 'api' 3 0
		New-GitRepo (Join-Path $temp 'git/web') 'web' 2 1

		# A server on Azurite + PostgreSQL, in a fresh database and container.
		Invoke-Psql $state "CREATE DATABASE $database" | Out-Null
		$postgres = $connections.Postgres -replace 'Database=postgres', "Database=$database"
		$port = Get-FreePort
		$url = "http://127.0.0.1:$port"
		$serverArgs = @('-storage', 'azure', '-azure-connection', $connections.AzureBlob, '-azure-container', $blobContainer, '-state', 'postgres', '-postgres', $postgres)
		$log = Join-Path $temp 'server.log'
		$serverProcess = Start-Process -FilePath $serverExe -ArgumentList (@('-data', (Join-Path $temp 'server'), '-url', $url, '-semantic-driver', 'off') + $serverArgs | ForEach-Object { "`"$_`"" }) `
			-RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru
		$info = $null
		Wait-Until {
			if ($serverProcess.HasExited) { throw "bassia-server exited ($($serverProcess.ExitCode)):`n$((Get-Content "$log.err" -TotalCount 5) -join "`n")" }
			try { $script:info = Invoke-RestMethod "$url/api/v1/info" -TimeoutSec 2; $true } catch { $false }
		} 'bassia-server'
		$info = $script:info
		Assert-Check 'server runs on Azure Blob Storage' ($info.storage -match 'azure') "storage = $($info.storage)"

		# Clone A imports both repositories with history and lands a change.
		$a = Join-Path $temp 'a'
		Invoke-Bassia clone -from $url -path $a | Out-Null
		$import = Invoke-Bassia -C $a import -git "$(Join-Path $temp 'git/api'),$(Join-Path $temp 'git/web')" -history
		Assert-Check 'import lands every git commit' ($import.seq -eq 5 -and @($import.repository).Count -eq 2) "seq $($import.seq), $(@($import.repository).Count) repositories"

		$workspace = Invoke-Bassia -C $a workspace create smoke -intent 'smoke test change'
		Set-Content -LiteralPath (Join-Path $workspace.path 'api/SMOKE.md') -Value "landed through Azurite and PostgreSQL`n" -NoNewline
		Set-Content -LiteralPath (Join-Path $workspace.path 'web/README.md') -Value "# web`n`nedited by the smoke test`n" -NoNewline
		$land = Invoke-Bassia -C $a workspace land smoke
		Assert-Check 'a workspace lands' ($land.outcome -eq 'landed' -and $land.seq -eq 6) "outcome $($land.outcome), seq $($land.seq)"

		# Clone B sees it.
		$b = Join-Path $temp 'b'
		Invoke-Bassia clone -from $url -path $b | Out-Null
		$view = Invoke-Bassia -C $b workspace create view
		Assert-Check 'a second clone checks out the landed files' (
			(Read-Text (Join-Path $view.path 'api/SMOKE.md')) -eq "landed through Azurite and PostgreSQL`n" -and
			(Read-Text (Join-Path $view.path 'web/README.md')) -eq "# web`n`nedited by the smoke test`n" -and
			(Read-Text (Join-Path $view.path 'api/file3.txt')) -eq 'api 3')

		# Export mainline to git, then once more incrementally after another landing.
		$export = Join-Path $temp 'export'
		$first = Invoke-Bassia -C $b export -git $export
		$log1 = Invoke-Git $export @('log', '--format=%s', 'main')
		Assert-Check 'export writes one git commit per seq' ($first.commits -eq 6 -and $log1.Count -eq 6) "$($first.commits) exported, $($log1.Count) in git log"
		Assert-Check 'exported files match mainline' (
			((Invoke-Git $export @('show', 'main:api/SMOKE.md')) -join "`n") -eq 'landed through Azurite and PostgreSQL' -and
			((Invoke-Git $export @('show', 'main:web/file2.txt')) -join "`n") -eq 'web 2')

		Add-Content -LiteralPath (Join-Path $view.path 'api/SMOKE.md') -Value "and exported incrementally`n" -NoNewline
		$land2 = Invoke-Bassia -C $b workspace land view
		$second = Invoke-Bassia -C $b export -git $export
		$log2 = Invoke-Git $export @('log', '--format=%s', 'main')
		Assert-Check 'a second export adds only the new seq' ($land2.seq -eq 7 -and $second.from_seq -eq 6 -and $second.commits -eq 1 -and $log2.Count -eq 7) `
			"seq $($land2.seq), exported from $($second.from_seq), $($second.commits) commit(s), $($log2.Count) in git log"
		$status = Invoke-Bassia -C $a status
		Assert-Check 'the first clone sees the second landing' ($status.seq -eq 7) "seq $($status.seq)"

		# What ended up in PostgreSQL and Azurite.
		$main = Invoke-Psql $state "SELECT count(*) FROM bassia_heads WHERE name = 'main'" $database
		$indexed = Invoke-Psql $state "SELECT value FROM bassia_meta WHERE key = 'indexed_seq'" $database
		$paths = Invoke-Psql $state 'SELECT count(DISTINCT path) FROM bassia_paths' $database
		Assert-Check 'PostgreSQL holds the heads and the path index' ($main -contains '1' -and $indexed -contains '7' -and [int] $paths[0] -ge 8) `
			"main head $($main -join ''), indexed seq $($indexed -join ''), $($paths -join '') paths"

		Stop-Process -Id $serverProcess.Id -Force
		$serverProcess.WaitForExit()
		$scrub = & $serverExe scrub -data (Join-Path $temp 'server') @serverArgs 2>&1
		$checked = [regex]::Match(($scrub -join "`n"), 'checked (\d+) blobs: (\d+) corrupt, (\d+) unreadable')
		Assert-Check 'Azurite holds every object, intact (scrub)' ($LASTEXITCODE -eq 0 -and $checked.Success -and [int] $checked.Groups[1].Value -gt 0) ($scrub -join ' ')
		$passed = $true
	}
	finally {
		if ($serverProcess -and -not $serverProcess.HasExited) { Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue }
		if ($Keep -or -not $passed) {
			Write-Host "Kept $temp and database $database for inspection (server log: $(Join-Path $temp 'server.log.err'))."
		}
		else {
			try { Invoke-Psql $state "DROP DATABASE IF EXISTS $database" | Out-Null } catch { Write-Warning "Could not drop $database`: $_" }
			Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
		}
	}

	Write-Host "Smoke test passed: $($script:Checks.Count) checks against Azurite (127.0.0.1:$($state.BlobPort)) and PostgreSQL (127.0.0.1:$($state.PostgresPort))."
}

switch ($Command) {
	'check' { Invoke-Check }
	'up' { Invoke-Up }
	'status' { Invoke-Status }
	'env' { $state = Read-State; if (-not $state) { throw 'Not set up. Run: ./scripts/bassia-local-cloud.ps1 up' }; Show-Env $state }
	'test' { Invoke-Test }
	'smoke' { Invoke-Smoke }
	'down' { Invoke-Down }
}
