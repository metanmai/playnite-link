param(
    [ValidateSet('Desktop','Fullscreen')][string]$Mode = 'Desktop',
    [string]$PlaynitePath = "$env:LOCALAPPDATA/Playnite",
    [switch]$KeepOpen,
    [switch]$SkipBuild,
    [switch]$LockProbe,
    [switch]$OnboardingProbe,
    [string]$BuildDirectory = ''
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$buildDir = if($BuildDirectory){[IO.Path]::GetFullPath($BuildDirectory)}else{Join-Path $projectRoot 'build'}
if(!$SkipBuild){& (Join-Path $projectRoot 'build.ps1') -PlaynitePath $PlaynitePath -BuildDirectory $buildDir}
$labParent=Join-Path $env:USERPROFILE 'PlayniteLinkTestLabs'
New-Item -ItemType Directory -Force $labParent | Out-Null
$labRoot = Join-Path $labParent ('probe-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$appPath = Join-Path $labRoot 'app'
$profilePath = Join-Path $labRoot 'profile'
$subjectPath = Join-Path $profilePath 'Extensions/PlaytimeRclone'
$probePath = Join-Path $profilePath 'Extensions/HostProbe'
New-Item -ItemType Directory -Force $appPath,$profilePath,$subjectPath,$probePath | Out-Null
& robocopy $PlaynitePath $appPath /E /XD Extensions 'User Data' /XF '*.pdb' '*.log' 'unins*' /NFL /NDL /NJH /NJS /NP
if ($LASTEXITCODE -ge 8) { throw 'Failed to copy installed Playnite to isolated test lab' }
Copy-Item (Join-Path $buildDir 'PlayniteLink.dll'),(Join-Path $buildDir 'extension.yaml') $subjectPath
if(Test-Path (Join-Path $buildDir 'SetupApply.ps1')){Copy-Item (Join-Path $buildDir 'SetupApply.ps1') $subjectPath}
$commonPath = Join-Path $appPath 'Common.config'
$pipeName = 'net.pipe://localhost/PlaytimeProbe' + [Guid]::NewGuid().ToString('N')
(Get-Content $commonPath -Raw).Replace('net.pipe://localhost/PlaynitePipe',$pipeName) | Set-Content $commonPath
@{ Version=7; DatabasePath=(Join-Path $profilePath 'library'); FirstTimeWizardComplete=$true; EnableGameControllerSupport=$false; AfterGameClose=0 } | ConvertTo-Json | Set-Content (Join-Path $profilePath 'config.json')
New-Item -ItemType File (Join-Path $profilePath 'PROBE-ONLY') | Out-Null
@'
Id: HostProbe_130f2083-f8e6-46bd-a9a9-56ff84451f95
Name: Playtime test probe
Author: Test fixture
Version: 0.1.0
Module: HostProbe.dll
Type: GenericPlugin
'@ | Set-Content (Join-Path $probePath 'extension.yaml')
$compilerPath = "$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
$wpfRoot = "$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/WPF"
$references = @("/r:$appPath/Playnite.SDK.dll","/r:$appPath/Newtonsoft.Json.dll","/r:$buildDir/PlayniteLink.dll",'/r:System.Core.dll','/r:System.Xaml.dll',"/r:$wpfRoot/WindowsBase.dll","/r:$wpfRoot/PresentationFramework.dll","/r:$wpfRoot/PresentationCore.dll")
Copy-Item (Join-Path $buildDir 'PlayniteLink.dll') $probePath
$probeSource=if($OnboardingProbe){'OnboardingHostProbe.cs'}elseif($LockProbe){'LockHostProbe.cs'}else{'PersonalHostProbe.cs'}
& $compilerPath /nologo /target:library "/out:$probePath/HostProbe.dll" @references (Join-Path $PSScriptRoot $probeSource)
if ($LASTEXITCODE -ne 0) { throw 'Host probe compile failed' }
& $compilerPath /nologo /target:winexe "/out:$profilePath/FixtureGame.exe" (Join-Path $PSScriptRoot 'FixtureGame.cs')
if ($LASTEXITCODE -ne 0) { throw 'Fixture compile failed' }
Add-Type -Path (Join-Path $appPath 'Newtonsoft.Json.dll')
Add-Type -Path (Join-Path $appPath 'Playnite.SDK.dll')
Add-Type -Path (Join-Path $subjectPath 'PlayniteLink.dll')
$repository = [PlaytimeRclone.Repository]::new((Join-Path $profilePath 'ExtensionsData/8ca18482-3ebe-4bf7-b229-a259b2094767'))
$repository.Enroll('f3d3b83a-fc3f-40ec-87c5-24094bdd81d9','manual:demo-adventure',100,100)
$repository.Dispose()
if($LockProbe){
 $protection=[PlaytimeRclone.LocalSecurity]::new((Join-Path $profilePath 'ExtensionsData/8ca18482-3ebe-4bf7-b229-a259b2094767'))
 $protection.Configure('isolated-test-passcode',1)
 $protection.Lock()
}
$rcloneExe = Join-Path $env:APPDATA 'Playnite/ExtensionsData/e8b2f7a1-8c3d-4f5e-9a6b-1c2d3e4f5a6b/Tools/rclone.exe'
if (!(Test-Path $rcloneExe)) { throw 'Set rcloneExe to your installed Rclone executable for the probe' }
# This fixture represents joining an established vault, whose metadata areas exist.
# Avoid spending rclone's missing-directory retry window on an unrelated cold-vault case.
foreach($area in @('devices','ledger','activity','revisions','setup')){New-Item -ItemType Directory -Force (Join-Path $profilePath ('Cloud/personal/playnite-link-v1/'+$area+'/objects')) | Out-Null}
$rcloneConfig = Join-Path $profilePath 'fixture-rclone.conf'
"[vault]`ntype = alias`nremote = $profilePath/Cloud`n" | Set-Content $rcloneConfig
@{ RcloneExe=$rcloneExe;ConfigFile=$rcloneConfig;RemoteFolder='vault:personal' } | ConvertTo-Json | Set-Content (Join-Path $profilePath 'ExtensionsData/8ca18482-3ebe-4bf7-b229-a259b2094767/rclone-settings.json')
if($OnboardingProbe){
 $extensionData=Join-Path $profilePath 'ExtensionsData/8ca18482-3ebe-4bf7-b229-a259b2094767'
 @{AutomaticGames=$true;SetupComplete=$false;SetupStep=0;SyncPaused=$false} | ConvertTo-Json | Set-Content (Join-Path $extensionData 'onboarding.json')
 & $compilerPath /nologo /target:exe "/out:$profilePath/fixture-rclone.exe" (Join-Path $PSScriptRoot 'TransferFixture.cs')
 if($LASTEXITCODE -ne 0){throw 'Transfer fixture compilation failed'}
 [IO.File]::WriteAllText((Join-Path $profilePath 'real-rclone-path.txt'),$rcloneExe)
 @{ RcloneExe=(Join-Path $profilePath 'fixture-rclone.exe');ConfigFile=$rcloneConfig;RemoteFolder='vault:personal' } | ConvertTo-Json | Set-Content (Join-Path $extensionData 'rclone-settings.json')
}
$exeName = if ($Mode -eq 'Desktop') { 'Playnite.DesktopApp.exe' } else { 'Playnite.FullscreenApp.exe' }
$exePath = Join-Path $appPath $exeName
$arguments = @('--masterinstance','--nolibupdate','--hidesplashscreen','--userdatadir',('"' + $profilePath + '"'))
if ($Mode -eq 'Fullscreen') { $arguments += @('--fullscreenwidth','1280','--fullscreenheight','720') }
$testProcess = Start-Process $exePath -ArgumentList $arguments -WindowStyle Hidden -PassThru
$resultPath = Join-Path $profilePath 'host-result.json'
$deadline = [DateTime]::UtcNow.AddSeconds(240)
while (!(Test-Path $resultPath) -and [DateTime]::UtcNow -lt $deadline -and !$testProcess.HasExited) { Start-Sleep -Seconds 1 }
# Shutdown is routed to this lab's unique named pipe, never the user's instance.
if (!$KeepOpen -and !$OnboardingProbe -and !$testProcess.HasExited) { Start-Process $exePath -ArgumentList '--shutdown' -WindowStyle Hidden | Out-Null }
if (!(Test-Path $resultPath)) { throw "Host probe did not finish; inspect $profilePath" }
if($OnboardingProbe -and (Get-Content $resultPath -Raw|ConvertFrom-Json).passed){
 if(!$testProcess.HasExited -and !$testProcess.WaitForExit(15000)){throw 'First instance did not exit'}
 # The extension restart helper launches the next process after this one exits.
 $restartResult=Join-Path $profilePath 'vault-restart-result.json';$restartDeadline=[DateTime]::UtcNow.AddSeconds(90)
 while(!(Test-Path $restartResult)-and [DateTime]::UtcNow -lt $restartDeadline){Start-Sleep -Milliseconds 300}
 Start-Process $exePath -ArgumentList '--shutdown' -WindowStyle Hidden | Out-Null
 if(!(Test-Path $restartResult)){throw 'Restart verification did not finish'}
 Copy-Item $restartResult (Join-Path $projectRoot ('docs/evidence/vault-switch-'+$Mode.ToLowerInvariant()+'-restart.json')) -Force
 Write-Output (Get-Content $restartResult -Raw)
 if(!(Get-Content $restartResult -Raw|ConvertFrom-Json).passed){throw 'Restart verification failed'}
}
$evidencePath = Join-Path $projectRoot 'docs/evidence'
New-Item -ItemType Directory -Force $evidencePath | Out-Null
$prefix=if($OnboardingProbe){'onboarding-'+$Mode.ToLowerInvariant()}elseif($LockProbe){'lock-'+$Mode.ToLowerInvariant()}else{$Mode.ToLowerInvariant()}
Copy-Item $resultPath (Join-Path $evidencePath ($prefix + '-host.json')) -Force
Get-ChildItem $profilePath -Filter '*.png' | ForEach-Object { Copy-Item $_.FullName (Join-Path $evidencePath ($prefix + '-' + $_.Name)) -Force }
Copy-Item (Join-Path $profilePath 'extensions.log') (Join-Path $evidencePath ($prefix + '-extensions.log')) -Force
$result = Get-Content $resultPath -Raw | ConvertFrom-Json
Write-Output (Get-Content $resultPath -Raw)
Write-Output "Isolated lab retained at $labRoot"
if (!$result.passed) { throw 'Host probe failed' }

