param([string]$PlaynitePath="$env:LOCALAPPDATA/Playnite")
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$liveExe=Join-Path $PlaynitePath 'Playnite.DesktopApp.exe'
$profile=Join-Path $env:APPDATA 'Playnite'
$extension=Join-Path $profile 'Extensions/PlayniteLink'
$probe=Join-Path $profile 'Extensions/PlayniteLinkLiveProbe'
$data=Join-Path $profile 'ExtensionsData/8ca18482-3ebe-4bf7-b229-a259b2094767'
if(!(Test-Path (Join-Path $data 'live-probe-ticket.json'))){throw 'Existing live test ticket required; this script never creates another mapping.'}
Start-Process $liveExe -ArgumentList '--shutdown' -WindowStyle Hidden | Out-Null
$deadline=[DateTime]::UtcNow.AddSeconds(25)
while((Get-Process Playnite.DesktopApp -ErrorAction SilentlyContinue | Where-Object Path -EQ $liveExe) -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 250}
if(Get-Process Playnite.DesktopApp -ErrorAction SilentlyContinue | Where-Object Path -EQ $liveExe){throw 'Playnite did not close gracefully; installation was not changed.'}
try {
 Copy-Item (Join-Path $projectRoot 'build/PlayniteLink.dll'),(Join-Path $projectRoot 'extension.yaml') $extension -Force
 Copy-Item (Join-Path $projectRoot 'build/PlayniteLink.dll') $probe -Force
 $compiler="$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
 $wpf="$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/WPF"
 $references=@("/r:$PlaynitePath/Playnite.SDK.dll","/r:$PlaynitePath/Newtonsoft.Json.dll","/r:$projectRoot/build/PlayniteLink.dll",'/r:System.Core.dll','/r:System.Security.dll','/r:System.Xaml.dll',"/r:$wpf/WindowsBase.dll","/r:$wpf/PresentationCore.dll","/r:$wpf/PresentationFramework.dll")
 & $compiler /nologo /target:library "/out:$probe/LiveHostProbe.dll" @references (Join-Path $PSScriptRoot 'LiveHostProbe.cs')
 if($LASTEXITCODE -ne 0){throw 'Live probe compilation failed'}
} finally {Start-Process $liveExe -ArgumentList '--nolibupdate','--hidesplashscreen' -WindowStyle Hidden | Out-Null}
Write-Output 'Updated installed extension and resumed the existing synthetic cloud test.'
