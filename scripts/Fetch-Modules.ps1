param([string]$Lock=(Join-Path (Split-Path $PSScriptRoot) 'modules.lock.json'),[string]$Destination=(Join-Path (Split-Path $PSScriptRoot) 'out/sources'))
$ErrorActionPreference='Stop'
$entries=Get-Content $Lock -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$result=@{}
foreach($entry in $entries){
 $target=Join-Path $Destination $entry.name
 $url=$entry.url
 if($env:NC_REPOSITORY_BASE){$url=$env:NC_REPOSITORY_BASE.TrimEnd('/')+'/'+$entry.name}
 if(!(Test-Path "$target/.git")){git clone --no-checkout $url $target | Out-Null;if($LASTEXITCODE){throw "Fetch failed: $url"}}
 git -C $target fetch $url $entry.revision | Out-Null
 if($LASTEXITCODE){throw 'Pinned source fetch failed'}
 git -C $target checkout --detach $entry.revision | Out-Null
 if($LASTEXITCODE){throw "Pinned revision absent: $($entry.revision)"}
 if((git -C $target rev-parse HEAD) -ne $entry.revision){throw 'Pin mismatch'}
 $result[$entry.name]=[IO.Path]::GetFullPath($target)
}
return $result
