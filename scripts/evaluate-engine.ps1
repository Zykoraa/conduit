<# Evaluate an official release without replacing the installed or pinned engine. #>
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('xray','sing-box')][string]$Engine,
      [Parameter(Mandatory)][ValidatePattern('^v?[0-9][0-9A-Za-z.-]+$')][string]$Tag)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$repo = if ($Engine -eq 'xray') { 'XTLS/Xray-core' } else { 'SagerNet/sing-box' }
$name = if ($Engine -eq 'xray') { 'Xray-windows-64.zip' } else { 'sing-box-' + $Tag.TrimStart('v') + '-windows-amd64.zip' }
$release = gh api "repos/$repo/releases/tags/$Tag" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Official release metadata unavailable' }
$asset = @($release.assets | Where-Object { $_.name -ceq $name })
if ($asset.Count -ne 1 -or $asset[0].digest -notmatch '^sha256:([a-fA-F0-9]{64})$') { throw 'An official asset with a published SHA-256 digest is required' }
$digest = $Matches[1]
$candidate = Join-Path (Join-Path $root 'dist') ('candidate-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $candidate -Force | Out-Null
gh release download $Tag -R $repo -p $name -D $candidate
if ($LASTEXITCODE -ne 0) { throw 'Candidate download failed' }
$archive = Join-Path $candidate $name
if ((Get-FileHash -LiteralPath $archive).Hash -ne $digest) { throw 'Official release digest mismatch; nothing will execute' }
[IO.Compression.ZipFile]::ExtractToDirectory($archive,(Join-Path $candidate 'unpacked'))
$exeName = if ($Engine -eq 'xray') { 'xray.exe' } else { 'sing-box.exe' }
$exe = @(Get-ChildItem -LiteralPath (Join-Path $candidate 'unpacked') -Filter $exeName -Recurse -File)
if ($exe.Count -ne 1) { throw 'Unexpected candidate archive layout' }
& $exe[0].FullName version
if ($LASTEXITCODE -ne 0) { throw 'Candidate does not run' }
if ($Engine -eq 'xray') {
    $template = [IO.File]::ReadAllText((Join-Path $root 'configs/xray.template.json')).Replace('__UUID__','11111111-1111-4111-8111-111111111111').Replace('__SNI__','example.com').Replace('__SERVER__','203.0.113.1').Replace('__PUBLIC_KEY__',('A' * 43)).Replace('__SHORT_ID__','aabb')
    $config = Join-Path $candidate 'validation.json'
    [IO.File]::WriteAllText($config,$template)
    & $exe[0].FullName run -test -c $config
} else {
    & $exe[0].FullName check -c (Join-Path $root 'configs/singbox-tun.json')
}
if ($LASTEXITCODE -ne 0) { throw 'Candidate rejects current configuration; existing engines remain unchanged' }
Write-Host "Candidate passed syntax validation in $candidate. Review upstream changes and run the disposable Windows integration suite before changing engines.lock.json. No installed or pinned files were replaced."
