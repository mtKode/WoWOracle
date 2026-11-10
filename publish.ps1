<#
  Builds the shareable WoW Oracle package:  dist\WoWOracle-<version>.zip, unzipped in dist\WoWOracle to run locally
  (earlier builds in dist are removed, so only the latest is there).
  One self-contained exe (no .NET install needed on the friend's PC), the in-game addon (addon\WoWOracle) and a short readme.

  Usage:   .\publish.ps1                 # Friz Quadrata NOT included (it's Blizzard's licensed font)
           .\publish.ps1 -IncludeFont    # copies .\font\*.ttf into the zip, your call
           .\publish.ps1 -Version 1.2.0  # this version instead of the one in Directory.Build.props
                                         # (GitHub passes the tag's version, so a tag is all a release needs)
#>
param([switch]$IncludeFont, [string]$Version)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$version = $Version
if (-not $version) { $version = ([xml](Get-Content .\Directory.Build.props)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
if (-not $version) { $version = 'dev' }

# Only the latest build is kept: one app folder with a fixed name (dist\WoWOracle, so you start the same exe and the
# Start menu shortcut keeps working after every build) and one zip. Older WoWOracle* folders and zips go.
$dist  = Join-Path $PSScriptRoot 'dist'
$stage = Join-Path $dist 'WoWOracle'
$zip   = Join-Path $dist "WoWOracle-$version.zip"
$running = Get-Process -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($dist + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) }
if ($running) { throw "WoW Oracle is running from $($running[0].Path). Close it (tray icon, Exit) and run this again." }
if (Test-Path $dist) { Get-ChildItem $dist -Filter 'WoWOracle*' | Remove-Item -Recurse -Force }

Write-Host "Publishing WoW Oracle $version ..." -ForegroundColor Yellow
dotnet publish .\src\WowCompanion.Desktop -c Release -r win-x64 --self-contained true `
    -p:Version=$version `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# Friendly name for the exe, plus the readme.
Rename-Item (Join-Path $stage 'WowCompanion.Desktop.exe') 'WoW Oracle.exe'
Get-ChildItem $stage -Filter *.pdb | Remove-Item -Force
Copy-Item .\packaging\README.txt (Join-Path $stage 'README.txt')

# The in-game addon ships with the app; "Install addon" in the app copies it into WoW.
New-Item -ItemType Directory (Join-Path $stage 'addon') | Out-Null
Copy-Item .\addon\WoWOracle (Join-Path $stage 'addon\WoWOracle') -Recurse
# Known.lua may hold this PC's generated list (a linked dev copy): ship the empty placeholder.
Set-Content (Join-Path $stage 'addon\WoWOracle\Known.lua') 'WoWOracleKnown = nil' -Encoding ascii
Copy-Item .\LICENSE (Join-Path $stage 'LICENSE.txt')
Copy-Item .\THIRD-PARTY-NOTICES.md $stage

if ($IncludeFont -and (Test-Path .\font)) {
    New-Item -ItemType Directory (Join-Path $stage 'font') | Out-Null
    Copy-Item .\font\*.ttf (Join-Path $stage 'font')
}

Compress-Archive -Path "$stage\*" -DestinationPath $zip
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Done: $zip ($size MB)" -ForegroundColor Green
