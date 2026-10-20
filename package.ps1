param([string]$PlaynitePath = "$env:LOCALAPPDATA/Playnite",[switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
if(!$SkipBuild){& (Join-Path $projectRoot 'build.ps1') -PlaynitePath $PlaynitePath}
$stagePath = Join-Path $projectRoot ('build/package-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$releasePath = Join-Path $projectRoot 'release'
New-Item -ItemType Directory -Force $stagePath,$releasePath | Out-Null
Copy-Item (Join-Path $projectRoot 'src/SetupApply.ps1'),(Join-Path $projectRoot 'build/PlayniteLink.dll'),(Join-Path $projectRoot 'extension.yaml'),(Join-Path $projectRoot 'README.md'),(Join-Path $projectRoot 'LICENSE'),(Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') $stagePath -Force
$reportPath=Join-Path $stagePath 'docs'
New-Item -ItemType Directory -Force $reportPath | Out-Null
Copy-Item (Join-Path $projectRoot 'docs/ACCEPTANCE.md'),(Join-Path $projectRoot 'docs/evidence/acceptance-tally.json') $reportPath -Force
if(Test-Path (Join-Path $projectRoot 'docs/ACCEPTANCE-0.4.md')){Copy-Item (Join-Path $projectRoot 'docs/ACCEPTANCE-0.4.md') $reportPath -Force}
if(Test-Path (Join-Path $projectRoot 'docs/ACCEPTANCE-0.5.md')){Copy-Item (Join-Path $projectRoot 'docs/ACCEPTANCE-0.5.md') $reportPath -Force}
& (Join-Path $PlaynitePath 'Toolbox.exe') pack $stagePath $releasePath
if ($LASTEXITCODE -ne 0) { throw 'Playnite Toolbox packaging failed' }
Get-ChildItem $releasePath -Filter '*.pext' | Get-FileHash -Algorithm SHA256

