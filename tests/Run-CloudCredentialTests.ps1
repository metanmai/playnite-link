param([Parameter(Mandatory=$true)][string]$Config,[Parameter(Mandatory=$true)][string]$OutputDirectory,[string]$Remote='personal',[string]$RcloneExe="$env:APPDATA/Playnite/ExtensionsData/e8b2f7a1-8c3d-4f5e-9a6b-1c2d3e4f5a6b/Tools/rclone.exe")
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $OutputDirectory){throw 'Use a fresh isolated output directory'}
New-Item -ItemType Directory $OutputDirectory|Out-Null
$original=Get-Content -LiteralPath $Config -Raw
$match=[regex]::Match($original,'(?m)^token\s*=\s*(\{[^\r\n]+\})')
if(!$match.Success){throw 'No OAuth token available in test config'}
$results=@()
foreach($mode in @('expired-access','invalid-refresh')){
 $token=$match.Groups[1].Value|ConvertFrom-Json
 $token.access_token='invalid-disposable-test-access-token';$token.expiry='2000-01-01T00:00:00Z'
 if($mode -eq 'invalid-refresh'){$token.refresh_token='invalid-disposable-test-refresh-token'}
 $replacement='token = '+($token|ConvertTo-Json -Compress)
 $text=$original.Substring(0,$match.Index)+$replacement+$original.Substring($match.Index+$match.Length)
 $private=Join-Path $OutputDirectory ($mode+'.private.conf');[IO.File]::WriteAllText($private,$text)
 $errorFile=Join-Path $OutputDirectory ($mode+'.private.log')
 & $RcloneExe lsf ($Remote+':PlayniteLink') --max-depth 1 --config $private --retries 1 --low-level-retries 1 --contimeout 10s --timeout 15s --max-duration 30s 1>$null 2>$errorFile
 $code=$LASTEXITCODE
 if($mode -eq 'expired-access'){
  if($code -ne 0){throw 'Expired access token did not refresh'}
  if((Get-Content -LiteralPath $private -Raw).Contains('invalid-disposable-test-access-token')){throw 'Successful request did not persist refreshed token'}
 }else{
  if($code -eq 0){throw 'Invalid refresh credential unexpectedly authenticated'}
  if((Get-Content -LiteralPath $errorFile -Raw) -notmatch 'invalid_grant'){throw 'Failure was not a verified provider credential rejection'}
 }
 $results+=@{case=$mode;passed=$true;exitCode=$code;realGoogleEndpoint=$true;originalConfigUnchanged=((Get-Content -LiteralPath $Config -Raw) -eq $original)}
 Write-Output ('PASS isolated live provider credential test: '+$mode)
}
$results|ConvertTo-Json|Set-Content (Join-Path $OutputDirectory 'results.json')
