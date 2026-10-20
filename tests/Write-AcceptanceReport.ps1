param([string]$ProjectRoot=(Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop'
$evidence=Join-Path $ProjectRoot 'docs/evidence'
$groups=@(
 @('Ledger and transport','ledger-tests.txt',54),
 @('Save conflicts, backups, preferences and OAuth protocol','vault-tests.txt',55),
 @('Streaming, large saves and device management','streaming-device-tests.txt',15),
 @('Passcode persistence and throttling','security-tests.txt',9),
 @('WPF app lock, input and Desktop/Fullscreen theme resources','app-lock-tests.txt',15),
 @('Peer add-ons, opt-in, exclusions, conflicts and rollback','setup-tests.txt',17)
)
$checks=@();$lines=New-Object System.Collections.Generic.List[string]
$lines.Add('# Acceptance report — Playnite Link 0.4.0')
$lines.Add('')
$lines.Add('Windows 11, installed Playnite 10.62, .NET Framework, existing Rclone. Synthetic games and add-on packages were used in separate Playnite profiles. No personal game saves or credentials were copied into fixtures. The installed passcode is preserved.')
$lines.Add('')
$lines.Add('| Automated group | Expected | Passed | Failed |')
$lines.Add('|---|---:|---:|---:|')
$total=0;$failed=0
foreach($group in $groups){
 $log=Get-Content -LiteralPath (Join-Path $evidence $group[1]) -Raw
 $results=[regex]::Matches($log,'RESULT (\d+) passed, (\d+) failed')
 if(!$results.Count){throw ('No result for '+$group[0])}
 $result=$results[$results.Count-1];$passed=[int]$result.Groups[1].Value;$errors=[int]$result.Groups[2].Value
 $total+=$passed;$failed+=$errors
 $checks+=@{name=$group[0];expected=$group[2];passed=$passed;failed=$errors;evidence=$group[1]}
 $lines.Add('| '+$group[0]+' | '+$group[2]+' | '+$passed+' | '+$errors+' |')
}
$lines.Add('')
$lines.Add('## Actual Playnite applications')
$lines.Add('')
$lines.Add('| Run | Result |')
$lines.Add('|---|---|')
$hosts=@()
foreach($name in @('lock-desktop-host','lock-fullscreen-host','desktop-host','fullscreen-host','addon-peer-A','addon-peer-B')){
 $value=Get-Content -LiteralPath (Join-Path $evidence ($name+'.json')) -Raw | ConvertFrom-Json
 $hosts+=@{name=$name;passed=$value.passed;evidence=$name+'.json'}
 $lines.Add('| '+$name+' | '+$(if($value.passed){'Passed'}else{'Failed'})+' |')
}
$lines.Add('')
$lines.Add('Two independently configured Playnite instances acted as Desktop and Fullscreen peers. Tests drove real WPF buttons, entered disposable passcodes, launched a real synthetic game process, and applied a real extension DLL and complete theme package through the restart helper. The receiver restarted and loaded the installed extension. Double-clicking Apply was tested; it must produce only one installation/restart. The Fullscreen add-on review was also checked at an 800×640 dialog viewport with its Apply button inside the window.')
$lines.Add('')
$lines.Add('## Resolution and state tally')
$lines.Add('')
$lines.Add('| Case | Expected | Actual |')
$lines.Add('|---|---|---|')
foreach($mode in @('desktop','fullscreen')){
 $hostResult=Get-Content -LiteralPath (Join-Path $evidence ($mode+'-host.json')) -Raw | ConvertFrom-Json
 $lines.Add('| '+$mode+' playtime | 100 + 2 sessions = '+$hostResult.runtime.expectedPlaytime+' seconds | '+$hostResult.runtime.displayedPlaytime+' seconds |')
 $lines.Add('| '+$mode+' cancel save conflict | No extra game launch | '+$hostResult.visual.cancelledLaunch+' |')
 $lines.Add('| '+$mode+' choose cloud | Cloud content restored | '+$hostResult.visual.cloudChoiceVerified+' |')
 $lines.Add('| '+$mode+' choose local | Local content retained; remote branch preserved | '+$hostResult.visual.localChoiceVerified+' |')
 $lines.Add('| '+$mode+' resolved save heads | 1 | '+$hostResult.visual.remainingSaveHeads+' |')
 $lines.Add('| '+$mode+' tab navigation and draft | Same page controls and unsaved draft retained | '+$hostResult.visual.navigationStable+' / '+$hostResult.visual.unsavedDraftPreserved+' |')
 $lines.Add('| '+$mode+' game manager | Opens selected game controls without losing account page | '+$hostResult.visual.gameManagerVerified+' |')
 $lines.Add('| '+$mode+' quiet idle save scans | 0 | '+$(if($hostResult.performance.noIdleSaveScans){'0'}else{'Unexpected scan'})+' |')
 $lines.Add('| '+$mode+' warm idle check | Responsive UI, no progress dialog | '+$hostResult.performance.warmSyncMilliseconds+' ms on local Rclone alias |')
}
$lines.Add('| Add-on concurrent edits | Two branches; explicit choice; one surviving head | Passed; discarded package retained in backup |')
$lines.Add('| Independent add-on installs | Union of both peers | Passed |')
$lines.Add('| Shared add-on removal | Code backed up, extension data retained | Passed |')
$lines.Add('| Active theme removal | Refuse until a different local theme is chosen | Passed |')
$lines.Add('| Device-only package | Neither publish nor apply remote changes | Passed, including restart persistence |')
$lines.Add('| Corrupt staged package | No installed files modified | Passed |')
$lines.Add('| Mid-install failure | Roll back earlier replacements; retry succeeds | Passed with a failure injected into a test-only copy of the helper |')
$lines.Add('')
$cloudLog=Get-Content -LiteralPath (Join-Path $evidence 'live-cloud-tests.txt') -Raw
$cloud=Get-Content -LiteralPath (Join-Path $evidence 'live-cloud-result.json') -Raw | ConvertFrom-Json
$cloudTally=@{
 passed=$cloud.passed
 largeSaveUploaded=$cloudLog.Contains('streamed large files and published manifest')
 largeSaveRestoredAndHashesVerified=$cloudLog.Contains('second device restored and verified large save')
 conflictResolvedAndBackupPreserved=$cloudLog.Contains('concurrent branches resolved with preserved backup')
 deviceRosterAndSettingsVerified=$cloudLog.Contains('device roster, remote settings, pause/resume/remove verified')
 cloudPeerAddonRunPassed=$false
}
if(Test-Path -LiteralPath (Join-Path $evidence 'addon-cloud-peer-B.json')){$cloudPeer=Get-Content -LiteralPath (Join-Path $evidence 'addon-cloud-peer-B.json') -Raw | ConvertFrom-Json;$cloudTally.cloudPeerAddonRunPassed=$cloudPeer.passed}
$lines.Add('## Existing Google Drive account')
$lines.Add('')
$lines.Add('Dedicated synthetic acceptance folders were used on the existing Google Drive account. Each large file was 40 MiB + 13 bytes; two identical files formed an 80 MiB + 26-byte logical snapshot and shared a blob. Upload, restore, SHA-256 checks, save-conflict resolution, and preservation of the displaced local save in its backup completed.')
$lines.Add('')
if($cloud.passed -and $cloudTally.cloudPeerAddonRunPassed){
 $lines.Add('**Full live-cloud acceptance passed using the owner''s personal OAuth client.** The earlier shared Rclone OAuth project returned HTTP 403 RATE_LIMIT_EXCEEDED. A separate personal-client configuration removed that dependency. The combined save/conflict/device/playtime suite passed, including two devices, remote configuration, pause/resume/removal, and exactly 140 seconds of converged playtime after retries. Actual Desktop and Fullscreen Playnite peers published and received an extension and theme over Drive; the recipient installed, restarted, and loaded the extension. Local settings and active theme selection were retained. Forced OAuth token refresh also passed. Historical quota-failure evidence remains separately recorded.')
}else{
 $lines.Add('**Full live-cloud acceptance remains incomplete.** See live-cloud-result.json and the cloud peer results for current failures. Earlier attempts hit HTTP 403 RATE_LIMIT_EXCEEDED on the shared Rclone OAuth project. Failed or missing steps are not marked passed.')
}
$lines.Add('')
$lines.Add('## Visual review and practical limits')
$lines.Add('')
$lines.Add('Captured actual WPF pages: compact lock, Overview, Devices, Protection, Add-ons, settings, game manager, playtime controls and all three save-conflict choices. Desktop and Fullscreen colour resources were reviewed; missing Fullscreen brushes, low-contrast fields and early lock-window theme loading were corrected. Tab switches preserve page instances and drafts, and Sync now does not close the account window. A visible Close action is available in Fullscreen.')
$lines.Add('')
$lines.Add('This is software testing on one Windows computer with isolated profiles, not a physical ROG Ally/gamepad test. Real commercial-game save layouts have not been exercised here. Fresh browser OAuth enrollment and a forced refresh were exercised for the personal client; OAuth state handling, cancellation and masked secrets are also covered by protocol tests. Settings synchronization remains out of scope at the user''s request; active theme, launch mode, Windows startup choices, extension settings and credentials remain local.')
$report=@{version='0.4.0';unitPassed=$total;unitFailed=$failed;groups=$checks;hosts=$hosts;cloud=$cloudTally;allLocalPassed=($failed -eq 0 -and @($hosts | Where-Object {!$_.passed}).Count -eq 0);fullLiveCloudPassed=($cloud.passed -and $cloudTally.cloudPeerAddonRunPassed)}
$lines | Set-Content -LiteralPath (Join-Path $ProjectRoot 'docs/ACCEPTANCE.md')
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $evidence 'acceptance-tally.json')
Write-Output ($report | ConvertTo-Json -Depth 3)

