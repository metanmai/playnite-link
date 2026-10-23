param([Parameter(Mandatory=$true)][string]$Request)
$ErrorActionPreference='Stop'
function Get-VerifiedHash([string]$Path) {
    $stream=[IO.File]::OpenRead($Path); $algorithm=[Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','').ToLowerInvariant() } finally { $algorithm.Dispose(); $stream.Dispose() }
}
$record=Get-Content -LiteralPath $Request -Raw | ConvertFrom-Json
try {
    $targetProcess=Get-Process -Id $record.Process -ErrorAction SilentlyContinue
    if($targetProcess){$targetProcess.WaitForExit()}
    $source=[IO.Path]::GetFullPath($record.Source).TrimEnd('\')
    $extensions=[IO.Path]::GetFullPath($record.Extensions).TrimEnd('\')
    $destination=[IO.Path]::GetFullPath($record.Destination)
    $requestDirectory=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Request))
    if([IO.Path]::GetDirectoryName($source) -ne $extensions -or !(Test-Path -LiteralPath (Join-Path $source 'SaveManager.dll'))){throw 'Save Manager directory verification failed'}
    if(!$destination.StartsWith($requestDirectory+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Archive destination escaped migration directory'}
    if((Get-VerifiedHash $record.Config) -ne $record.ConfigHash){throw 'Save Manager changed after verification; migration must be repeated'}
    if((Get-VerifiedHash (Join-Path $source 'SaveManager.dll')) -ne $record.AssemblyHash){throw 'Save Manager installation changed after verification'}
    foreach($file in $record.Files){
        if(!(Test-Path -LiteralPath $file.Copy) -or (Get-VerifiedHash $file.Copy) -ne $file.Sha256){throw 'A migration copy changed after verification'}
        $expectedSource=if($file.SourceSha256){$file.SourceSha256}else{$file.Sha256}
        if((Test-Path -LiteralPath $file.Source) -and (Get-VerifiedHash $file.Source) -ne $expectedSource){throw 'A legacy source changed after verification'}
    }
    New-Item -ItemType Directory -Force ([IO.Path]::GetDirectoryName($destination)) | Out-Null
    if(Test-Path -LiteralPath $destination){throw 'Archive already exists'}
    Move-Item -LiteralPath $source -Destination $destination
    @{Removed=$true;ArchivedTo=$destination;CompletedUtc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $requestDirectory 'retirement-result.json')
} catch {
    @{Removed=$false;Error=$_.Exception.Message} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path ([IO.Path]::GetDirectoryName($Request)) 'retirement-result.json')
    exit 1
}

