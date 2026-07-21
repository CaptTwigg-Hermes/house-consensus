[CmdletBinding()]
param(
    [string]$SourceDb = (Join-Path $PSScriptRoot "..\..\houseshopping\state\house.db")
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path -LiteralPath $SourceDb -PathType Leaf)) {
    throw "Houseshopping database not found: $SourceDb"
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$cacheDir = Join-Path $env:LOCALAPPDATA "HouseConsensus"
$localDb = Join-Path $cacheDir "house.db"
New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null

Write-Host "Staging SQLite database on local disk: $localDb"
Copy-Item -LiteralPath $SourceDb -Destination $localDb -Force
$env:HOUSESHOPPING_DB = $localDb.Replace("\", "/")

Push-Location $repoRoot
try {
    & docker compose -f docker-compose.yml -f docker-compose.dev.yml --profile tools run --rm --build importer
    if ($LASTEXITCODE -ne 0) {
        throw "House importer failed with exit code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}
