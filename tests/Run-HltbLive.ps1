param([string]$PlaynitePath="$env:LOCALAPPDATA/Playnite",[string]$RestoreFixture='',[uint64]$ExpectedSeconds=0)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$lab=Join-Path $env:LOCALAPPDATA ('pl-hltb-live-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
$app=Join-Path $lab 'app';$profile=Join-Path $lab 'p';$probe=Join-Path $profile 'Extensions/HostProbe'
New-Item -ItemType Directory -Force $app,$probe|Out-Null
& robocopy $PlaynitePath $app /E /XD Extensions 'User Data' /XF '*.pdb' '*.log' 'unins*' /NFL /NDL /NJH /NJS /NP
if($LASTEXITCODE -ge 8){throw 'App copy failed'}
$common=Join-Path $app 'Common.config';(Get-Content $common -Raw).Replace('net.pipe://localhost/PlaynitePipe',('net.pipe://localhost/HltbLive'+[Guid]::NewGuid().ToString('N')))|Set-Content $common
foreach($addon in @('playnite-gameactivity-plugin','playnite-howlongtobeat-plugin')){Copy-Item -LiteralPath (Join-Path $env:APPDATA ('Playnite/Extensions/'+$addon)) -Destination (Join-Path $profile 'Extensions') -Recurse}
# Copy only this add-on's encrypted session and preferences, never the shared browser profile.
$id='e08cd51f-9c9a-4ee3-a094-fde03b55492f';$source=Join-Path $env:APPDATA ('Playnite/ExtensionsData/'+$id);$target=Join-Path $profile ('ExtensionsData/'+$id)
New-Item -ItemType Directory -Force $target|Out-Null
foreach($name in @('HowLongToBeat.dat','HltbUserStats.json')){Copy-Item -LiteralPath (Join-Path $source $name) -Destination $target}
$settings=Get-Content (Join-Path $source 'config.json') -Raw|ConvertFrom-Json
$settings.AutoSetCurrentPlayTime=$false;$settings|ConvertTo-Json -Depth 60|Set-Content (Join-Path $target 'config.json')
$stats=Get-Content (Join-Path $source 'HltbUserStats.json') -Raw|ConvertFrom-Json
$entry=$stats.TitlesList|Where-Object {$_.UserGameId -and $_.UserGameId -ne '0'}|Select-Object -First 1
if(!$entry){throw 'No existing HLTB profile entry available'}
if($RestoreFixture){$original=Get-Content -LiteralPath $RestoreFixture -Raw|ConvertFrom-Json;@{Name=$original.Name;Submission=[string]$original.Submission;RestoreSeconds=[uint64]$original.BaselineSeconds;ExpectedSeconds=$ExpectedSeconds}|ConvertTo-Json|Set-Content (Join-Path $profile 'hltb-live-fixture.json')}
else{@{Name=$entry.GameName;Submission=[string]$entry.UserGameId}|ConvertTo-Json|Set-Content (Join-Path $profile 'hltb-live-fixture.json')}
New-Item -ItemType File (Join-Path $profile 'PROBE-ONLY')|Out-Null
@{Version=7;DatabasePath=(Join-Path $profile 'library');FirstTimeWizardComplete=$true;EnableGameControllerSupport=$false;StartOnBoot=$false}|ConvertTo-Json|Set-Content (Join-Path $profile 'config.json')
Copy-Item (Join-Path $projectRoot 'build/PlayniteLink.dll') $probe
"Id: HostProbe_130f2083-f8e6-46bd-a9a9-56ff84451f95`nName: Live HLTB acceptance probe`nAuthor: Test`nVersion: 1.0.0`nModule: HostProbe.dll`nType: GenericPlugin"|Set-Content (Join-Path $probe 'extension.yaml')
$compiler="$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe";$wpf="$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/WPF"
& $compiler /nologo /target:library "/out:$probe/HostProbe.dll" "/r:$app/Playnite.SDK.dll" "/r:$app/Newtonsoft.Json.dll" "/r:$projectRoot/build/PlayniteLink.dll" /r:System.Core.dll /r:System.Xaml.dll "/r:$wpf/WindowsBase.dll" "/r:$wpf/PresentationFramework.dll" (Join-Path $PSScriptRoot 'HltbLiveProbe.cs')
if($LASTEXITCODE -ne 0){throw 'Probe compile failed'}
$process=Start-Process (Join-Path $app 'Playnite.DesktopApp.exe') -ArgumentList @('--masterinstance','--nolibupdate','--hidesplashscreen','--userdatadir',('"'+$profile+'"')) -WindowStyle Hidden -PassThru
$result=Join-Path $profile 'hltb-live-result.json';$until=[DateTime]::UtcNow.AddMinutes(5)
while(!(Test-Path $result)-and [DateTime]::UtcNow -lt $until){Start-Sleep -Milliseconds 500}
if(!(Test-Path $result)){throw ('Live HLTB probe timeout: '+$lab)}
Copy-Item $result (Join-Path $projectRoot $(if($RestoreFixture){'docs/evidence/hltb-live-restored.json'}else{'docs/evidence/hltb-live.json'})) -Force
Get-Content $result
Write-Output ('Private live-auth lab retained at '+$lab)
if(!(Get-Content $result -Raw|ConvertFrom-Json).passed){throw 'Live HLTB verification not complete'}


