param([string]$ProfilePath = "$env:APPDATA\Playnite", [string]$OutputDirectory = "$env:USERPROFILE\Downloads")
$ErrorActionPreference = 'Stop'
# Read-only allowlist. Never include settings/config files, browser cookies,
# credentials, save files, library records, notes, or complete raw log lines.
function Read-SharedText([string]$Path) {
    $stream = [IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try { if($stream.Length -gt 33554432){throw 'Diagnostic input exceeds 32 MiB'}; $reader=[IO.StreamReader]::new($stream); try{$reader.ReadToEnd()}finally{$reader.Dispose()} } finally {$stream.Dispose()}
}
function Read-Json([string]$Path) { if(Test-Path -LiteralPath $Path){try{return (Read-SharedText $Path | ConvertFrom-Json)}catch{return $null}} }
$profile=[IO.Path]::GetFullPath($ProfilePath)
$data=Join-Path $profile 'ExtensionsData\8ca18482-3ebe-4bf7-b229-a259b2094767'
if(!(Test-Path -LiteralPath $data)){throw 'Link data not found. Supply -ProfilePath with the active Playnite profile folder.'}
$stamp=[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$folder=Join-Path $OutputDirectory ("PlayniteLink-diagnostics-$stamp-"+[Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$profiles=@($data)
$profiles+=@(Get-ChildItem -LiteralPath (Join-Path $data 'vault-profiles') -Directory -ErrorAction SilentlyContinue | ForEach-Object FullName)
$result=[ordered]@{Schema=1;CollectedUtc=[DateTime]::UtcNow.ToString('O');TimeZone=[TimeZoneInfo]::Local.Id;PowerShell=$PSVersionTable.PSVersion.ToString();Profiles=@();Extensions=@();Processes=@();Events=@();Privacy='Allowlisted summaries only; no credentials, save contents, names, account identifiers or raw log lines.'}
$index=0
foreach($root in $profiles){
    $index++
    $state=Read-Json (Join-Path $root 'state.json');$onboarding=Read-Json (Join-Path $root 'onboarding.json');$preferences=Read-Json (Join-Path $root 'device-settings.json')
    $games=@()
    if($state){foreach($map in $state.Mappings.PSObject.Properties){
        $key=$map.Value.Key;$entries=@($state.Entries | Where-Object Game -eq $key)
        $games+= [ordered]@{CloudGameKey=$key;Provider=if($map.Value.Source -like 'manual:*'){'manual'}else{'store'};Expected=$map.Value.Expected;Intended=$map.Value.Intended;Sessions=@($entries | Where-Object Kind -ne 'baseline' | Select-Object Kind,Device,Seconds,StartedUtc,EndedUtc);Baselines=@($entries | Where-Object Kind -eq 'baseline' | Select-Object Seconds,Count);Active=($null -ne $state.Active.PSObject.Properties[$map.Name])}
    }}
    $result.Profiles+= [ordered]@{Index=$index;StateReadable=($null -ne $state);Device=$state.Device;SyncPaused=$onboarding.SyncPaused;SetupComplete=$onboarding.SetupComplete;AutomaticGames=$onboarding.AutomaticGames;OnlineHltb=$onboarding.OnlineHltb;SyncAchievements=$onboarding.SyncAchievements;AchievementCatalogs=@(Get-ChildItem -LiteralPath (Join-Path $root 'achievement-catalogs') -Filter '*.json' -File -ErrorAction SilentlyContinue).Count;AchievementCustomizationObjects=@(Get-ChildItem -LiteralPath (Join-Path $root 'achievement-customization-vault\vault-objects') -Filter '*.json' -File -ErrorAction SilentlyContinue).Count;AchievementObjects=@(Get-ChildItem -LiteralPath (Join-Path $root 'achievement-objects') -Filter '*.json' -File -ErrorAction SilentlyContinue).Count;SyncPlaytime=$preferences.SyncPlaytime;SyncSaves=$preferences.SyncSaves;AutoSync=$preferences.AutoSync;Games=$games;ReceiptObjects=@(Get-ChildItem -LiteralPath (Join-Path $root 'sync-receipts') -Filter '*.json' -File -Recurse -ErrorAction SilentlyContinue).Count;HltbReceiptsPresent=(Test-Path -LiteralPath (Join-Path $root 'hltb-progress.json'))}
}
foreach($extension in Get-ChildItem -LiteralPath (Join-Path $profile 'Extensions') -Directory -ErrorAction SilentlyContinue){
    $manifest=Join-Path $extension.FullName 'extension.yaml';if(!(Test-Path -LiteralPath $manifest)){continue}
    $text=Read-SharedText $manifest
    if($text -notmatch '(?i)PlayniteLink|HowLongToBeat|GameActivity|SaveManager'){continue}
    $version=[regex]::Match($text,'(?m)^Version:\s*(.+)$').Groups[1].Value.Trim()
    $type=if($text -match '8ca18482-3ebe-4bf7-b229-a259b2094767'){'Link'}elseif($text -match '(?i)HowLongToBeat'){'HLTB'}elseif($text -match '(?i)GameActivity'){'GameActivity'}else{'SaveManager'}
    $dll=Join-Path $extension.FullName 'PlayniteLink.dll'
    $engineDll=Join-Path $extension.FullName 'AchievementEngine\PlayniteAchievements.dll'
    $result.Extensions+=@{Type=$type;Version=$version;BuiltInAchievements=(Test-Path -LiteralPath $engineDll);AchievementEngineSha256=if(Test-Path -LiteralPath $engineDll){(Get-FileHash -LiteralPath $engineDll -Algorithm SHA256).Hash}else{$null};DllSha256=if(Test-Path -LiteralPath $dll){(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash}else{$null}}
}
$result.Processes=@(Get-Process -ErrorAction SilentlyContinue | Where-Object ProcessName -match '^(Playnite\.(DesktopApp|FullscreenApp)|Bloodborne|bb-probe|shadPS4)$' | Select-Object ProcessName,Id,StartTime)
# Event categories retain timing and ordering without leaking arbitrary exception
# messages (which can contain remote paths, names, URLs or access tokens).
$categories=[ordered]@{SessionRecorded='recorded session';SessionStarted='session started';SessionUnlinked='session not tracked';SessionStart='pending session|unfinished session';PlaytimeEdit='Playtime changed outside|Expected .+found';Identity='Provider identity changed';SaveUnavailable='Save capture deferred|save.*unavailable|Could not find.*save';CloudTimeout='timed out|timeout|deadline';CloudFailure='CloudTransferException|Rclone.*failed|Could not sync';Device='Device (removed|paused)|Device sync unavailable';Hltb='HLTB.*deferred|HLTB.*queued|HLTB.*sign.in';Activity='GameActivity|Optional session display';Achievements='Achievement.*deferred|Achievement.*pending|Built-in achievement.*failed';BackgroundBackup='Background backup compatibility';Sync='Combining playtime|Sync completed|Up to date'}
foreach($log in @(Get-ChildItem -LiteralPath $profile -File -Filter '*.log*' -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 3)){
    try{$lines=(Read-SharedText $log.FullName) -split '\r?\n' | Select-Object -Last 12000}catch{continue}
    foreach($line in $lines){foreach($category in $categories.Keys){if($line -match $categories[$category]){
        $time=[regex]::Match($line,'^\d{2,4}[-/]\d{2}[-/]\d{2,4}[ T]\d{2}:\d{2}:\d{2}(?:\.\d+)?').Value
        $result.Events+=@{Time=$time;Category=$category;Seconds=if($category -eq 'SessionRecorded'){[regex]::Match($line,'recorded session:\s*(\d+)').Groups[1].Value}else{$null}}
        break
    }}}
}
$result.Events=@($result.Events | Select-Object -Last 600)
$json=Join-Path $folder 'diagnostics.json';$result | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $json -Encoding UTF8
$zip=$folder+'.zip';Compress-Archive -LiteralPath $json -DestinationPath $zip
Write-Output "Read-only diagnostic report: $json"
Write-Output "Attach this archive: $zip"
