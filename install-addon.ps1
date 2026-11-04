<#
  Puts the WoWOracle addon into your WoW client's Interface\AddOns folder.

  .\install-addon.ps1                 # link (junction): edits in the repo show up after /reload in game
  .\install-addon.ps1 -Copy           # plain copy instead (for friends / a zip)
  .\install-addon.ps1 -Client _forever_ -WowPath "D:\Games\World of Warcraft"
#>
param(
    [string]$WowPath,
    [string]$Client,
    [switch]$Copy
)
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'addon\WoWOracle'

$roots = @($WowPath, 'C:\Program Files (x86)\World of Warcraft', 'C:\Program Files\World of Warcraft',
           'C:\Games\World of Warcraft', 'D:\World of Warcraft', 'D:\Games\World of Warcraft') |
         Where-Object { $_ -and (Test-Path $_) }
if (-not $roots) { throw "World of Warcraft not found. Pass -WowPath 'X:\path\to\World of Warcraft'." }
$root = $roots[0]

# Client folders look like _retail_, _classic_era_, _forever_ (or similar).
$clients = Get-ChildItem $root -Directory | Where-Object { $_.Name -match '^_.+_$' }
if ($Client) { $clients = $clients | Where-Object { $_.Name -eq $Client } }
if (-not $clients) { throw "No client folder found in $root." }
if (@($clients).Count -gt 1) {
    Write-Host "Clients in ${root}:" -ForegroundColor Yellow
    $i = 0; $clients | ForEach-Object { Write-Host "  [$i] $($_.Name)"; $i++ }
    $pick = Read-Host 'Which one (number)'
    $clients = @($clients)[[int]$pick]
}

$addons = Join-Path $clients.FullName 'Interface\AddOns'
New-Item -ItemType Directory -Force $addons | Out-Null
$target = Join-Path $addons 'WoWOracle'
if (Test-Path $target) { Remove-Item $target -Recurse -Force }

if ($Copy) {
    Copy-Item $source $target -Recurse
    Write-Host "Copied WoWOracle to $target" -ForegroundColor Green
} else {
    New-Item -ItemType Junction -Path $target -Target $source | Out-Null
    Write-Host "Linked $target -> $source (edit in the repo, /reload in game)" -ForegroundColor Green
}
Write-Host "In game: AddOns list at the character screen, tick 'Load out of date AddOns' if it shows as out of date."
