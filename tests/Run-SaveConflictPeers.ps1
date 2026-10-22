param([Parameter(Mandatory=$true)][string]$Lab,[string]$EvidencePrefix='save-conflicts',[switch]$ExtraOnly,[switch]$TransferOnly)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$labPath=[IO.Path]::GetFullPath($Lab)
if(!$labPath.StartsWith('C:\Users\win11\PlayniteLinkTestLabs\picker-')){throw 'Not an isolated test lab'}
$compiler="$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe";$wpf="$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/WPF"
foreach($peer in @('A','B')){
 $app=Join-Path $Lab "$peer/app";$profile=Join-Path $Lab "$peer/p";if(!(Test-Path "$profile/PROBE-ONLY")){throw 'Missing test marker'}
 $probe=Join-Path $profile 'Extensions/HostProbe';$data=Join-Path $profile 'ExtensionsData/8ca18482-3ebe-4bf7-b229-a259b2094767'
 Copy-Item "$projectRoot/build/PlayniteLink.dll" "$profile/Extensions/PlayniteLink/PlayniteLink.dll" -Force;Copy-Item "$projectRoot/build/PlayniteLink.dll" $probe -Force;Copy-Item "$projectRoot/extension.yaml" "$profile/Extensions/PlayniteLink/extension.yaml" -Force
 $o=Get-Content "$data/onboarding.json" -Raw|ConvertFrom-Json;$o.SyncPaused=$true;$o|ConvertTo-Json|Set-Content "$data/onboarding.json"
 $prefs=Get-Content "$data/device-settings.json" -Raw|ConvertFrom-Json;$prefs.AutoSync=$false;$prefs|ConvertTo-Json|Set-Content "$data/device-settings.json"
 $refs=@("/r:$app/Playnite.SDK.dll","/r:$app/Newtonsoft.Json.dll","/r:$projectRoot/build/PlayniteLink.dll",'/r:System.Core.dll','/r:System.Xaml.dll',"/r:$wpf/WindowsBase.dll","/r:$wpf/PresentationFramework.dll","/r:$wpf/PresentationCore.dll")
 & $compiler /nologo /target:library "/out:$probe/HostProbe.dll" @refs (Join-Path $projectRoot 'tests/SaveConflictPeerProbe.cs');if($LASTEXITCODE-ne 0){throw 'Compile failed'}
}
$steps=@(
 @{Peer='A';Name='A-push';Write='a1';Expect='a1';Choice='local';AllowConflict=$true},
 @{Peer='B';Name='B-pull';Expect='a1'},
 @{Peer='B';Name='B-push';Write='b2';Expect='b2'},
 @{Peer='A';Name='A-pull';Expect='b2'},
 @{Peer='A';Name='A-offline';Write='a-offline';Sync=$false;Expect='a-offline'},
 @{Peer='B';Name='B-diverge';Write='b-cloud';Expect='b-cloud'},
 @{Peer='A';Name='A-cancel';Choice='cancel';Conflict=$true;Expect='a-offline'},
 @{Peer='A';Name='A-use-cloud';Choice='cloud';Conflict=$true;Expect='b-cloud'},
 @{Peer='B';Name='B-converge';Expect='b-cloud'},
 @{Peer='B';Name='B-offline';Write='b-offline';Sync=$false;Expect='b-offline'},
 @{Peer='A';Name='A-diverge';Write='a-cloud';Expect='a-cloud'},
 @{Peer='B';Name='B-keep-local';Choice='local';Conflict=$true;Expect='b-offline'},
 @{Peer='A';Name='A-converge';Expect='b-offline'},
 @{Peer='A';Name='A-offline-retry';Write='a-retry';Sync=$false;Expect='a-retry'},
 @{Peer='B';Name='B-before-retry';Write='b-retry';Expect='b-retry'},
 @{Peer='A';Name='A-failed-transfer';Fail=$true;Expect='a-retry'},
 @{Peer='A';Name='A-retry-cloud';Choice='cloud';Conflict=$true;Expect='b-retry'},
 @{Peer='B';Name='B-retry-converge';Expect='b-retry'},
 @{Peer='A';Name='A-empty-cancel';Write='';Choice='cancel';Conflict=$true;Expect=''},
 @{Peer='A';Name='A-empty-confirm';Choice='local';Conflict=$true;Expect=''},
 @{Peer='B';Name='B-pull-deletion';Expect=''},
 @{Peer='B';Name='B-add-after-delete';Write='restored';Expect='restored'},
 @{Peer='A';Name='A-pull-after-delete';Expect='restored'},
 @{Peer='A';Name='A-offline-quiet';Write='quiet-a';Sync=$false;Expect='quiet-a'},
 @{Peer='B';Name='B-push-quiet';Write='quiet-b';Expect='quiet-b'},
 @{Peer='A';Name='A-background-conflict';Interactive=$false;Expect='quiet-a'},
 @{Peer='A';Name='A-review-quiet';Choice='local';Conflict=$true;Expect='quiet-a'},
 @{Peer='B';Name='B-final-pull';Expect='quiet-a'},
 @{Peer='A';Name='A-repeat';Expect='quiet-a'},
 @{Peer='B';Name='B-repeat';Expect='quiet-a'}
)
if($ExtraOnly){$steps=@(
 @{Peer='A';Name='A-same-offline';Write='identical';Sync=$false;Expect='identical'},
 @{Peer='B';Name='B-same-write';Write='identical';Expect='identical'},
 @{Peer='A';Name='A-identical-no-conflict';Expect='identical'},
 @{Peer='B';Name='B-identical-no-conflict';Expect='identical'},
 @{Peer='A';Name='A-extra-offline';Write='extra-a';Sync=$false;Expect='extra-a'},
 @{Peer='B';Name='B-extra-diverge';Write='extra-b';Expect='extra-b'},
 @{Peer='A';Name='A-extra-background';Interactive=$false;Expect='extra-a'},
 @{Peer='A';Name='A-extra-review';Choice='cloud';Conflict=$true;Expect='extra-b'},
 @{Peer='B';Name='B-extra-converge';Expect='extra-b'},
 @{Peer='A';Name='A-offline-close';Write='close-a';Sync=$false;Expect='close-a'},
 @{Peer='B';Name='B-push-close';Write='close-b';Expect='close-b'},
 @{Peer='A';Name='A-close-dialog';Choice='close';Conflict=$true;Expect='close-a'},
 @{Peer='A';Name='A-after-close';Choice='cloud';Conflict=$true;Expect='close-b'},
 @{Peer='B';Name='B-after-close';Expect='close-b'},
 @{Peer='A';Name='A-interrupted-upload';Write='interrupted-upload';CancelTransfer=$true;Expect='interrupted-upload'},
 @{Peer='B';Name='B-before-upload-resumes';Expect='close-b'},
 @{Peer='A';Name='A-resume-upload';Expect='interrupted-upload'},
 @{Peer='B';Name='B-pull-resumed-upload';Expect='interrupted-upload'},
 @{Peer='B';Name='B-final-small';Write='finished';Expect='finished'},
 @{Peer='A';Name='A-final-small';Expect='finished'}
)}
if($TransferOnly){$steps=@(
 @{Peer='A';Name='A-ui-offline';Write='ui-a';Sync=$false;Expect='ui-a'},
 @{Peer='B';Name='B-ui-change';Write='ui-b';Expect='ui-b'},
 @{Peer='A';Name='A-ui-background';Interactive=$false;Expect='ui-a'},
 @{Peer='A';Name='A-ui-choose-saves';ViaGamePage=$true;Choice='cloud';Conflict=$true;Expect='ui-b'},
 @{Peer='B';Name='B-ui-converge';Expect='ui-b'},
 @{Peer='A';Name='A-interrupted-upload';Write='interrupted-upload';CancelTransfer=$true;Expect='interrupted-upload'},
 @{Peer='B';Name='B-before-upload-resumes';Expect='ui-b'},
 @{Peer='A';Name='A-resume-upload';Expect='interrupted-upload'},
 @{Peer='B';Name='B-pull-resumed-upload';Expect='interrupted-upload'},
 @{Peer='B';Name='B-final-small';Write='finished';Expect='finished'},
 @{Peer='A';Name='A-final-small';Expect='finished'}
)}
$results=@();$evidence=Join-Path $projectRoot 'docs/evidence'
foreach($step in $steps){
 $peer=$step.Peer;$profile=Join-Path $Lab "$peer/p";$app=Join-Path $Lab "$peer/app";$result=Join-Path $profile 'save-result.json';if(Test-Path $result){Remove-Item -LiteralPath $result}
 $data=Join-Path $profile 'ExtensionsData/8ca18482-3ebe-4bf7-b229-a259b2094767'
 $options=Get-Content "$data/onboarding.json" -Raw|ConvertFrom-Json;$options.SyncPaused=$true;$options|ConvertTo-Json|Set-Content "$data/onboarding.json"
 $step|ConvertTo-Json|Set-Content "$profile/save-scenario.json"
 $exe=Join-Path $app $(if($peer-eq 'B'){'Playnite.FullscreenApp.exe'}else{'Playnite.DesktopApp.exe'})
 $proc=Start-Process $exe -ArgumentList @('--masterinstance','--nolibupdate','--hidesplashscreen','--userdatadir',('"'+$profile+'"')) -WindowStyle Hidden -PassThru
 $deadline=[DateTime]::UtcNow.AddMinutes(5);while(!(Test-Path $result)-and [DateTime]::UtcNow-lt $deadline){Start-Sleep -Milliseconds 300};if(!(Test-Path $result)){throw "Timed out: $($step.Name)"}
 $r=Get-Content $result -Raw|ConvertFrom-Json;Copy-Item $result "$evidence/$EvidencePrefix-$($step.Name).json" -Force
 Get-ChildItem $profile -Filter 'conflict-*.png'|ForEach-Object{Copy-Item $_.FullName "$evidence/$EvidencePrefix-$($_.Name)" -Force}
 $results+=$r;$results|ConvertTo-Json -Depth 20|Set-Content "$evidence/$EvidencePrefix-tally.json"
 Write-Output ($r|ConvertTo-Json -Compress);if(!$r.passed){throw "Failed: $($step.Name)"};if(!$proc.WaitForExit(15000)){throw 'Test client did not close'}
}
Write-Output "PASS $($results.Count) save scenarios"
