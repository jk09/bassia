# Shared by the hand-check scripts: locates the Debug build of bassia.exe and runs it, parsing its JSON result.

$PSNativeCommandUseErrorActionPreference = $false

$exe = Join-Path $PSScriptRoot '..\Bassia\bin\Debug\net10.0\Bassia.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
	throw "Bassia.exe not found at '$exe'. Build it first: dotnet build $(Join-Path $PSScriptRoot '..\Bassia\Bassia.csproj')"
}

$script:BassiaExe = (Resolve-Path -LiteralPath $exe).Path

# Runs bassia with the given arguments. stderr (progress lines, or the JSON error) streams to the console; stdout is
# the JSON result, returned as an object. A non-zero exit code throws.
#
# `bassia agent` lets the agent command inherit stdout, so its output precedes the JSON. The JSON is the trailing
# block that starts with a line holding just "{"; anything before it is echoed as agent output.
function Invoke-Bassia {
	Write-Host "> bassia $($args -join ' ')" -ForegroundColor DarkGray
	$lines = @(& $script:BassiaExe @args)
	if ($LASTEXITCODE -ne 0) {
		throw "bassia $($args[0]) failed (exit code $LASTEXITCODE)."
	}

	for ($start = $lines.Count - 1; $start -ge 0; $start--) {
		if ($lines[$start] -ne '{') { continue }
		try {
			$result = ($lines[$start..($lines.Count - 1)] -join "`n") | ConvertFrom-Json
		}
		catch { continue }

		if ($start -gt 0) { $lines[0..($start - 1)] | ForEach-Object { Write-Host $_ } }
		return $result
	}

	throw "bassia $($args[0]) printed no JSON result:`n$($lines -join "`n")"
}
