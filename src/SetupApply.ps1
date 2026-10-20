param([Parameter(Mandatory=$true)][string]$Plan,[int]$ParentId=0,[string]$RestartExe='',[string]$RestartProfile='',[switch]$RestartMaster)
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
try {
 Assert-NoLink $profile;Assert-NoLink $data
 if(!$data.StartsWith($profile+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Setup state must be inside this Playnite profile.'}
 if($ParentId -gt 0){
  $parent=Get-Process -Id $ParentId -ErrorAction SilentlyContinue
  if($parent){if($RestartExe -and $parent.Path -ne $RestartExe){throw 'Unexpected parent application.'};if(!$parent.WaitForExit(120000)){throw 'Playnite has not exited; packages were not changed.'}}
 }
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
 foreach($item in $pending.Installs){
  $target=[IO.Path]::GetFullPath((Join-Path $profile $item.Target));$saved=Join-Path $backup $moved.Count.ToString()
  $record=[PSCustomObject]@{Target=$target;Backup=$saved;HadOriginal=(Test-Path -LiteralPath $target);Installed=$false}
  if($record.HadOriginal){Move-Item -LiteralPath $target -Destination $saved}
  $moved.Add($record)
  if(!$item.Removed){New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null;Move-Item -LiteralPath $item.Stage -Destination $target;$record.Installed=$true}
 }
 if(Test-Path -LiteralPath (Join-Path $data "apply-error.txt")){Move-Item -LiteralPath (Join-Path $data "apply-error.txt") -Destination (Join-Path $backup "previous-error.txt")}
 $receipt=Join-Path $data 'applied.json';$receiptTemp=$receipt+'.'+[Guid]::NewGuid().ToString('N').Substring(0,8)+'.tmp'
 [IO.File]::WriteAllText($receiptTemp,($pending | ConvertTo-Json -Depth 100))
 [IO.File]::Move($receiptTemp,$receipt)
 $success=$true
} catch {
 $reason=$_.Exception.Message
 for($i=$moved.Count-1;$i -ge 0;$i--){
  $record=$moved[$i]
  # Move replacements aside rather than deleting anything during rollback.
  if($record.Installed -and (Test-Path -LiteralPath $record.Target)){Move-Item -LiteralPath $record.Target -Destination ($record.Backup+'-not-applied')}
  if($record.HadOriginal -and (Test-Path -LiteralPath $record.Backup)){Move-Item -LiteralPath $record.Backup -Destination $record.Target}
 }
 [IO.File]::WriteAllText((Join-Path $data 'apply-error.txt'),$reason)
} finally {
 if($RestartExe){$arguments=@('--nolibupdate','--hidesplashscreen');if($RestartMaster){$arguments+='--masterinstance'};if($RestartProfile){$arguments+=@('--userdatadir',('"'+$RestartProfile+'"'))};Start-Process -FilePath $RestartExe -ArgumentList $arguments -WindowStyle Hidden | Out-Null}
}
if(!$success){exit 1}

