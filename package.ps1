param([string]$PlaynitePath = "$env:LOCALAPPDATA/Playnite",[switch]$SkipBuild,[string]$BuildDirectory='',[string]$ReleaseDirectory='')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$buildDir=if($BuildDirectory){[IO.Path]::GetFullPath($BuildDirectory)}else{Join-Path $projectRoot 'build'}
if(!$SkipBuild){& (Join-Path $projectRoot 'build.ps1') -PlaynitePath $PlaynitePath -BuildDirectory $buildDir}
$stagePath = Join-Path $projectRoot ('build/package-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$releasePath = if($ReleaseDirectory){[IO.Path]::GetFullPath($ReleaseDirectory)}else{Join-Path $projectRoot 'release'}
New-Item -ItemType Directory -Force $stagePath,$releasePath | Out-Null
Copy-Item (Join-Path $projectRoot 'src/SetupApply.ps1'),(Join-Path $buildDir 'PlayniteLink.dll'),(Join-Path $projectRoot 'extension.yaml'),(Join-Path $projectRoot 'README.md'),(Join-Path $projectRoot 'LICENSE'),(Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') $stagePath -Force
Copy-Item (Join-Path $buildDir 'SaveManager.Core.dll'),(Join-Path $projectRoot 'src/RetireSaveManager.ps1') $stagePath -Force
Copy-Item (Join-Path $buildDir 'Microsoft.Toolkit.Uwp.Notifications.dll') $stagePath -Force
Copy-Item (Join-Path $projectRoot 'vendor/Notifications/LICENSE.md') (Join-Path $stagePath 'Notifications-LICENSE.md')
$sourceStage=Join-Path $buildDir ('source-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force $sourceStage | Out-Null
foreach($directory in @('src','tests','vendor')){Copy-Item (Join-Path $projectRoot $directory) $sourceStage -Recurse}
Copy-Item (Join-Path $projectRoot 'build.ps1'),(Join-Path $projectRoot 'package.ps1'),(Join-Path $projectRoot 'extension.yaml'),(Join-Path $projectRoot 'README.md'),(Join-Path $projectRoot 'LICENSE'),(Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') $sourceStage
$sourceDocs=Join-Path $sourceStage 'docs'
$sourceEvidence=Join-Path $sourceDocs 'evidence'
New-Item -ItemType Directory -Force $sourceDocs,$sourceEvidence | Out-Null
Get-ChildItem (Join-Path $projectRoot 'docs') -Filter '*.md' | Copy-Item -Destination $sourceDocs
foreach($name in @('acceptance-tally.json','saves-0511-local-tally.json','saves-0511-local-extra-tally.json','saves-0511-local-transfer-tally.json','saves-0511-cloud-tally.json','saves-0511-cloud-transfer-tally.json')){Copy-Item (Join-Path $projectRoot ('docs/evidence/'+$name)) $sourceEvidence}
Copy-Item (Join-Path $projectRoot 'docs/evidence/save-manager-060') $sourceEvidence -Recurse
if(Test-Path (Join-Path $projectRoot 'docs/evidence/save-manager-061')){Copy-Item (Join-Path $projectRoot 'docs/evidence/save-manager-061') $sourceEvidence -Recurse}
Compress-Archive -Path (Join-Path $sourceStage '*') -DestinationPath (Join-Path $stagePath 'CorrespondingSource.zip')
$reportPath=Join-Path $stagePath 'docs'
New-Item -ItemType Directory -Force $reportPath | Out-Null
Copy-Item (Join-Path $projectRoot 'docs/ACCEPTANCE-0.6.0.md') $reportPath
if(Test-Path (Join-Path $projectRoot 'docs/ACCEPTANCE-0.6.1.md')){Copy-Item (Join-Path $projectRoot 'docs/ACCEPTANCE-0.6.1.md') $reportPath}
Copy-Item (Join-Path $projectRoot 'docs/evidence/save-manager-060') $reportPath -Recurse
if(Test-Path (Join-Path $projectRoot 'docs/evidence/save-manager-061')){Copy-Item (Join-Path $projectRoot 'docs/evidence/save-manager-061') $reportPath -Recurse}
Copy-Item (Join-Path $projectRoot 'docs/ACCEPTANCE.md'),(Join-Path $projectRoot 'docs/evidence/acceptance-tally.json') $reportPath -Force
foreach($name in @('AUDIT-REPORT-2026-10-21.md','AUDIT-CLOSURE-2026-10-22.md')){if(Test-Path (Join-Path $projectRoot ('docs/'+$name))){Copy-Item (Join-Path $projectRoot ('docs/'+$name)) $reportPath -Force}}
if(Test-Path (Join-Path $projectRoot 'docs/ACCEPTANCE-0.5.12.md')){Copy-Item (Join-Path $projectRoot 'docs/ACCEPTANCE-0.5.12.md') $reportPath -Force}
if(Test-Path (Join-Path $projectRoot 'docs/ACCEPTANCE-0.5.13.md')){Copy-Item (Join-Path $projectRoot 'docs/ACCEPTANCE-0.5.13.md') $reportPath -Force}
if(Test-Path (Join-Path $projectRoot 'docs/ACCEPTANCE-0.4.md')){Copy-Item (Join-Path $projectRoot 'docs/ACCEPTANCE-0.4.md') $reportPath -Force}
if(Test-Path (Join-Path $projectRoot 'docs/ACCEPTANCE-0.5.md')){Copy-Item (Join-Path $projectRoot 'docs/ACCEPTANCE-0.5.md') $reportPath -Force}
Copy-Item (Join-Path $projectRoot 'docs/ACCEPTANCE-0.5.8.md'),(Join-Path $projectRoot 'docs/ACCEPTANCE-0.5.9.md'),(Join-Path $projectRoot 'docs/ACCEPTANCE-0.5.10.md'),(Join-Path $projectRoot 'docs/ACCEPTANCE-0.5.11.md') $reportPath -Force
foreach($name in @('saves-0511-local-tally.json','saves-0511-local-extra-tally.json','saves-0511-local-transfer-tally.json','saves-0511-cloud-tally.json','saves-0511-cloud-transfer-tally.json')){Copy-Item (Join-Path $projectRoot ('docs/evidence/'+$name)) $reportPath -Force}
& (Join-Path $PlaynitePath 'Toolbox.exe') pack $stagePath $releasePath
if ($LASTEXITCODE -ne 0) { throw 'Playnite Toolbox packaging failed' }
Get-ChildItem $releasePath -Filter '*.pext' | Get-FileHash -Algorithm SHA256

