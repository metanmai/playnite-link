param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $root){throw 'Use a new isolated output directory.'}
New-Item -ItemType Directory $root | Out-Null
$helper=Join-Path (Split-Path $PSScriptRoot -Parent) 'src/SetupApply.ps1'
$source=Get-Content -LiteralPath $helper -Raw
$restartSource=Join-Path $root 'RestartSentinel.cs';$restartExe=Join-Path $root 'RestartSentinel.exe'
'using System.IO;class RestartSentinel{static void Main(string[] a){for(int i=0;i<a.Length-1;i++)if(a[i]=="--userdatadir")File.WriteAllText(Path.Combine(a[i+1],"unexpected-restart"),"started");}}'|Set-Content -LiteralPath $restartSource
& "$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe" /nologo /target:exe ("/out:"+$restartExe) $restartSource
if($LASTEXITCODE -ne 0){throw 'Restart sentinel compile failed'}
$boundaries=@{
 journal='Write-Durable $journal (@{Backup=$backup;Records=$records;ReceiptHash=$receiptHash}|ConvertTo-Json -Depth 100)'
 backup='if($record.HadOriginal){Move-Item -LiteralPath $target -Destination $saved}'
 installed='if(!$item.Removed){New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null;Move-Item -LiteralPath $item.Stage -Destination $target;$record.Installed=$true}'
 receipt='Write-Durable $receipt $receiptContent'
}
function Manifest([string]$folder){
 $map=@{}
 if(Test-Path -LiteralPath $folder){foreach($file in Get-ChildItem -LiteralPath $folder -File){$map[$file.Name]='blob:'+(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()+':'+$file.Length}}
 return $map
}
$results=@()
foreach($point in @('journal','backup','installed','receipt')){
 $profile=Join-Path $root $point;$data=Join-Path $profile 'ExtensionsData/fixture/setup'
 New-Item -ItemType Directory -Force $data | Out-Null
 $items=@();$number=0
 foreach($name in @('Existing','Added','Removed')){
  $target=Join-Path $profile ('Extensions/'+$name);$stage=Join-Path $data ('staged/'+$name)
  if($name -ne 'Added'){New-Item -ItemType Directory -Force $target | Out-Null;Set-Content -LiteralPath (Join-Path $target 'asset.txt') -Value 'original'}
  New-Item -ItemType Directory -Force $stage | Out-Null;Set-Content -LiteralPath (Join-Path $stage 'asset.txt') -Value 'replacement'
  $items+=@{Key=([string]$number).PadLeft(64,'0');Target='Extensions/'+$name;Stage=$stage;Expected=(Manifest $target);Desired=(Manifest $stage);Removed=($name -eq 'Removed')};$number++
 }
 $plan=Join-Path $data 'pending.json';@{Profile=$profile;DataDirectory=$data;Installs=$items}|ConvertTo-Json -Depth 20|Set-Content -LiteralPath $plan
 $anchor=$boundaries[$point];if(!$source.Contains($anchor)){throw 'Fault injection anchor missing.'}
 # Kill the actual helper while suspended at a known durable boundary. No catch/finally runs.
 $fault=$source.Replace($anchor,($anchor+"`n [IO.File]::WriteAllText((Join-Path `$data 'ready'),'ready');while(`$true){Start-Sleep -Seconds 1}"))
 $injected=Join-Path $profile 'fault-helper.ps1';Set-Content -LiteralPath $injected -Value $fault
 $process=Start-Process powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$injected+'"'),'-Plan',('"'+$plan+'"')) -WindowStyle Hidden -PassThru
 $deadline=[DateTime]::UtcNow.AddSeconds(30);$ready=Join-Path $data 'ready'
 while(!(Test-Path -LiteralPath $ready) -and !$process.HasExited -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 50}
 if(!(Test-Path -LiteralPath $ready)){if(!$process.HasExited){$process.Kill()};throw ('Helper did not reach '+$point)}
 if($point -eq 'backup'){
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $helper -Plan $plan -RecoverOnly -RestartExe $restartExe -RestartProfile $profile
  Start-Sleep -Milliseconds 300
  if(Test-Path -LiteralPath (Join-Path $profile 'unexpected-restart')){$process.Kill();throw 'Contending helper restarted the host during an active transaction'}
  if($LASTEXITCODE -eq 0 -or !(Test-Path -LiteralPath (Join-Path $data 'apply-journal.json')) -or (Test-Path -LiteralPath (Join-Path $profile 'Extensions/Existing'))){$process.Kill();throw 'Concurrent helper did not preserve the active transaction'}
 }
 $process.Kill();$process.WaitForExit()
 for($restart=0;$restart -lt 2;$restart++){
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $helper -Plan $plan -RecoverOnly
  if($LASTEXITCODE -ne 0){throw 'Recovery failed'}
 }
 foreach($name in @('Existing','Added','Removed')){
  $path=Join-Path $profile ('Extensions/'+$name+'/asset.txt');$committed=$point -eq 'receipt'
  $exists=if($committed){$name -ne 'Removed'}else{$name -ne 'Added'}
  if((Test-Path -LiteralPath $path) -ne $exists){throw ('Incorrect recovered package presence: '+$point+'/'+$name)}
  if($exists -and (Get-Content -LiteralPath $path -Raw).Trim() -ne $(if($committed){'replacement'}else{'original'})){throw 'Recovered bytes differ'}
 }
 if(Test-Path -LiteralPath (Join-Path $data 'apply-journal.json')){throw 'Journal not finalized'}
 $results+=@{boundary=$point;passed=$true;processKilled=$true;restarts=2}
 Write-Output ('PASS actual helper kill and two recovery runs: '+$point)
}
$results|ConvertTo-Json|Set-Content (Join-Path $root 'results.json')
