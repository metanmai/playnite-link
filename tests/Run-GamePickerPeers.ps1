param([string]$PlaynitePath="$env:LOCALAPPDATA/Playnite",[string]$CloudRemote='',[string]$RcloneConfig='',[switch]$SkipBuild,[string]$EvidencePrefix='activity-local',[string]$HltbFixture='')
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
if(!$SkipBuild){& (Join-Path $projectRoot 'build.ps1')}
$lab=Join-Path (Join-Path $env:USERPROFILE 'PlayniteLinkTestLabs') ('picker-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory $lab|Out-Null
$rclone=Join-Path $env:APPDATA 'Playnite/ExtensionsData/e8b2f7a1-8c3d-4f5e-9a6b-1c2d3e4f5a6b/Tools/rclone.exe'
$cfg=Join-Path $lab 'rclone.private.conf'
if($CloudRemote){if(!$RcloneConfig){throw 'Cloud config path required'};Copy-Item -LiteralPath $RcloneConfig -Destination $cfg; "`n[fixture]`ntype = alias`nremote = $CloudRemote`n"|Add-Content $cfg}else{"[fixture]`ntype = alias`nremote = $lab/cloud`n"|Set-Content $cfg}
foreach($area in @('devices','ledger','activity','revisions','setup')){& $rclone mkdir ('fixture:PlayniteLink/playnite-link-v1/'+$area+'/objects') --config $cfg --retries 1 --low-level-retries 1 --contimeout 5s --timeout 15s;if($LASTEXITCODE -ne 0){throw 'Cannot prepare test metadata folder'}}
$compiler="$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe";$wpf="$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/WPF"
$gameIds=@{A=[Guid]::NewGuid().ToString();B=[Guid]::NewGuid().ToString()}
foreach($peer in @('A','B')){
 $app=Join-Path $lab "$peer/app";$profile=Join-Path $lab "$peer/p";$extension=Join-Path $profile 'Extensions/PlayniteLink';$probe=Join-Path $profile 'Extensions/HostProbe';$data=Join-Path $profile 'ExtensionsData/8ca18482-3ebe-4bf7-b229-a259b2094767'
 New-Item -ItemType Directory -Force $app,$profile,$extension,$probe,$data|Out-Null
 & robocopy $PlaynitePath $app /E /XD Extensions 'User Data' /XF '*.pdb' '*.log' 'unins*' /NFL /NDL /NJH /NJS /NP
 if($LASTEXITCODE -ge 8){throw 'App copy failed'}
 $common=Join-Path $app 'Common.config';(Get-Content $common -Raw).Replace('net.pipe://localhost/PlaynitePipe',('net.pipe://localhost/ActivityPeer'+[Guid]::NewGuid().ToString('N')))|Set-Content $common
 foreach($addon in @('playnite-gameactivity-plugin','playnite-howlongtobeat-plugin')){Copy-Item -LiteralPath (Join-Path $env:APPDATA ('Playnite/Extensions/'+$addon)) -Destination (Join-Path $profile 'Extensions') -Recurse}
 if($HltbFixture){
  Copy-Item -LiteralPath $HltbFixture -Destination (Join-Path $profile 'online-fixture.private.json')
  $hltbId='e08cd51f-9c9a-4ee3-a094-fde03b55492f';$hltbSource=Join-Path $env:APPDATA ('Playnite/ExtensionsData/'+$hltbId);$hltbTarget=Join-Path $profile ('ExtensionsData/'+$hltbId)
  New-Item -ItemType Directory -Force $hltbTarget|Out-Null
  foreach($name in @('HowLongToBeat.dat','HltbUserStats.json')){Copy-Item -LiteralPath (Join-Path $hltbSource $name) -Destination $hltbTarget}
  $hltbSettings=Get-Content (Join-Path $hltbSource 'config.json') -Raw|ConvertFrom-Json;$hltbSettings.AutoSetCurrentPlayTime=$false;$hltbSettings|ConvertTo-Json -Depth 60|Set-Content (Join-Path $hltbTarget 'config.json')
 }
 Copy-Item (Join-Path $projectRoot 'build/PlayniteLink.dll'),(Join-Path $projectRoot 'src/SetupApply.ps1'),(Join-Path $projectRoot 'extension.yaml') $extension
 Copy-Item (Join-Path $projectRoot 'build/PlayniteLink.dll') $probe
 "Id: HostProbe_130f2083-f8e6-46bd-a9a9-56ff84451f95`nName: Activity integration acceptance probe`nAuthor: Test`nVersion: 1.0.0`nModule: HostProbe.dll`nType: GenericPlugin"|Set-Content (Join-Path $probe 'extension.yaml')
 $refs=@("/r:$app/Playnite.SDK.dll","/r:$app/Newtonsoft.Json.dll","/r:$projectRoot/build/PlayniteLink.dll",'/r:System.Core.dll','/r:System.Xaml.dll',"/r:$wpf/WindowsBase.dll","/r:$wpf/PresentationFramework.dll","/r:$wpf/PresentationCore.dll")
 & $compiler /nologo /target:library "/out:$probe/HostProbe.dll" @refs (Join-Path $PSScriptRoot 'GamePickerPeerProbe.cs');if($LASTEXITCODE -ne 0){throw 'Probe compile failed'}
 & $compiler /nologo /target:winexe "/out:$profile/FixtureGame.exe" (Join-Path $PSScriptRoot 'FixtureGame.cs');if($LASTEXITCODE -ne 0){throw 'Fixture compile failed'}
 @{Version=7;DatabasePath=(Join-Path $profile 'library');FirstTimeWizardComplete=$true;EnableGameControllerSupport=$false;StartOnBoot=$false;StartInFullscreen=($peer -eq 'B');Theme='Playnite_builtin_DefaultDesktop';AfterGameClose=0}|ConvertTo-Json|Set-Content (Join-Path $profile 'config.json')
 New-Item -ItemType File (Join-Path $profile 'PROBE-ONLY')|Out-Null
 @{RcloneExe=$rclone;ConfigFile=$cfg;RemoteFolder='fixture:PlayniteLink'}|ConvertTo-Json|Set-Content (Join-Path $data 'rclone-settings.json')
 @{AutomaticGames=$false;OnlineHltb=$false;SetupComplete=$true;SyncPaused=$true}|ConvertTo-Json|Set-Content (Join-Path $data 'onboarding.json')
 @{Name=('Device '+$peer);AutoSync=$false;SyncPlaytime=$true;SyncSaves=$true;SyncPreferences=$true;IntervalSeconds=86400;BandwidthKiB=0;SharePaths=$false}|ConvertTo-Json|Set-Content (Join-Path $data 'device-settings.json')
 @{Peer=$peer;Action=$peer;GameId=$gameIds[$peer]}|ConvertTo-Json|Set-Content (Join-Path $profile 'activity-scenario.json')
}
$last=$null
foreach($run in @('A','B','Verify')){
 $peer=if($run -eq 'Verify'){'A'}else{$run};$app=Join-Path $lab "$peer/app";$profile=Join-Path $lab "$peer/p"
 $result=Join-Path $profile 'activity-result.json'
 if($run -eq 'Verify'){
  Move-Item -LiteralPath $result -Destination ($result+'.initial')
  @{Peer='A';Action='Verify';GameId=$gameIds.A;ExpectedTotal=$last.playtime}|ConvertTo-Json|Set-Content (Join-Path $profile 'activity-scenario.json')
 }
 $exe=Join-Path $app $(if($peer -eq 'B'){'Playnite.FullscreenApp.exe'}else{'Playnite.DesktopApp.exe'})
 $process=Start-Process $exe -ArgumentList @('--masterinstance','--nolibupdate','--hidesplashscreen','--userdatadir',('"'+$profile+'"')) -WindowStyle Hidden -PassThru
 $deadline=[DateTime]::UtcNow.AddMinutes($(if($CloudRemote){20}else{5}))
 while(!(Test-Path $result) -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 500}
 if(!(Test-Path $result)){throw ('Activity test did not complete: '+$profile)}
 $last=Get-Content $result -Raw|ConvertFrom-Json
 $evidence=Join-Path $projectRoot 'docs/evidence';Copy-Item $result (Join-Path $evidence ($EvidencePrefix+'-'+$run+'.json')) -Force
 Get-ChildItem $profile -Filter '*.png'|Where-Object{$_.Name -like 'picker-*' -or $_.Name -eq 'game-simple.png'}|ForEach-Object{Copy-Item $_.FullName (Join-Path $evidence ($EvidencePrefix+'-'+$run+'-'+$_.Name)) -Force}
 Write-Output ($last|ConvertTo-Json)
 if(!$last.passed){throw ('Integration failure: '+$last.error)}
 if(!$process.WaitForExit(15000)){throw 'Probe did not close its Playnite instance'}
}
Write-Output ('Two activity-enabled Playnite profiles retained at '+$lab)
