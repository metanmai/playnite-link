param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $root){throw 'Choose an unused fixture directory'}
New-Item -ItemType Directory $root | Out-Null
$helper=Join-Path (Split-Path $PSScriptRoot -Parent) 'src/RetireSaveManager.ps1'
$results=@()
foreach($scenario in @('verified','changed-config','changed-dll','changed-copy','changed-source','outside-destination','outside-source','existing-destination','wait-for-exit')){
    $fixture=Join-Path $root $scenario
    $extensions=Join-Path $fixture Extensions
    $source=Join-Path $extensions SaveManager
    $migration=Join-Path $fixture migration
    $destination=Join-Path $migration 'retired/SaveManager'
    New-Item -ItemType Directory -Force $source,$migration | Out-Null
    Set-Content -LiteralPath (Join-Path $source SaveManager.dll) 'isolated fake extension'
    $config=Join-Path $fixture config.json;Set-Content -LiteralPath $config '{}'
    $original=Join-Path $fixture original.zip;Set-Content -LiteralPath $original 'saved bytes'
    $copy=Join-Path $migration archive.zip;Copy-Item -LiteralPath $original -Destination $copy
    $record=@{Process=2147483000;Source=$source;Destination=$destination;Extensions=$extensions;Config=$config;ConfigHash=(Get-FileHash $config).Hash.ToLowerInvariant();AssemblyHash=(Get-FileHash (Join-Path $source SaveManager.dll)).Hash.ToLowerInvariant();Files=@(@{Source=$original;Copy=$copy;Sha256=(Get-FileHash $copy).Hash.ToLowerInvariant()})}
    switch($scenario){
        changed-config {Set-Content -LiteralPath $config '{"changed":true}'}
        changed-dll {Add-Content -LiteralPath (Join-Path $source SaveManager.dll) 'changed'}
        changed-copy {Add-Content -LiteralPath $copy 'changed'}
        changed-source {Add-Content -LiteralPath $original 'changed'}
        outside-destination {$record.Destination=Join-Path $fixture outside}
        outside-source {$record.Extensions=Join-Path $fixture unrelated}
        existing-destination {New-Item -ItemType Directory -Force $destination|Out-Null}
        wait-for-exit {$waiting=Start-Process powershell.exe -ArgumentList '-NoProfile -WindowStyle Hidden -Command "Start-Sleep -Seconds 4"' -WindowStyle Hidden -PassThru;$record.Process=$waiting.Id}
    }
    $request=Join-Path $migration retire-request.json;$record|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $request
    if($scenario -eq 'wait-for-exit'){
        $worker=Start-Process powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-WindowStyle','Hidden','-File',('"'+$helper+'"'),'-Request',('"'+$request+'"')) -WindowStyle Hidden -PassThru
        Start-Sleep -Milliseconds 500
        if(!(Test-Path -LiteralPath $source)){throw 'Retired a running process extension'}
        if(!$worker.WaitForExit(15000)){throw 'Retirement helper did not finish'}
    } else {& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $helper -Request $request}
    $outcome=Get-Content -LiteralPath (Join-Path $migration retirement-result.json) -Raw|ConvertFrom-Json
    $expected=$scenario -in @('verified','wait-for-exit')
    if($outcome.Removed -ne $expected){throw "Unexpected result for $scenario"}
    if($expected -and ((Test-Path -LiteralPath $source) -or !(Test-Path -LiteralPath (Join-Path $destination SaveManager.dll)))){throw 'Verified extension was not archived'}
    if(!$expected -and !(Test-Path -LiteralPath $source)){throw 'Unverified source was modified'}
    if(!(Test-Path -LiteralPath $original) -or !(Test-Path -LiteralPath $copy)){throw 'Retirement removed recovery data'}
    $results += [pscustomobject]@{Scenario=$scenario;Passed=$true}
    Write-Output "PASS retirement $scenario"
}
$results|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $root results.json)
Write-Output "$($results.Count) retirement cases passed"
