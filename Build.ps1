param([string]$GameRoot='C:/Program Files (x86)/Steam/steamapps/common/Tailside')
$ErrorActionPreference='Stop'
[xml]$project=Get-Content -LiteralPath "$PSScriptRoot/FurnitureOverlap.csproj" -Raw
$version=[string]$project.Project.PropertyGroup.Version
if($version -notmatch '^\d+\.\d+\.\d+$'){throw 'Invalid project version'}
$source=Get-Content -LiteralPath "$PSScriptRoot/Plugin.cs" -Raw
if($source -notmatch ('BepInPlugin\(Id, "Furniture Overlap", "'+[regex]::Escape($version)+'"\)')){throw 'Plugin and project versions differ'}
dotnet build "$PSScriptRoot/FurnitureOverlap.csproj" -c Release --configfile "$PSScriptRoot/NuGet.Config" "-p:GameRoot=$GameRoot"
if($LASTEXITCODE){throw 'Build failed'}
$target=Join-Path $PSScriptRoot 'release/BepInEx/plugins/FurnitureOverlap'
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item "$PSScriptRoot/bin/Release/netstandard2.1/FurnitureOverlap.dll" $target -Force
Copy-Item "$PSScriptRoot/README.md" $target -Force
$allowed=@((Join-Path $target 'FurnitureOverlap.dll'),(Join-Path $target 'README.md'))
foreach($notice in @('LICENSE')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $notice) -Destination $target -Force
    $allowed+=Join-Path $target $notice
}
$unexpected=Get-ChildItem -LiteralPath "$PSScriptRoot/release/BepInEx" -Recurse -File | Where-Object {$_.FullName -notin $allowed}
if($unexpected){throw 'Unexpected release staging files; inspect before packaging'}
$archive=Join-Path $PSScriptRoot "release/FurnitureOverlap-$version.zip"
Compress-Archive -LiteralPath "$PSScriptRoot/release/BepInEx" -DestinationPath $archive -Force
Get-FileHash "$target/FurnitureOverlap.dll",$archive
