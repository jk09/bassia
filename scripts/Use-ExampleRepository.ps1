<#
.SYNOPSIS
	CI: chooses the git repository AzuriteIntegrationTests imports (bassia-07d97c3d).

.DESCRIPTION
	The test imports BASSIA_TEST_GIT_URL, by default https://github.com/jk09/example.git, which is private: GITHUB_TOKEN
	cannot read it. With a token that can (the repository secret BASSIA_EXAMPLE_REPO_TOKEN, passed in the environment
	variable of the same name), git authenticates to that repository only and the test imports it from GitHub. Without
	one, a local stand-in with the same files is created and BASSIA_TEST_GIT_URL points the test at it, with a warning
	in the run's summary.
#>
[CmdletBinding()]
param(
	[string] $Url = 'https://github.com/jk09/example'
)

$ErrorActionPreference = 'Stop'

if ($env:BASSIA_EXAMPLE_REPO_TOKEN) {
	$basic = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("x-access-token:$($env:BASSIA_EXAMPLE_REPO_TOKEN)"))
	git config --global "http.$Url.extraheader" "AUTHORIZATION: basic $basic"
	git ls-remote --heads "$Url.git" | Out-Null
	if ($LASTEXITCODE -ne 0) { throw "BASSIA_EXAMPLE_REPO_TOKEN cannot read $Url." }
	Write-Output "Importing $Url from GitHub."
	return
}

$standIn = Join-Path ([IO.Path]::GetTempPath()) 'bassia-example-standin'
if (Test-Path $standIn) { Remove-Item -Recurse -Force $standIn }
git init -q -b main $standIn
Set-Content -NoNewline -Path (Join-Path $standIn 'README.md') -Value "# example`nAn example repository for testing cloning`n"
Set-Content -NoNewline -Path (Join-Path $standIn '.gitignore') -Value "# Object files`n*.o`n*.obj`n`n# Executables`n*.exe`n*.out`n"
Set-Content -NoNewline -Path (Join-Path $standIn 'LICENSE') -Value "Stand-in for the license of jk09/example.`n"
git -C $standIn add -A
git -C $standIn -c user.name='Bassia CI' -c user.email=ci@bassia.invalid commit -q -m 'Initial commit'
if ($LASTEXITCODE -ne 0) { throw 'Could not create the stand-in repository.' }

"BASSIA_TEST_GIT_URL=$standIn" | Out-File -Append -Encoding utf8 $env:GITHUB_ENV
Write-Output "::warning title=Example repository::BASSIA_EXAMPLE_REPO_TOKEN is not set, so the Azurite integration test imports a local stand-in instead of $Url (private)."
