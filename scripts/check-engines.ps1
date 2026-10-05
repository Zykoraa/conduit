[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$lock = Get-Content -LiteralPath (Join-Path $root 'engines.lock.json') -Raw | ConvertFrom-Json
foreach ($engine in $lock.files) {
    $path = Join-Path $root $engine.path
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $engine.sha256) { throw "Engine integrity mismatch: $($engine.path)" }
}
foreach ($engine in $lock.engines) {
    $output = & (Join-Path $root $engine.path) version
    if ($LASTEXITCODE -ne 0 -or -not ($output -match [regex]::Escape($engine.version))) { throw "Engine version mismatch: $($engine.path)" }
}
Write-Host 'Pinned engine versions and all bundled engine file hashes match.'
