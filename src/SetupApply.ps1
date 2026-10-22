param([Parameter(Mandatory=$true)][string]$Plan,[int]$ParentId=0,[string]$RestartExe='',[string]$RestartProfile='',[switch]$RestartMaster,[switch]$RecoverOnly)
$ErrorActionPreference='Stop'
$planPath=[IO.Path]::GetFullPath($Plan)
$pending=Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json
$profile=[IO.Path]::GetFullPath($pending.Profile)
$data=[IO.Path]::GetFullPath($pending.DataDirectory)
$stageRoot=[IO.Path]::GetFullPath((Join-Path $data 'staged'))+[IO.Path]::DirectorySeparatorChar
function Assert-NoLink([string]$path) {
 for($current=$path;$current;$current=[IO.Path]::GetDirectoryName($current)){
  if((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked package paths are not supported.'}
 }
}
function Assert-Files([string]$root,$expected) {
 Assert-NoLink $root
 $actual=if(Test-Path -LiteralPath $root){@(Get-ChildItem -LiteralPath $root -Recurse -Force -File)}else{@()}
 if($actual.Count -ne @($expected.PSObject.Properties).Count){throw 'Package changed after review; no changes applied.'}
 foreach($file in $actual){
  Assert-NoLink $file.FullName
  $relative=$file.FullName.Substring($root.Length+1).Replace('\','/')
  $property=$expected.PSObject.Properties[$relative]
  if(!$property -or $property.Value -notmatch '^blob:([a-f0-9]{64}):([0-9]+)$'){throw 'Unexpected package file.'}
  $hash=$Matches[1];$length=[long]$Matches[2]
  $stream=[IO.File]::OpenRead($file.FullName);$sha=[Security.Cryptography.SHA256]::Create()
  try{$actualHash=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose();$stream.Dispose()}
  if($file.Length -ne $length -or $actualHash -ne $hash){throw 'Package contents changed after review.'}
 }
}
$backup=Join-Path $data ('backups/'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
$moved=New-Object System.Collections.Generic.List[object]
$success=$false
$lease=$null
$leaseAttempted=$false
$journal=Join-Path $data 'apply-journal.json'
function Write-Durable([string]$path,[string]$text) {
 $temp=$path+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
 $bytes=(New-Object Text.UTF8Encoding($false)).GetBytes($text)
 $stream=New-Object IO.FileStream($temp,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
 try{$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
 [IO.File]::Move($temp,$path)
}
function Recover-Apply {
 Assert-NoLink $journal
 $transaction=Get-Content -LiteralPath $journal -Raw | ConvertFrom-Json
 $savedRoot=[IO.Path]::GetFullPath($transaction.Backup)
 $allowed=[IO.Path]::GetFullPath((Join-Path $data 'backups'))+[IO.Path]::DirectorySeparatorChar
 if(!$savedRoot.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid recovery backup path.'}
 Assert-NoLink $savedRoot
 $seen=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
 $indices=New-Object 'System.Collections.Generic.HashSet[string]'
 if(@($transaction.Records).Count -eq 0){throw 'Empty recovery journal.'}
 foreach($record in $transaction.Records){
  if($record.Relative -notmatch '^(Extensions/|Themes/(Desktop|Fullscreen)/)[A-Za-z0-9_(][A-Za-z0-9_. ()-]{0,179}$' -or $record.Relative -match '(?i)(PlayniteLink|_Builtin$)' -or $record.Relative.EndsWith('.') -or $record.Relative.EndsWith(' ') -or !$seen.Add($record.Relative)){throw 'Invalid recovery destination.'}
  $target=[IO.Path]::GetFullPath((Join-Path $profile $record.Relative));Assert-NoLink $target
  if($record.Index -notmatch '^[0-9]+$' -or !$indices.Add([string]$record.Index)){throw 'Invalid recovery index.'}
  Assert-NoLink (Join-Path $savedRoot ([string]$record.Index))
  $stage=[IO.Path]::GetFullPath($record.Stage)
  if(!$stage.StartsWith($stageRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid recovery stage.'}
  Assert-NoLink $stage
 }
 $receipt=Join-Path $data 'applied.json';Assert-NoLink $receipt
 if((Test-Path -LiteralPath $receipt) -and (Get-FileHash -LiteralPath $receipt -Algorithm SHA256).Hash -eq $transaction.ReceiptHash){[IO.File]::Delete($journal);return}
 foreach($record in @($transaction.Records)[($transaction.Records.Count-1)..0]){
  $target=[IO.Path]::GetFullPath((Join-Path $profile $record.Relative));$saved=Join-Path $savedRoot ([string]$record.Index)
  if($record.HadOriginal -and !(Test-Path -LiteralPath $saved) -and !(Test-Path -LiteralPath $target)){throw 'Original installation and backup are both missing; recovery journal preserved.'}
  if((Test-Path -LiteralPath $saved) -or (!$record.HadOriginal -and !(Test-Path -LiteralPath $record.Stage))){
   if(Test-Path -LiteralPath $target){Move-Item -LiteralPath $target -Destination ($saved+'-not-applied-'+[Guid]::NewGuid().ToString('N'))}
   if($record.HadOriginal -and (Test-Path -LiteralPath $saved)){Move-Item -LiteralPath $saved -Destination $target}
  }
 }
 [IO.File]::Delete($journal)
}
try {
 Assert-NoLink $profile;Assert-NoLink $data
 Assert-NoLink $journal;Assert-NoLink (Join-Path $data 'applied.json')
 if(!$data.StartsWith($profile+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Setup state must be inside this Playnite profile.'}
 if($ParentId -gt 0){
  $parent=Get-Process -Id $ParentId -ErrorAction SilentlyContinue
  if($parent){if($RestartExe -and $parent.Path -ne $RestartExe){throw 'Unexpected parent application.'};if(!$parent.WaitForExit(120000)){throw 'Playnite has not exited; packages were not changed.'}}
 }
 $leasePath=Join-Path $data 'apply.lock';Assert-NoLink $leasePath
 $leaseAttempted=$true
 $lease=New-Object IO.FileStream($leasePath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
 if(Test-Path -LiteralPath $journal){Recover-Apply;$success=$true;exit 0}
 if($RecoverOnly){$success=$true;exit 0}
 if(Test-Path -LiteralPath (Join-Path $data 'applied.json')){throw 'Previous install receipt must be processed before another installation.'}
 $targets=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
 foreach($item in $pending.Installs){
  if($item.Key -notmatch '^[a-f0-9]{64}$'){throw 'Invalid package key.'}
  if($item.Target -notmatch '^(Extensions/|Themes/(Desktop|Fullscreen)/)[A-Za-z0-9_(][A-Za-z0-9_. ()-]{0,179}$' -or $item.Target -match '(?i)(PlayniteLink|_Builtin$)' -or $item.Target.EndsWith('.') -or $item.Target.EndsWith(' ')){throw 'Unsafe package destination.'}
  if(!$targets.Add($item.Target)){throw 'Two packages target the same directory.'}
  $target=[IO.Path]::GetFullPath((Join-Path $profile $item.Target))
  if(!$target.StartsWith($profile+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Package destination escaped profile.'}
  $manifest=Join-Path $target 'extension.yaml'
  if((Test-Path -LiteralPath $manifest) -and (Select-String -LiteralPath $manifest -SimpleMatch '8ca18482-3ebe-4bf7-b229-a259b2094767' -Quiet)){throw 'The running sync extension cannot replace itself.'}
  Assert-Files $target $item.Expected
  if(!$item.Removed){
   $stage=[IO.Path]::GetFullPath($item.Stage)
   if(!$stage.StartsWith($stageRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid staging directory.'}
   Assert-Files $stage $item.Desired
  }
 }
 New-Item -ItemType Directory -Path $backup -Force | Out-Null
 $receiptContent=$pending | ConvertTo-Json -Depth 100
 $sha=[Security.Cryptography.SHA256]::Create()
 try{$receiptHash=[BitConverter]::ToString($sha.ComputeHash((New-Object Text.UTF8Encoding($false)).GetBytes($receiptContent))).Replace('-','')}finally{$sha.Dispose()}
 $records=@();$index=0
 foreach($item in $pending.Installs){$records+=@{Relative=$item.Target;Index=[string]$index;Stage=if($item.Removed){Join-Path $stageRoot ('removed-'+$index)}else{$item.Stage};HadOriginal=(Test-Path -LiteralPath (Join-Path $profile $item.Target))};$index++}
 Write-Durable $journal (@{Backup=$backup;Records=$records;ReceiptHash=$receiptHash}|ConvertTo-Json -Depth 100)
 foreach($item in $pending.Installs){
  $target=[IO.Path]::GetFullPath((Join-Path $profile $item.Target));$saved=Join-Path $backup $moved.Count.ToString()
  $record=[PSCustomObject]@{Target=$target;Backup=$saved;HadOriginal=(Test-Path -LiteralPath $target);Installed=$false}
  if($record.HadOriginal){Move-Item -LiteralPath $target -Destination $saved}
  $moved.Add($record)
  if(!$item.Removed){New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null;Move-Item -LiteralPath $item.Stage -Destination $target;$record.Installed=$true}
 }
 if(Test-Path -LiteralPath (Join-Path $data "apply-error.txt")){Move-Item -LiteralPath (Join-Path $data "apply-error.txt") -Destination (Join-Path $backup "previous-error.txt")}
 $receipt=Join-Path $data 'applied.json'
 Write-Durable $receipt $receiptContent
 [IO.File]::Delete($journal)
 $success=$true
} catch {
 $reason=$_.Exception.Message
 if($lease -and (Test-Path -LiteralPath $journal)){try{Recover-Apply;$moved.Clear()}catch{$reason+=' Recovery requires another restart: '+$_.Exception.Message;$moved.Clear()}}
 for($i=$moved.Count-1;$i -ge 0;$i--){
  $record=$moved[$i]
  # Move replacements aside rather than deleting anything during rollback.
  if($record.Installed -and (Test-Path -LiteralPath $record.Target)){Move-Item -LiteralPath $record.Target -Destination ($record.Backup+'-not-applied')}
  if($record.HadOriginal -and (Test-Path -LiteralPath $record.Backup)){Move-Item -LiteralPath $record.Backup -Destination $record.Target}
 }
 [IO.File]::WriteAllText((Join-Path $data 'apply-error.txt'),$reason)
} finally {
 if($lease){$lease.Dispose()}
 if($RestartExe -and (!$leaseAttempted -or $lease)){$arguments=@('--nolibupdate','--hidesplashscreen');if($RestartMaster){$arguments+='--masterinstance'};if($RestartProfile){$arguments+=@('--userdatadir',('"'+$RestartProfile+'"'))};Start-Process -FilePath $RestartExe -ArgumentList $arguments -WindowStyle Hidden | Out-Null}
}
if(!$success){exit 1}

