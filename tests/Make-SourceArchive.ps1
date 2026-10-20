$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$stage=Join-Path $env:LOCALAPPDATA ('playnite-link-source-'+[Guid]::NewGuid().ToString('N').Substring(0,8));New-Item -ItemType Directory $stage|Out-Null
foreach($name in @('README.md','LICENSE','THIRD-PARTY-NOTICES.md','build.ps1','package.ps1','extension.yaml','.gitignore')){Copy-Item -LiteralPath (Join-Path $root $name) -Destination $stage}
foreach($folder in @('src','tests','vendor')){Copy-Item -LiteralPath (Join-Path $root $folder) -Destination $stage -Recurse}
$docs=Join-Path $stage 'docs';$evidence=Join-Path $docs 'evidence';New-Item -ItemType Directory -Force $docs,$evidence|Out-Null
Get-ChildItem (Join-Path $root 'docs') -File -Filter '*.md'|Copy-Item -Destination $docs
foreach($name in @('acceptance-tally.json','activity-tests.txt','hltb-tests.txt','runtime-tests.txt','hltb-live.json','hltb-live-restored.json','activity-harness-retirement.json')){Copy-Item -LiteralPath (Join-Path $root ('docs/evidence/'+$name)) -Destination $evidence}
foreach($prefix in @('activity-final-local','activity-final-cloud','activity-online','launch-final-local','launch-final-cloud')){foreach($peer in @('A','B','Verify')){Copy-Item -LiteralPath (Join-Path $root ('docs/evidence/'+$prefix+'-'+$peer+'.json')) -Destination $evidence}}
foreach($name in @('Tests','VaultTests','StreamingTests','SecurityTests','ApplicationLockTests','SetupTests','ActivityTests','HltbTests','RuntimeTests')){Copy-Item -LiteralPath (Join-Path $root ('docs/evidence/launch-'+$name+'.txt')) -Destination $evidence}
Get-ChildItem $stage -Recurse -File|Where-Object {$_.Name -match '\.private\.|rclone.*\.conf$|HowLongToBeat\.dat$|local-security\.dpapi$'}|ForEach-Object{throw ('Private file refused: '+$_.Name)}
$archive=Join-Path $root 'release/PlayniteLink-0.5.1-source.zip'
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -Force
$release=Join-Path $root 'release';Get-ChildItem $release -File|Where-Object {$_.Extension -in @('.zip','.pext')}|Sort-Object Name|ForEach-Object {((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+$_.Name)}|Set-Content (Join-Path $release 'SHA256SUMS.txt')
Write-Output $archive
