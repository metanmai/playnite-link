param([string]$OutputDirectory='C:\PL064qa\retirement-tests')
$ErrorActionPreference='Stop'
$helper=Join-Path (Split-Path $PSScriptRoot -Parent) 'src/RetireAchievementAddon.ps1'
foreach($case in @('verified','changed-module','outside-destination','outside-source')){
 $root=Join-Path $OutputDirectory $case;$extensions=Join-Path $root 'Extensions';$source=Join-Path $extensions 'PlayniteAchievements';$recovery=Join-Path $root 'engine-recovery';$destination=Join-Path $recovery 'packages\preserved';New-Item -ItemType Directory -Force $source,$recovery|Out-Null
 $module=Join-Path $source 'PlayniteAchievements.dll';Set-Content -LiteralPath $module 'fixture';$expected=(Get-FileHash -LiteralPath $module).Hash
 $data=Join-Path $root 'user-data.txt';Set-Content -LiteralPath $data 'Preserve credentials and achievements';$before=(Get-FileHash -LiteralPath $data).Hash
 $request=Join-Path $recovery 'retire-fixture.json';$sourceArg=if($case -eq 'outside-source'){$root}else{$source};$destinationArg=if($case -eq 'outside-destination'){Join-Path $root 'outside-recovery'}else{$destination}
 @{Source=$sourceArg;Extensions=$extensions;Destination=$destinationArg;Process=2147483647;Files=@(@{Name='PlayniteAchievements.dll';Hash=$expected})}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath $request
 if($case -eq 'changed-module'){Set-Content -LiteralPath $module 'changed'}
 & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $helper -Request $request *> (Join-Path $root 'result.log');$code=$LASTEXITCODE
 if($case -eq 'verified'){if($code -ne 0 -or (Test-Path -LiteralPath $source) -or !(Test-Path -LiteralPath (Join-Path $destination 'PlayniteAchievements.dll'))){throw 'Verified archive failed'}}else{if($code -eq 0 -or !(Test-Path -LiteralPath $source)){throw "Unsafe archive did not fail closed: $case"}}
 if((Get-FileHash -LiteralPath $data).Hash -ne $before){throw 'User achievement data changed'}
 Write-Output "PASS achievement retirement: $case"
}
