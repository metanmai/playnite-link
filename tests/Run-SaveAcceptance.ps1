param([string]$CloudRemote='', [string]$RcloneConfig='', [string]$EvidencePrefix='saves-acceptance')
$ErrorActionPreference='Stop'
$lines=& (Join-Path $PSScriptRoot 'Run-GamePickerPeers.ps1') -CloudRemote $CloudRemote -RcloneConfig $RcloneConfig -EvidencePrefix ($EvidencePrefix+'-baseline')
$lines|Write-Output
$marker='Two activity-enabled Playnite profiles retained at '
$last=$lines|Where-Object{$_ -is [string] -and $_.StartsWith($marker)}|Select-Object -Last 1
if(!$last){throw 'No completed test fixture'}
$lab=$last.Substring($marker.Length)
& (Join-Path $PSScriptRoot 'Run-SaveConflictPeers.ps1') -Lab $lab -EvidencePrefix $EvidencePrefix
if($CloudRemote){& (Join-Path $PSScriptRoot 'Run-SaveConflictPeers.ps1') -Lab $lab -EvidencePrefix ($EvidencePrefix+'-transfer') -TransferOnly}else{& (Join-Path $PSScriptRoot 'Run-SaveConflictPeers.ps1') -Lab $lab -EvidencePrefix ($EvidencePrefix+'-extra') -ExtraOnly}
