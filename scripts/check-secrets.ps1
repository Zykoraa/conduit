[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$toolRoot = Join-Path $root 'artifacts/private/security/gitleaks-8.30.1'
New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null
$archive = Join-Path $toolRoot 'gitleaks_8.30.1_windows_x64.zip'
$expected = 'D29144DEFF3A68AA93CED33DDDF84B7FDC26070ADD4AA0F4513094C8332AFC4E'
if (!(Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest 'https://github.com/gitleaks/gitleaks/releases/download/v8.30.1/gitleaks_8.30.1_windows_x64.zip' -OutFile $archive -TimeoutSec 60
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) { throw 'Secret scanner digest mismatch.' }
Expand-Archive -LiteralPath $archive -DestinationPath $toolRoot -Force
$report = Join-Path $toolRoot 'history.json'
& (Join-Path $toolRoot 'gitleaks.exe') git $root --log-opts=--all --redact --no-banner --report-format json --report-path $report
if ($LASTEXITCODE -ne 0) { throw 'Secret scan failed. Inspect the redacted local report before publishing.' }
