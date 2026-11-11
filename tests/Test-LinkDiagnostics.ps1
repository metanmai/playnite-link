param([string]$OutputDirectory='C:\PL062qa\diagnostic-test')
$ErrorActionPreference='Stop'
$profile=Join-Path $OutputDirectory 'profile';$data=Join-Path $profile 'ExtensionsData\8ca18482-3ebe-4bf7-b229-a259b2094767'
New-Item -ItemType Directory -Force $data | Out-Null
$secret='DO_NOT_EXPORT_CREDENTIAL_89db00'
$game='1'*64;$otherGame='2'*64;$device='3'*32;$peer='4'*32
@{
    Device=$device
    Mappings=@{local=@{Key=$game;Source="manual:$secret";Expected=15};another=@{Key=$otherGame;Source='manual:fixture';Expected=0}}
    Entries=@(
        @{Kind='session';Game=$game;Device=$device;Seconds=15;StartedUtc='2026-10-23T01:00:22.123Z';EndedUtc='2026-10-23T01:00:37.123Z';Notes=$secret}
        @{Kind='session';Game=$game;Device=$peer;Seconds=20;StartedUtc='2026-10-23T02:00:05Z';EndedUtc='2026-10-23T02:00:25Z'}
    )
    Active=@{}
} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $data 'state.json')
@{SyncPlaytime=$false;Name=$secret} | ConvertTo-Json | Set-Content (Join-Path $data 'device-settings.json')
Set-Content (Join-Path $data 'rclone-settings.json') $secret
Set-Content (Join-Path $profile 'extensions.log') "23-10-2026 12:00:22.000|ERROR|CloudTransferException token=$secret"
$collector=Join-Path (Split-Path $PSScriptRoot -Parent) 'src/Collect-LinkDiagnostics.ps1'
$aliases=@()
foreach($run in 'first','second'){
    $output=Join-Path $OutputDirectory $run
    & $collector -ProfilePath $profile -OutputDirectory $output
    $archive=Get-ChildItem -LiteralPath $output -File -Filter 'PlayniteLink-diagnostics-*.zip' | Select-Object -First 1
    $extracted=Join-Path $output 'extracted'
    Expand-Archive -LiteralPath $archive.FullName -DestinationPath $extracted
    $text=Get-Content (Join-Path $extracted 'diagnostics.json') -Raw
    $json=$text | ConvertFrom-Json
    foreach($canary in $secret,$game,$otherGame,$device,$peer,'01:00:22','01:00:37','12:00:22'){
        if($text.Contains($canary)){throw 'An identifier, credential or exact timestamp leaked'}
    }
    $fixture=$json.Profiles[0]
    if($fixture.SyncPlaytime -ne $false){throw 'Disabled playtime preference lost'}
    $games=@($fixture.Games)
    if($games.Count -ne 2 -or $games[0].GameAlias -eq $games[1].GameAlias){throw 'Game aliases missing or conflated'}
    $sessions=($games | Where-Object Expected -eq 15).Sessions
    if($sessions[0].Seconds -ne 15 -or $sessions[0].StartedUtc -ne '2026-10-23T01:00Z'){throw 'Session diagnostic missing'}
    if($sessions[0].DeviceAlias -ne $fixture.DeviceAlias -or $sessions[1].DeviceAlias -eq $fixture.DeviceAlias){throw 'Device relationships lost'}
    if($json.Events[0].Category -ne 'CloudFailure' -or $json.Events[0].Time -ne '23-10-2026 12:00'){throw 'Error timing/category missing'}
    if($json.Privacy -notmatch 'times are shown to the minute' -or $json.Privacy -notmatch 'recorded seconds remain exact'){throw 'Timing disclosure missing'}
    if(@(Get-ChildItem -LiteralPath $extracted -File).Count -ne 1){throw 'Unexpected diagnostic attachment'}
    $current=@($games.GameAlias)+@($sessions.DeviceAlias)
    foreach($alias in $current){if(!$alias -or $alias -in $aliases){throw 'Report aliases missing or reused'}}
    $aliases+=$current
}
Write-Output 'PASS diagnostic ZIPs: fresh aliases, preserved relationships and durations, minute precision, credential and identifier exclusion, file allowlist'
