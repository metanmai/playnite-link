param([string]$OutputDirectory='C:\PL062qa\diagnostic-test')
$ErrorActionPreference='Stop'
$profile=Join-Path $OutputDirectory 'profile';$data=Join-Path $profile 'ExtensionsData\8ca18482-3ebe-4bf7-b229-a259b2094767'
New-Item -ItemType Directory -Force $data | Out-Null
$secret='DO_NOT_EXPORT_CREDENTIAL_89db00'
@{Device='fixture';Mappings=@{local=@{Key='cloud-key';Source="manual:$secret";Expected=15}};Entries=@(@{Kind='session';Game='cloud-key';Device='fixture';Seconds=15;StartedUtc='2026-10-23T01:00:00Z';EndedUtc='2026-10-23T01:00:15Z';Notes=$secret});Active=@{}} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $data 'state.json')
@{SyncPlaytime=$false;Name=$secret} | ConvertTo-Json | Set-Content (Join-Path $data 'device-settings.json')
Set-Content (Join-Path $data 'rclone-settings.json') $secret
Set-Content (Join-Path $profile 'extensions.log') "23-10-2026 12:00:00.000|ERROR|CloudTransferException token=$secret"
& (Join-Path (Split-Path $PSScriptRoot -Parent) 'src/Collect-LinkDiagnostics.ps1') -ProfilePath $profile -OutputDirectory $OutputDirectory
$report=Get-ChildItem -LiteralPath $OutputDirectory -Directory -Filter 'PlayniteLink-diagnostics-*' | Select-Object -First 1
$text=Get-Content (Join-Path $report.FullName 'diagnostics.json') -Raw;$json=$text | ConvertFrom-Json
if($text.Contains($secret)){throw 'Credential canary leaked'}
if($json.Profiles[0].SyncPlaytime -ne $false){throw 'Disabled playtime preference lost'}
if($json.Profiles[0].Games[0].Sessions[0].Seconds -ne 15){throw 'Session diagnostic missing'}
if($json.Events[0].Category -ne 'CloudFailure'){throw 'Error timing/category missing'}
if(@(Get-ChildItem -LiteralPath $report.FullName -File).Count -ne 1){throw 'Unexpected diagnostic attachment'}
Write-Output 'PASS diagnostic collector: session visibility, disabled sync, failure category, credential canary exclusion, file allowlist'
