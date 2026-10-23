param([string]$PlaynitePath="$env:LOCALAPPDATA/Playnite")
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$profile=Join-Path $env:APPDATA 'Playnite'
$extension=Join-Path $profile 'Extensions/PlayniteLink'
$data=Join-Path $profile 'ExtensionsData/8ca18482-3ebe-4bf7-b229-a259b2094767'
$liveExe=Join-Path $PlaynitePath 'Playnite.DesktopApp.exe'
$installed=Get-Process Playnite.DesktopApp -ErrorAction SilentlyContinue | Where-Object Path -EQ $liveExe
if($installed){
 Start-Process $liveExe -ArgumentList '--shutdown' -WindowStyle Hidden | Out-Null
 $deadline=[DateTime]::UtcNow.AddSeconds(30)
 while((Get-Process Playnite.DesktopApp -ErrorAction SilentlyContinue | Where-Object Path -EQ $liveExe) -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 250}
 if(Get-Process Playnite.DesktopApp -ErrorAction SilentlyContinue | Where-Object Path -EQ $liveExe){throw 'Playnite did not close gracefully. No installation files were changed.'}
}
try {
 $backup=Join-Path $data ('install-backups/'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
 New-Item -ItemType Directory -Force $backup,$extension | Out-Null
 $protectedFile=Join-Path $data 'local-security.dpapi'
 $protectionHash=if(Test-Path -LiteralPath $protectedFile){(Get-FileHash -LiteralPath $protectedFile -Algorithm SHA256).Hash}else{$null}
 if($protectionHash){Copy-Item -LiteralPath $protectedFile -Destination $backup}
 Get-ChildItem -LiteralPath $extension -File | Copy-Item -Destination $backup
 Copy-Item (Join-Path $projectRoot 'src/SetupApply.ps1'),(Join-Path $projectRoot 'build/PlayniteLink.dll'),(Join-Path $projectRoot 'extension.yaml'),(Join-Path $projectRoot 'README.md'),(Join-Path $projectRoot 'LICENSE'),(Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') $extension -Force
 Copy-Item (Join-Path $projectRoot 'build/StartPage.SDK.dll') $extension -Force
 Copy-Item (Join-Path $projectRoot 'build/AchievementEngine') $extension -Recurse -Force
 Copy-Item (Join-Path $projectRoot 'build/SaveManager.Core.dll'),(Join-Path $projectRoot 'build/Microsoft.Toolkit.Uwp.Notifications.dll'),(Join-Path $projectRoot 'src/RetireAchievementAddon.ps1') $extension -Force
 $reportPath=Join-Path $extension 'docs';New-Item -ItemType Directory -Force $reportPath | Out-Null
 Copy-Item (Join-Path $projectRoot 'docs/ACCEPTANCE.md'),(Join-Path $projectRoot 'docs/evidence/acceptance-tally.json') $reportPath -Force
 # Archive the temporary companion; never launch test games in the normal profile again.
 $probe=[IO.Path]::GetFullPath((Join-Path $profile 'Extensions/PlayniteLinkLiveProbe'))
 $archive=[IO.Path]::GetFullPath((Join-Path $backup 'PlayniteLinkLiveProbe'))
 $extensionsRoot=[IO.Path]::GetFullPath((Join-Path $profile 'Extensions'))+[IO.Path]::DirectorySeparatorChar
 $dataRoot=[IO.Path]::GetFullPath($data)+[IO.Path]::DirectorySeparatorChar
 if(!$probe.StartsWith($extensionsRoot,[StringComparison]::OrdinalIgnoreCase) -or !$archive.StartsWith($dataRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Archive paths failed workspace validation.'}
 if(Test-Path -LiteralPath $probe){Move-Item -LiteralPath $probe -Destination $archive}
 if($protectionHash -and (Get-FileHash -LiteralPath $protectedFile -Algorithm SHA256).Hash -ne $protectionHash){throw 'Protected passcode settings changed during installation.'}
 if($protectionHash){Write-Output 'Existing passcode settings preserved without modification.'}
 Write-Output ('Updated Playnite Link. Previous files retained in '+$backup)
} finally {Start-Process $liveExe -ArgumentList '--nolibupdate','--hidesplashscreen' -WindowStyle Hidden | Out-Null}
