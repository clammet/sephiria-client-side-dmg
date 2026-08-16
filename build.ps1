<#
.SYNOPSIS
  Builds ClientSideDamage.dll (BepInEx 5 plugin for Sephiria) into .\dist.

.DESCRIPTION
  Two ways to build:
    1. With the .NET SDK installed (winget install Microsoft.DotNet.SDK.8):
         dotnet build ClientSideDamage.csproj -c Release
       (that is what this script does when `dotnet build` is available)
    2. Without an SDK, using the portable Roslyn compiler that lives in ..\tools\roslyn
       (only needs the .NET 8 runtime that is already on most machines).

  Reference assemblies are taken straight from the game's Managed folder plus
  BepInEx\core - no publicised assemblies are required.

.PARAMETER GameDir
  Path to the Sephiria install (folder containing Sephiria.exe). Default: ..\Sephiria

.PARAMETER BepInExCore
  Path to a BepInEx\core folder (for BepInEx.dll / 0Harmony.dll).
  Default: <GameDir>\BepInEx\core, falling back to ..\tools\bepinex\BepInEx\core
#>
param(
    [string]$GameDir = (Join-Path $PSScriptRoot "..\Sephiria"),
    [string]$BepInExCore = ""
)

$ErrorActionPreference = "Stop"
$root    = $PSScriptRoot
$managed = Join-Path $GameDir "Sephiria_Data\Managed"
if (-not (Test-Path (Join-Path $managed "Assembly-CSharp.dll"))) { throw "Game assemblies not found in $managed" }

if ($BepInExCore -eq "") {
    $BepInExCore = Join-Path $GameDir "BepInEx\core"
    if (-not (Test-Path (Join-Path $BepInExCore "BepInEx.dll"))) { $BepInExCore = Join-Path $root "..\tools\bepinex\BepInEx\core" }
}
if (-not (Test-Path (Join-Path $BepInExCore "0Harmony.dll"))) { throw "BepInEx core not found (looked in $BepInExCore). Install BepInEx 5 into the game folder first." }

$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force $dist | Out-Null
$out = Join-Path $dist "ClientSideDamage.dll"

$refs = @(
    "$managed\mscorlib.dll", "$managed\System.dll", "$managed\System.Core.dll", "$managed\netstandard.dll",
    "$managed\UnityEngine.dll", "$managed\UnityEngine.CoreModule.dll", "$managed\UnityEngine.Physics2DModule.dll",
    "$managed\Assembly-CSharp.dll", "$managed\Mirror.dll", "$managed\Mirror.Components.dll",
    "$BepInExCore\BepInEx.dll", "$BepInExCore\0Harmony.dll"
)
$sources = Get-ChildItem (Join-Path $root "src") -Filter *.cs | ForEach-Object { $_.FullName }

$roslyn = Join-Path $root "..\tools\roslyn\tasks\netcore\bincore\csc.dll"
$sdk = $null
try { $sdk = & dotnet --list-sdks 2>$null } catch {}

if ($sdk) {
    Write-Host "Building with the .NET SDK ..."
    $env:CSD_GAME_DIR = $GameDir
    $env:CSD_BEPINEX_CORE = $BepInExCore
    & dotnet build (Join-Path $root "ClientSideDamage.csproj") -c Release -nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }
} elseif (Test-Path $roslyn) {
    Write-Host "Building with portable Roslyn ($roslyn) ..."
    $args = @("-nologo", "-noconfig", "-nostdlib", "-target:library", "-langversion:latest", "-optimize+", "-deterministic",
              "-nowarn:CS1701,CS1702", "-out:$out") + ($refs | ForEach-Object { "-r:$_" }) + $sources
    & dotnet $roslyn @args
    if ($LASTEXITCODE -ne 0) { throw "csc failed" }
} else {
    throw "Neither the .NET SDK nor ..\tools\roslyn was found. Install the SDK: winget install Microsoft.DotNet.SDK.8"
}

Write-Host "OK -> $out"
