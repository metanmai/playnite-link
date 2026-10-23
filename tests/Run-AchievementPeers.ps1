param([string]$BuildDirectory,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
if(!$BuildDirectory -or !$OutputDirectory){throw 'Supply a tested build and a new local evidence directory'}
if(Test-Path -LiteralPath $OutputDirectory){throw 'Choose a fresh evidence directory'}
New-Item -ItemType Directory -Path $OutputDirectory|Out-Null
$cloud=Join-Path $OutputDirectory 'cloud'
$results=@();$author=$null
foreach($phase in @('author','receive')){
 $output=& (Join-Path $PSScriptRoot 'Run-HostProbe.ps1') -SkipBuild -BuildDirectory $BuildDirectory -AchievementPeer -AchievementPhase $phase -SharedAchievementCloud $cloud
 $output|Write-Output
 $line=$output|Where-Object{$_ -like 'Isolated lab retained at *'}|Select-Object -Last 1
 if(!$line){throw 'Peer profile was not returned'}
 $lab=$line.Substring('Isolated lab retained at '.Length);$profile=Join-Path $lab 'profile'
 $result=Get-Content (Join-Path $profile 'host-result.json') -Raw|ConvertFrom-Json
 if(!$result.passed){throw 'Peer failed'}
 Copy-Item (Join-Path $profile 'host-result.json') (Join-Path $OutputDirectory ($phase+'.json'))
 $results+= $result|Select-Object passed,phase,checks,localGameId,cloudGameKey,device,engineBuiltIn
 if($phase -eq 'author'){$author=$lab}
}
$profile=Join-Path $author 'profile';$resultPath=Join-Path $profile 'host-result.json'
Move-Item -LiteralPath $resultPath -Destination ($resultPath+'.author')
'verify'|Set-Content (Join-Path $profile 'achievement-peer-phase.txt')
$process=Start-Process (Join-Path $author 'app/Playnite.DesktopApp.exe') -ArgumentList @('--masterinstance','--nolibupdate','--hidesplashscreen','--userdatadir',('"'+$profile+'"')) -WindowStyle Hidden -PassThru
$deadline=[DateTime]::UtcNow.AddSeconds(240)
while(!(Test-Path -LiteralPath $resultPath) -and !$process.HasExited -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 500}
if(!$process.HasExited -and !$process.WaitForExit(15000)){[void]$process.CloseMainWindow();if(!$process.WaitForExit(15000)){throw 'Peer did not close cleanly'}}
if(!(Test-Path -LiteralPath $resultPath)){throw 'Returning peer produced no result'}
$result=Get-Content -LiteralPath $resultPath -Raw|ConvertFrom-Json
if(!$result.passed){throw ($result|ConvertTo-Json)}
Copy-Item -LiteralPath $resultPath -Destination (Join-Path $OutputDirectory 'verify.json')
$results+=$result|Select-Object passed,phase,checks,localGameId,cloudGameKey,device,engineBuiltIn
$results|ConvertTo-Json|Set-Content (Join-Path $OutputDirectory 'results.json')
$results|ConvertTo-Json|Write-Output
