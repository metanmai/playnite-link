param([string]$PlaynitePath = "$env:LOCALAPPDATA/Playnite", [string]$BuildDirectory = '')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot.Replace('/', '\')
$buildDir = if ($BuildDirectory) { [IO.Path]::GetFullPath($BuildDirectory) } else { Join-Path $projectRoot 'build' }
New-Item -ItemType Directory -Force $buildDir | Out-Null
$compilerPath = "$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
$references = @("/r:$PlaynitePath/Playnite.SDK.dll", "/r:$PlaynitePath/Newtonsoft.Json.dll", '/r:System.Core.dll', '/r:System.dll', '/r:System.Xaml.dll','/r:System.Security.dll','/r:System.IO.Compression.dll')
$wpfRoot = "$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/WPF"
$references += @("/r:$wpfRoot/WindowsBase.dll", "/r:$wpfRoot/PresentationCore.dll", "/r:$wpfRoot/PresentationFramework.dll")
& $compilerPath /nologo /target:library /optimize+ /langversion:5 "/out:$buildDir/PlayniteLink.dll" @references (Get-ChildItem (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
if ($LASTEXITCODE -ne 0) { throw 'Extension compilation failed' }
Copy-Item "$projectRoot/extension.yaml" $buildDir
& $compilerPath /nologo /target:exe /optimize+ /langversion:5 "/out:$buildDir/Tests.exe" @references "/r:$buildDir/PlayniteLink.dll" (Join-Path $projectRoot 'tests\Tests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
& $compilerPath /nologo /target:exe /optimize+ /langversion:5 "/out:$buildDir/VaultTests.exe" @references "/r:$buildDir/PlayniteLink.dll" (Join-Path $projectRoot 'tests/VaultTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Vault test compilation failed' }
& $compilerPath /nologo /target:exe /optimize+ /langversion:5 "/out:$buildDir/StreamingTests.exe" @references "/r:$buildDir/PlayniteLink.dll" (Join-Path $projectRoot 'tests/StreamingTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Streaming/device test compilation failed' }
& $compilerPath /nologo /target:exe /optimize+ /langversion:5 "/out:$buildDir/SecurityTests.exe" @references "/r:$buildDir/PlayniteLink.dll" (Join-Path $projectRoot 'tests/SecurityTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Security test compilation failed' }
& $compilerPath /nologo /target:exe /optimize+ /langversion:5 "/out:$buildDir/ApplicationLockTests.exe" @references "/r:$buildDir/PlayniteLink.dll" (Join-Path $projectRoot 'tests/ApplicationLockTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Application lock test compilation failed' }
# Tests use the exact Newtonsoft and SDK assemblies supplied by the installed Playnite.
Copy-Item "$PlaynitePath/Newtonsoft.Json.dll","$PlaynitePath/Playnite.SDK.dll" $buildDir
Write-Output "Built extension and tests in $buildDir"




Copy-Item (Join-Path $projectRoot "src/SetupApply.ps1") $buildDir -Force
& $compilerPath /nologo /target:exe /optimize+ /langversion:5 "/out:$buildDir/SetupTests.exe" @references "/r:$buildDir/PlayniteLink.dll" (Join-Path $projectRoot 'tests/SetupTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Setup sync test compilation failed' }
& $compilerPath /nologo /target:exe /optimize+ /langversion:5 "/out:$buildDir/ActivityTests.exe" @references "/r:$buildDir/PlayniteLink.dll" (Join-Path $projectRoot 'tests/ActivityTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Activity integration test compilation failed' }
& $compilerPath /nologo /target:exe /optimize+ /langversion:5 "/out:$buildDir/HltbTests.exe" @references "/r:$buildDir/PlayniteLink.dll" (Join-Path $projectRoot 'tests/HltbTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'HLTB integration test compilation failed' }
& $compilerPath /nologo /target:exe /optimize+ /langversion:5 "/out:$buildDir/RuntimeTests.exe" @references "/r:$buildDir/PlayniteLink.dll" (Join-Path $projectRoot 'tests/RuntimeTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Runtime test compilation failed' }

foreach($test in @('MetadataTransferTests','MetadataCloudProbe','AuditTests','CrashRecoveryTests','CloudEnrollmentProbe')){
 & $compilerPath /nologo /target:exe /optimize+ /langversion:5 "/out:$buildDir/$test.exe" @references "/r:$buildDir/PlayniteLink.dll" (Join-Path $projectRoot ('tests/'+$test+'.cs'))
 if($LASTEXITCODE -ne 0){throw 'Metadata test compilation failed'}
}
