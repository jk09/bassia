<#
.SYNOPSIS
Load-tests a Bassia git gateway (bassia-ce722c6c) the way agents use it: each simulated agent clones a component
history-only, reads a file by id, commits and pushes a result, again and again; the latencies of each operation are
reported as p50/p99 with the error count, to compare with the targets in feat/*/bassia-ce722c6c/SPEC.md.

.DESCRIPTION
Runs against a staging deployment (bassia-b9581afe), never production: pushes go to branches 'load/<agent>/<n>', which
the script does not delete. Needs git and PowerShell 7. The ref update and atomic landing targets are measured by the
server itself; this script measures what a client sees.

.EXAMPLE
./scripts/Invoke-CloudLoadTest.ps1 -Gateway https://bassia.staging.example.com/git -Components lib,app -Agents 500 -Minutes 30
#>
[CmdletBinding()]
param(
	# The gateway's URL prefix: <Gateway>/<component> is a repository.
	[Parameter(Mandatory)] [string] $Gateway,
	# The components agents pick from, round robin.
	[Parameter(Mandatory)] [string[]] $Components,
	[int] $Agents = 50,
	[int] $Minutes = 10,
	# Seconds an agent waits between two pushes (the target: one push per agent every 5 minutes).
	[int] $PushInterval = 300,
	# A file every component has, read by id after the history-only clone.
	[string] $File = 'README.md',
	[string] $WorkFolder = (Join-Path ([IO.Path]::GetTempPath()) "bassia-load-$([guid]::NewGuid().ToString('N').Substring(0, 8))")
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $WorkFolder -Force | Out-Null
$deadline = (Get-Date).AddMinutes($Minutes)

$samples = 1..$Agents | ForEach-Object -ThrottleLimit $Agents -Parallel {
	$agent = $_
	$component = $using:Components[($agent - 1) % $using:Components.Count]
	$folder = Join-Path $using:WorkFolder "agent-$agent"
	function Measure-Step([string] $Operation, [scriptblock] $Step) {
		$watch = [Diagnostics.Stopwatch]::StartNew()
		$output = & $Step 2>&1
		[pscustomobject]@{ Operation = $Operation; Milliseconds = $watch.Elapsed.TotalMilliseconds; Ok = ($LASTEXITCODE -eq 0); Error = "$output" }
	}

	Measure-Step 'clone (history only)' { git clone --quiet --filter=blob:none --no-checkout "$using:Gateway/$component" $folder }
	Measure-Step 'blob fetch by id' { git -C $folder show "HEAD:$using:File" }
	$git = @('-C', $folder, '-c', "user.name=load-agent-$agent", '-c', "user.email=load-agent-$agent@example.com")
	$round = 0
	while ((Get-Date) -lt $using:deadline) {
		$round++
		git @git checkout --quiet -B "load/$agent/$round" origin/HEAD 2>&1 | Out-Null
		Set-Content -LiteralPath (Join-Path $folder "load-$agent.txt") -Value "round $round"
		git @git add "load-$agent.txt" 2>&1 | Out-Null
		git @git commit --quiet -m "load agent $agent round $round" 2>&1 | Out-Null
		Measure-Step 'push (one commit)' { git @git push --quiet origin "load/$agent/$round" }
		Measure-Step 'fetch' { git @git fetch --quiet origin }
		Start-Sleep -Seconds (Get-Random -Minimum ([int]($using:PushInterval * 0.5)) -Maximum ([int]($using:PushInterval * 1.5)))
	}
}

function Get-Percentile([double[]] $Values, [double] $Percent) {
	$sorted = $Values | Sort-Object
	$sorted[[math]::Min($sorted.Count - 1, [math]::Floor($sorted.Count * $Percent))]
}

$report = $samples | Group-Object Operation | ForEach-Object {
	$ok = @($_.Group | Where-Object Ok)
	[pscustomobject]@{
		Operation = $_.Name
		Count = $_.Count
		Errors = $_.Count - $ok.Count
		'p50 ms' = if ($ok) { [math]::Round((Get-Percentile $ok.Milliseconds 0.50)) } else { $null }
		'p99 ms' = if ($ok) { [math]::Round((Get-Percentile $ok.Milliseconds 0.99)) } else { $null }
	}
}

$report | Format-Table -AutoSize
$samples | Where-Object { -not $_.Ok } | Select-Object -First 5 | ForEach-Object { Write-Warning "$($_.Operation): $($_.Error)" }
Remove-Item -LiteralPath $WorkFolder -Recurse -Force -ErrorAction SilentlyContinue
