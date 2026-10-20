param([string]$PlaynitePath="$env:LOCALAPPDATA/Playnite",[string]$CloudRemote='', [string]$RcloneConfig='',[switch]$SkipBuild,[string]$EvidencePrefix="addon-peer")
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
if(!$SkipBuild){& (Join-Path $projectRoot 'build.ps1')}
$lab=Join-Path $env:LOCALAPPDATA ('pap-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $lab | Out-Null
$rclone=Join-Path $env:APPDATA 'Playnite/ExtensionsData/e8b2f7a1-8c3d-4f5e-9a6b-1c2d3e4f5a6b/Tools/rclone.exe'
$compiler="$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
$wpf="$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/WPF"
$config=if($RcloneConfig){$RcloneConfig}else{Join-Path $lab 'rclone.conf'}
$remote=if($CloudRemote){$CloudRemote}else{'vault:shared'}
if(!$CloudRemote){"[vault]`ntype = alias`nremote = $lab/cloud`n" | Set-Content -LiteralPath $config}
foreach($area in @('devices','ledger','revisions','setup')){& $rclone mkdir ($remote+'/playnite-link-v1/'+$area+'/objects') --config $config --retries 1 --low-level-retries 1;if($LASTEXITCODE -ne 0){throw 'Cannot create test cloud metadata directories'}}
& $rclone mkdir ($remote+'/playnite-link-v1/blobs') --config $config --retries 1 --low-level-retries 1
foreach($peer in @('A','B')){
 $app=Join-Path $lab ($peer+'/app');$profile=Join-Path $lab ($peer+'/p');$subject=Join-Path $profile 'Extensions/PlayniteLink';$probe=Join-Path $profile 'Extensions/HostProbe';$data=Join-Path $profile 'ExtensionsData/8ca18482-3ebe-4bf7-b229-a259b2094767'
 New-Item -ItemType Directory -Path $app,$profile,$subject,$probe,$data,(Join-Path $profile 'Themes/Desktop') -Force | Out-Null
 & robocopy $PlaynitePath $app /E /XD Extensions 'User Data' /XF '*.pdb' '*.log' 'unins*' /NFL /NDL /NJH /NJS /NP
 if($LASTEXITCODE -ge 8){throw 'Playnite copy failed'}
 $common=Join-Path $app 'Common.config';(Get-Content -LiteralPath $common -Raw).Replace('net.pipe://localhost/PlaynitePipe',('net.pipe://localhost/AddonProbe'+[Guid]::NewGuid().ToString('N'))) | Set-Content -LiteralPath $common
 Copy-Item (Join-Path $projectRoot 'build/PlayniteLink.dll'),(Join-Path $projectRoot 'src/SetupApply.ps1'),(Join-Path $projectRoot 'extension.yaml') $subject
 Copy-Item (Join-Path $projectRoot 'build/PlayniteLink.dll') $probe
 "Id: HostProbe_130f2083-f8e6-46bd-a9a9-56ff84451f95`nName: Isolated add-on peer probe`nAuthor: Test`nVersion: 1.0.0`nModule: HostProbe.dll`nType: GenericPlugin" | Set-Content (Join-Path $probe 'extension.yaml')
 $refs=@("/r:$app/Playnite.SDK.dll","/r:$app/Newtonsoft.Json.dll","/r:$projectRoot/build/PlayniteLink.dll",'/r:System.Core.dll','/r:System.Xaml.dll',"/r:$wpf/WindowsBase.dll","/r:$wpf/PresentationFramework.dll","/r:$wpf/PresentationCore.dll")
 & $compiler /nologo /target:library "/out:$probe/HostProbe.dll" @refs (Join-Path $PSScriptRoot 'AddonPeerProbe.cs');if($LASTEXITCODE -ne 0){throw 'Probe compile failed'}
 @{Version=7;DatabasePath=(Join-Path $profile 'library');FirstTimeWizardComplete=$true;EnableGameControllerSupport=$false;StartOnBoot=$false;StartInFullscreen=($peer -eq 'B');Theme='Playnite_builtin_DefaultDesktop'} | ConvertTo-Json | Set-Content (Join-Path $profile 'config.json')
 'keep-local-'+$peer | Set-Content -NoNewline (Join-Path $profile 'local-settings-sentinel.txt')
 New-Item -ItemType File (Join-Path $profile 'PROBE-ONLY') | Out-Null
 @{RcloneExe=$rclone;ConfigFile=$config;RemoteFolder=$remote} | ConvertTo-Json | Set-Content (Join-Path $data 'rclone-settings.json')
 $setup=Join-Path $data 'setup';New-Item -ItemType Directory $setup | Out-Null
 # The test companion is intentionally device-only; it must never be replicated.
 $sha=[Security.Cryptography.SHA256]::Create();try{$hash=[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes('Extensions:HostProbe_130f2083-f8e6-46bd-a9a9-56ff84451f95'))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
 @{Enabled=$false;LocalOnly=@($hash)} | ConvertTo-Json | Set-Content (Join-Path $setup 'options.json')
 if($peer -eq 'A'){
  $fixture=Join-Path $profile 'Extensions/FixtureAddon';New-Item -ItemType Directory $fixture | Out-Null
  & $compiler /nologo /target:library "/out:$fixture/FixtureAddon.dll" "/r:$app/Playnite.SDK.dll" (Join-Path $PSScriptRoot 'FixtureAddon.cs');if($LASTEXITCODE -ne 0){throw 'Fixture compile failed'}
  "Id: FixtureAddon_758ab3a0-7335-48bb-8099-feb10b420ea4`nName: Fixture shared extension`nAuthor: Test`nVersion: 1.0.0`nModule: FixtureAddon.dll`nType: GenericPlugin" | Set-Content (Join-Path $fixture 'extension.yaml')
  $theme=Join-Path $profile 'Themes/Desktop/FixtureTheme';Copy-Item (Join-Path $app 'Themes/Desktop/Default') $theme -Recurse
  "Id: FixtureTheme`nName: Fixture shared theme`nAuthor: Test`nVersion: 1.0.0`nThemeApiVersion: 2.0.0" | Set-Content (Join-Path $theme 'theme.yaml')
 }
 @{Peer=$peer;Action=$(if($peer -eq 'A'){'Publish'}else{'Receive'});ExpectedPlugins=@('758ab3a0-7335-48bb-8099-feb10b420ea4');FullscreenPreference=($peer -eq 'B')} | ConvertTo-Json | Set-Content (Join-Path $profile 'addon-scenario.json')
 $exe=Join-Path $app $(if($peer -eq 'A'){'Playnite.DesktopApp.exe'}else{'Playnite.FullscreenApp.exe'})
 Start-Process -FilePath $exe -ArgumentList @('--masterinstance','--nolibupdate','--hidesplashscreen','--userdatadir',('"'+$profile+'"')) -WindowStyle Hidden | Out-Null
 $result=Join-Path $profile 'addon-result.json';$deadline=[DateTime]::UtcNow.AddMinutes($(if($CloudRemote){20}else{5}))
 while(!(Test-Path -LiteralPath $result) -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 500}
 if(!(Test-Path -LiteralPath $result)){throw ('Peer test did not complete: '+$profile)}
 $value=Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
 $evidence=Join-Path $projectRoot 'docs/evidence';Copy-Item $result (Join-Path $evidence ($EvidencePrefix+'-'+$peer+'.json')) -Force
 Get-ChildItem -LiteralPath $profile -Filter 'addon-*.png' | ForEach-Object{Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $evidence ($EvidencePrefix+'-'+$peer+'-'+$_.Name)) -Force}
 Write-Output ($value | ConvertTo-Json)
 if(!$value.passed){throw ('Peer '+$peer+' failed: '+$value.error)}
}
Write-Output ('Two actual Playnite profiles retained at '+$lab)
