param(
    [string]$PlaynitePath = "$env:LOCALAPPDATA/Playnite",
    [string]$RcloneExe = "$env:APPDATA/Playnite/ExtensionsData/e8b2f7a1-8c3d-4f5e-9a6b-1c2d3e4f5a6b/Tools/rclone.exe",
    [string]$OutputDirectory = '',
    [switch]$IncludeRuntimeDownload
)
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath $RcloneExe -PathType Leaf)) { throw 'Supply -RcloneExe with an installed Rclone executable.' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $env:LOCALAPPDATA ('PlayniteLink-regression-' + [Guid]::NewGuid().ToString('N')) }
$runRoot = [IO.Path]::GetFullPath($OutputDirectory)
if ($runRoot.StartsWith('\\')) { throw 'Use a local Windows output directory for filesystem guarantees.' }
if (Test-Path -LiteralPath $runRoot) { throw 'Choose a new output directory so existing fixtures cannot affect results.' }
New-Item -ItemType Directory -Path $runRoot | Out-Null
$repoRoot = Split-Path $PSScriptRoot -Parent
$buildDir = Join-Path $runRoot 'build'
& (Join-Path $repoRoot 'build.ps1') -PlaynitePath $PlaynitePath -BuildDirectory $buildDir
$suites = @('AuditTests','CrashRecoveryTests','SaveManagerReplacementTests','Tests','VaultTests','StreamingTests','SecurityTests','ApplicationLockTests','SetupTests','ActivityTests','HltbTests','NativeHltbTests','MetadataTransferTests')
if ($IncludeRuntimeDownload) { $suites += 'RuntimeTests' }
$results = @()
foreach ($suite in $suites) {
    & (Join-Path $buildDir ($suite + '.exe')) (Join-Path $runRoot $suite) $RcloneExe (Join-Path $buildDir 'SetupApply.ps1') 2>&1 | Tee-Object -FilePath (Join-Path $runRoot ($suite + '.txt'))
    $code = $LASTEXITCODE
    $results += [pscustomobject]@{ Suite=$suite; ExitCode=$code }
    $results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'results.json')
    if ($code -ne 0) { throw "$suite failed; inspect $runRoot" }
}
& (Join-Path $PSScriptRoot 'Run-SetupCrashTests.ps1') -OutputDirectory (Join-Path $runRoot 'SetupCrashTests')
& (Join-Path $PSScriptRoot 'Run-SaveRetirementTests.ps1') -OutputDirectory (Join-Path $runRoot 'SaveRetirementTests')
Write-Output "All requested regression suites and helper termination tests passed. Evidence: $runRoot"
