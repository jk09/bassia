<#
.SYNOPSIS
Creates the bassia-testbed monorepo: one component per git repo under -Dev (except the testbed itself, bassia and
jkmonorepo). Components are cloned from the local folders, each bare repo's HEAD is set to the trunk below and its
origin to the source's real remote. Then every component is unwound ('bassia component unwind'): its submodules become
referenced components, matched to the components already added by their origin URL.
#>
[CmdletBinding()]
param(
	[string] $Dev = 'C:\Users\jozef\Development',
	[string] $Root = (Join-Path $Dev 'bassia-testbed'),
	[string] $Exe = (Join-Path $PSScriptRoot '..\..\..\Bassia\bin\Debug\net10.0\Bassia.exe')
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$dev = $Dev
$root = $Root
$exe = (Resolve-Path -LiteralPath $Exe).Path

# name -> source path (relative to $dev), trunk
$comps = [ordered]@{}
function Add-Comp($name, $path, $trunk) { $script:comps[$name] = @{ path = $path; trunk = $trunk } }

# Submodule targets: the local checkouts, so the unwind below reuses them (matched by origin URL) instead of cloning
# from the network. Added before FsCheck, so UnitGen-FsCheck (which has UnitGen's pinned commit) wins that URL.
Add-Comp 'CodeContracts'   'ContractExpressions\CodeContracts'          'master'
Add-Comp 'color-thief'     'EdgeTabHandler\color-thief'                  'color-thief-main'
Add-Comp 'ILSpy'           'UnitGen\ExternalPrograms\ILSpy'              'master'
Add-Comp 'UnitGen-FsCheck' 'UnitGen\ExternalPrograms\FsCheck'            'master'
Add-Comp 'FluentTerminal'  'FluentTerminal'                              'master'

# Their parents; the unwind references the submodules at their paths.
Add-Comp 'ContractExpressions' 'ContractExpressions' 'main'
Add-Comp 'EdgeTabHandler'      'EdgeTabHandler'      'main'
Add-Comp 'UnitGen'             'UnitGen'             'main'

$plain = @{
  'AiBrowser'='main'; 'angular-custom-route-match'='master'; 'angular-router-sample'='master'; 'angular-router-tour-of-heroes'='master'
  'antimony-browser'='main'; 'ASP.NET-Core-5-and-React-Second-Edition'='master'; 'azure-devtestlab'='master'; 'beg-sql-queries'='master'
  'BlazorSurveys'='master'; 'BookExplorer'='master'; 'browse-ledger'='main'; 'CardMail'='master'; 'ChromeHistoryExtension'='main'
  'claude-skills'='main'; 'Clustering'='master'; 'ClusteringByConsensus'='master'; 'ConwaysLife'='master'; 'CPlusPlus20ForProgrammers'='master'
  'CsConceptsBook'='main'; 'CsConceptsBook_Quarto'='main'; 'CV'='master'; 'definitive_guide_sample_code'='main'
  'EfCoreinAction-SecondEdition'='master'; 'eShopOnContainers'='dev'; 'eShopOnWeb'='main'; 'FluentPython'='master'; 'FsCheck'='master'
  'Full-Stack-React-TypeScript-and-Node'='master'; 'git-scm.com'='master'; 'gitbranch'='master'; 'Hangfire'='master'; 'Haskell'='master'
  'impatient-js-preview-code'='master'; 'Infobip'='master'; 'InfobipCarpool'='master'; 'inherit'='master'; 'learning-blazor'='main'
  'LINQPad Queries'='master'; 'Mida'='master'; 'min'='master'; 'minimal-react'='master'; 'MITCourseFSharp'='master'
  'mongodb-the-definitive-guide-3e'='master'; 'msdn-asp-net-core-70'='master'; 'mslearn-n-tier-architecture'='master'; 'mslearn-react'='main'
  'multi-container-app'='main'; 'MultiInheritance'='master'; 'Ohpen'='master'; 'PracticalApps'='master'; 'pro-angular-5ed'='main'
  'pro-asp.net-core-6'='main'; 'psweb'='master'; 'RazorPagesMovie'='master'
  'React---The-Complete-Guide-includes-Hooks-React-Router-and-Redux-Second-Edition'='main'; 'SimCLR'='master'; 'snappymail'='master'
  'style-snooper'='master'; 'TransactSQL Cookbook-3834'='master'; 'vibrowse'='main'; 'web-hp-functional-jk09'='main'; 'WebbCompare'='gh-pages'
  'welcome-to-docker'='main'; 'Windows-appsample-photo-lab'='master'; 'zguide'='master'
}
foreach ($k in ($plain.Keys | Sort-Object)) { Add-Comp ($k -replace ' ', '-') $k $plain[$k] }

# Repos nested in non-git folders
Add-Comp 'angular-complete-guide-chapter-3' 'angular-complete-guide-chapter-3\project' 'master'
Add-Comp 'angular-complete-guide-chapter-5' 'angular-complete-guide-chapter-5\project' 'master'
Add-Comp 'angular-guide-routing'            'angular-guide-routing\routing-app'        'master'
Add-Comp 'jstdg7'                           'JavaScript\jstdg7'                        'master'
Add-Comp 'JavaScript-my-app'                'JavaScript\my-app'                        'master'
Add-Comp 'react-sandbox-my-app'             'JavaScript\react-sandbox\my-app'          'master'
Add-Comp 'react-take-home-test-2'           'Medisearch\react-take-home-test-2'        'main'
Add-Comp 'ReactActivities-act21'            'ReactActivities\act21'                    'master'
Add-Comp 'spicetify'                        'spicetify\spicetify'                      'main'
Add-Comp 'def-guide-to-sqlite'              'SQLite\def-guide-to-sqlite'               'master'
Add-Comp 'Airflow'                          'Takeda\Airflow'                           'main'
Add-Comp 'Timeline'                         'Timeline\CQRS-and-event\Timeline'         'master'

if (Test-Path $root) { throw "$root exists" }
New-Item -ItemType Directory $root | Out-Null
& $exe init -path $root | Out-Null
if ($LASTEXITCODE) { throw 'init failed' }

$urls = @{}
foreach ($name in $comps.Keys) {
  $c = $comps[$name]
  $src = Join-Path $dev $c.path
  # verify trunk exists in source
  git -C $src rev-parse --verify --quiet "refs/heads/$($c.trunk)" | Out-Null
  if ($LASTEXITCODE) { throw "$name : trunk $($c.trunk) missing in $src" }
  $a = @('-C', $root, 'component', 'add', '-url', $src, '-name', $name)
  $out = & $exe @a 2>&1
  if ($LASTEXITCODE) { Write-Host ($out -join "`n"); throw "add $name failed" }
  $bare = Join-Path $root "$name\.git"
  git --git-dir=$bare symbolic-ref HEAD "refs/heads/$($c.trunk)"
  # real remote of the source, if any (prefer origin)
  $remotes = git -C $src remote
  $r = if ($remotes -contains 'origin') { 'origin' } else { $remotes | Select-Object -First 1 }
  if ($r) {
    $u = git -C $src remote get-url $r
    git --git-dir=$bare remote set-url origin $u
    $urls[$name] = $u
  }
  Write-Host "ok $name ($($c.trunk))$(if($r){' <- '+$u})"
}
$null = $urls

# Unwind every component once all are added, trunks and origins set: the unwind works on the trunk (HEAD) and finds
# the submodule components by origin URL. A failing unwind is reported and the rest go on.
$failed = @()
$unexpected = @()
foreach ($name in $comps.Keys) {
  $out = & $exe -C $root component unwind -name $name 2>&1
  $text = $out -join "`n"
  if ($LASTEXITCODE) { Write-Warning "unwind $name failed:`n$text"; $failed += $name; continue }
  $added = [regex]::Matches($text, '(?ms)^\[\[component\]\]\s*\nname = "([^"]+)"\s*\naction = "added"') | ForEach-Object { $_.Groups[1].Value }
  foreach ($addedName in $added) { $unexpected += "$addedName (from $name)" }
  $message = [regex]::Match($text, '(?m)^message = "(.*)"$').Groups[1].Value
  if ($message -notmatch 'nothing to unwind') { Write-Host "unwound $name - $message" }
}
if ($unexpected) { Write-Warning "unwind cloned components not in the tables (submodule URL matched no local origin): $($unexpected -join ', ')" }
if ($failed) { throw "unwind failed for: $($failed -join ', ')" }
