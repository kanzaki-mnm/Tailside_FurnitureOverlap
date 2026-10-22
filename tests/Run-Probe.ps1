param([Parameter(Mandatory)][string]$SandboxRoot, [Parameter(Mandatory)][string]$SpriteFixtures)
$ErrorActionPreference='Stop'
$SandboxRoot=(Resolve-Path -LiteralPath $SandboxRoot).Path
if($SandboxRoot.Contains('steamapps',[StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath "$SandboxRoot/.modsetup-sandbox")){throw 'Marked isolated game copy required'}
if(Get-Process Tailside -ErrorAction SilentlyContinue){throw 'Close Tailside first'}
$guard=Get-ChildItem "$SandboxRoot/BepInEx/patchers" -Recurse -Filter SetupSandboxGuard.dll
if(@($guard).Count -ne 1){throw 'Exactly one save-isolation SetupSandboxGuard fixture is required'}
$project=Split-Path $PSScriptRoot -Parent
dotnet build "$project/FurnitureOverlap.csproj" -c Release --configfile "$project/NuGet.Config"
if($LASTEXITCODE){throw 'Mod build failed'}
dotnet build "$PSScriptRoot/Probe.csproj" -c Release --configfile "$project/NuGet.Config"
if($LASTEXITCODE){throw 'Probe build failed'}
$files=@("$SandboxRoot/BepInEx/plugins/FurnitureOverlap.dll","$SandboxRoot/BepInEx/plugins/FurnitureOverlapProbe.dll")
foreach($f in $files){if(Test-Path -LiteralPath $f){throw "Existing fixture: $f"}}
$validation=Join-Path $project 'validation'
New-Item -ItemType Directory -Force $validation | Out-Null
$previousSandbox=$env:MODSETUP_SANDBOX_ROOT
$previousFixtures=$env:OVERLAP_SPRITE_FIXTURES
$process=$null
$renderReport=Join-Path $SandboxRoot 'overlap-render.report'
if(Test-Path -LiteralPath $renderReport){Remove-Item -LiteralPath $renderReport}
try {
    Copy-Item "$project/bin/Release/netstandard2.1/FurnitureOverlap.dll" $files[0]
    Copy-Item "$PSScriptRoot/bin/Release/netstandard2.1/FurnitureOverlapProbe.dll" $files[1]
    $env:MODSETUP_SANDBOX_ROOT=$SandboxRoot
    $env:OVERLAP_SPRITE_FIXTURES=(Resolve-Path -LiteralPath $SpriteFixtures).Path
    $process=Start-Process -FilePath "$SandboxRoot/Tailside.exe" -WorkingDirectory $SandboxRoot -ArgumentList '-batchmode','--bridge-smoke-test','--overlap-probe','-logFile',('"'+$validation+'/player.log"') -WindowStyle Hidden -PassThru
    if(!$process.WaitForExit(45000)){throw 'Probe timeout'}
    Copy-Item "$SandboxRoot/BepInEx/LogOutput.log" "$validation/runtime.log" -Force
    $log=Get-Content "$validation/runtime.log" -Raw
    Copy-Item -LiteralPath $renderReport -Destination "$validation/render.report" -Force
    $render=Get-Content -LiteralPath $renderReport -Raw
    if($process.ExitCode -ne 0 -or $render -notmatch 'checks=176' -or $log -notmatch 'SANDBOX_ISOLATED' -or $log -match '\[Error|\[Fatal|OVERLAP_PROBE_FAIL'){throw 'Probe failed; inspect validation/runtime.log and render.report'}
    'PASS: 176 checks including real sprites, equal-priority herbs and GPU render-order readback; saves isolated.'
} finally {
    $env:MODSETUP_SANDBOX_ROOT=$previousSandbox
    $env:OVERLAP_SPRITE_FIXTURES=$previousFixtures
    if($process -and !$process.HasExited){Stop-Process -Id $process.Id; if(!$process.WaitForExit(10000)){throw 'Process still active; fixture removal deferred'}}
    foreach($f in $files){if(Test-Path -LiteralPath $f){Remove-Item -LiteralPath $f}}
    if(Test-Path -LiteralPath $renderReport){Remove-Item -LiteralPath $renderReport}
}
