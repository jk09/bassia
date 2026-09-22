# Shared by the hand-check scripts: locates the Debug build of bassia.exe and runs it, parsing its TOML result.

$PSNativeCommandUseErrorActionPreference = $false

$exe = Join-Path $PSScriptRoot '..\Bassia\bin\Debug\net10.0\Bassia.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
	throw "Bassia.exe not found at '$exe'. Build it first: dotnet build $(Join-Path $PSScriptRoot '..\Bassia\Bassia.csproj')"
}

$script:BassiaExe = (Resolve-Path -LiteralPath $exe).Path

# The comment line every bassia result opens with (TomlResult.Marker).
$script:BassiaResultMarker = '# bassia result'

# A TOML basic string: its quoted body, with backslash escapes.
$script:BassiaTomlString = '"((?:[^"\\]|\\.)*)"'

# Turns a TOML basic string's body back into its text.
function Expand-BassiaTomlString {
	param([string] $Body)

	[regex]::Replace($Body, '\\(u[0-9A-Fa-f]{4}|U[0-9A-Fa-f]{8}|.)', {
		param($match)

		$escape = $match.Groups[1].Value
		switch -CaseSensitive ($escape) {
			'b' { "`b" }
			'f' { "`f" }
			'n' { "`n" }
			'r' { "`r" }
			't' { "`t" }
			'"' { '"' }
			'\' { '\' }
			default {
				if ($escape[0] -ceq 'u' -or $escape[0] -ceq 'U') {
					[char]::ConvertFromUtf32([convert]::ToInt32($escape.Substring(1), 16))
				}
				else { $escape }
			}
		}
	})
}

function ConvertFrom-BassiaTomlValue {
	param([string] $Value)

	if ($Value -ceq 'true') { return $true }
	if ($Value -ceq 'false') { return $false }
	if ($Value -match '^-?[0-9]+$') { return [long] $Value }
	if ($Value.StartsWith('[')) {
		return @([regex]::Matches($Value, $script:BassiaTomlString) | ForEach-Object { Expand-BassiaTomlString $_.Groups[1].Value })
	}

	if ($Value -match "^$($script:BassiaTomlString)`$") { return Expand-BassiaTomlString $Matches[1] }
	throw "Unparsable value in bassia result: $Value"
}

# PowerShell has no TOML reader, and a result uses only a small, machine-generated subset of TOML: top-level
# key/value pairs (basic string, bool, integer, inline array of basic strings) followed by [[section]] blocks,
# which become an array of objects under that name. Anything else is a bug in the CLI rather than input to handle.
function ConvertFrom-BassiaToml {
	param([string[]] $Lines)

	$root = [ordered]@{}
	$sections = [ordered]@{}
	$target = $root

	foreach ($line in $Lines) {
		$text = $line.Trim()
		if (-not $text -or $text.StartsWith('#')) { continue }

		if ($text -match '^\[\[([A-Za-z0-9_]+)\]\]$') {
			$name = $Matches[1]
			if (-not $sections.Contains($name)) { $sections[$name] = [Collections.Generic.List[object]]::new() }
			$target = [ordered]@{}
			$sections[$name].Add($target)
			continue
		}

		if ($text -notmatch '^([A-Za-z0-9_]+)\s*=\s*(.+)$') { throw "Unparsable line in bassia result: $line" }
		$target[$Matches[1]] = ConvertFrom-BassiaTomlValue $Matches[2]
	}

	foreach ($name in @($sections.Keys)) {
		$root[$name] = @($sections[$name] | ForEach-Object { [pscustomobject] $_ })
	}

	[pscustomobject] $root
}

# Runs bassia with the given arguments. stderr (progress lines, or the TOML error) streams to the console; stdout is
# the TOML result, returned as an object. A non-zero exit code throws.
#
# `bassia agent` lets the agent command inherit stdout, so its output precedes the result. The result is the block
# starting at the last marker line; anything before it is echoed as agent output.
function Invoke-Bassia {
	Write-Host "> bassia $($args -join ' ')" -ForegroundColor DarkGray
	$lines = @(& $script:BassiaExe @args)
	if ($LASTEXITCODE -ne 0) {
		throw "bassia $($args[0]) failed (exit code $LASTEXITCODE)."
	}

	for ($start = $lines.Count - 1; $start -ge 0; $start--) {
		if ($lines[$start].Trim() -ne $script:BassiaResultMarker) { continue }
		if ($start -gt 0) { $lines[0..($start - 1)] | ForEach-Object { Write-Host $_ } }
		return ConvertFrom-BassiaToml $lines[$start..($lines.Count - 1)]
	}

	throw "bassia $($args[0]) printed no TOML result:`n$($lines -join "`n")"
}
