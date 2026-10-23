param([Parameter(Mandatory=$true)][string]$Request)
$ErrorActionPreference='Stop'
$requestData=Get-Content -LiteralPath $Request -Raw|ConvertFrom-Json
$source=[IO.Path]::GetFullPath($requestData.Source).TrimEnd('\')
$extensions=[IO.Path]::GetFullPath($requestData.Extensions).TrimEnd('\')
$destination=[IO.Path]::GetFullPath($requestData.Destination).TrimEnd('\')
$recovery=[IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Request))) 'packages')).TrimEnd('\')
if([IO.Path]::GetDirectoryName($source) -ne $extensions -or !$destination.StartsWith($recovery+'\',[StringComparison]::OrdinalIgnoreCase) -or $destination.StartsWith($extensions+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Achievement add-on archive paths are outside the permitted scope'}
if(!(Test-Path -LiteralPath (Join-Path $source 'PlayniteAchievements.dll'))){throw 'Expected achievement module is missing'}
try{Wait-Process -Id ([int]$requestData.Process) -ErrorAction SilentlyContinue}catch{}
$expected=@($requestData.Files)
if(@(Get-ChildItem -LiteralPath $source -Directory -Recurse|Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'Linked achievement directories are not supported'}
$actual=@(Get-ChildItem -LiteralPath $source -File -Recurse)
if($actual.Count -ne $expected.Count){throw 'Achievement package changed; original installation kept'}
foreach($entry in $expected){
 $file=[IO.Path]::GetFullPath((Join-Path $source $entry.Name))
 if(!$file.StartsWith($source+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid archive member'}
 if((Get-Item -LiteralPath $file).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked archive members are not supported'}
 if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.Hash){throw 'Achievement package changed; original installation kept'}
}
if(Test-Path -LiteralPath $destination){throw 'Recovery destination already exists'}
New-Item -ItemType Directory -Force ([IO.Path]::GetDirectoryName($destination))|Out-Null
# Both resolved absolute targets were checked above. Engine user data is untouched.
Move-Item -LiteralPath $source -Destination $destination
@{Archived=$true;Destination=$destination}|ConvertTo-Json|Set-Content -LiteralPath ($Request+'.result.json')
