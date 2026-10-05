[CmdletBinding()]
param([string]$Version = '', [string]$Destination = '')
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
if (!$Version) { $Version = ([xml](Get-Content -LiteralPath (Join-Path $repository 'WorkTunnel.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } }
if ($Version -notmatch '^\d{1,5}\.\d{1,5}\.\d{1,5}$') { throw 'Invalid release version' }
if (!$Destination) { $Destination = Join-Path $repository "artifacts\private\public-release\$Version" }
$releaseRoot = [IO.Path]::GetFullPath($Destination)
$site = Join-Path $releaseRoot 'site'
$assets = Join-Path $releaseRoot 'assets'
New-Item -ItemType Directory -Path $site,$assets -Force | Out-Null
$trustSource = Get-Content -LiteralPath (Join-Path $repository 'ReleaseTrust.cs') -Raw
$publicKey = [Convert]::FromBase64String([regex]::Match($trustSource, 'FromBase64String\("([^"]+)"\)').Groups[1].Value)
$signer = [Security.Cryptography.ECDsa]::Create()
$hashes = @{}
try {
    [int]$read = 0; $signer.ImportSubjectPublicKeyInfo($publicKey, [ref]$read)
    foreach ($flavor in @('Owner','Client')) {
        $feedPath = Join-Path $repository "dist\Conduit-$flavor-stable.json"
        $feed = Get-Content -LiteralPath $feedPath -Raw | ConvertFrom-Json
        $signed = [Convert]::FromBase64String($feed.Manifest)
        if (-not $signer.VerifyData($signed, [Convert]::FromBase64String($feed.Signature), [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) { throw 'Invalid public-feed signature' }
        $metadata = [Text.Encoding]::UTF8.GetString($signed) | ConvertFrom-Json
        $asset = "Conduit-$flavor-$Version.wtupdate"
        if ($metadata.Version -ne $Version -or $metadata.Flavor -ne $flavor -or $metadata.Asset -ne $asset -or [DateTimeOffset]::Parse($metadata.ExpiresAt) -le [DateTimeOffset]::UtcNow) { throw 'Unexpected or expired signed feed' }
        $bundle = Join-Path $repository "dist\$asset"
        if ((Get-Item -LiteralPath $bundle).Length -ne $metadata.BundleBytes -or (Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash -ne $metadata.BundleSha256) { throw 'Published bundle does not match signed metadata' }
        $sourceSetup = Join-Path $repository "dist\Conduit-$flavor-Setup.exe"
        $setupName = "Conduit-$flavor-$Version-Setup.exe"
        $hashes[$flavor] = (Get-FileHash -LiteralPath $sourceSetup -Algorithm SHA256).Hash
        # Explicit allowlist: no configs, signing keys, source tree or private test artifacts.
        Copy-Item -LiteralPath $sourceSetup -Destination (Join-Path $assets $setupName) -Force
        Copy-Item -LiteralPath $bundle -Destination (Join-Path $assets $asset) -Force
        Copy-Item -LiteralPath $feedPath -Destination (Join-Path $assets "Conduit-$flavor-stable.json") -Force
        if ((Get-FileHash -LiteralPath (Join-Path $assets $asset) -Algorithm SHA256).Hash -ne $metadata.BundleSha256 -or
            (Get-FileHash -LiteralPath (Join-Path $assets $setupName) -Algorithm SHA256).Hash -ne $hashes[$flavor]) { throw 'Release bytes changed during staging' }
    }
} finally { $signer.Dispose() }
$allowed = @("Conduit-Owner-$Version-Setup.exe","Conduit-Client-$Version-Setup.exe","Conduit-Owner-$Version.wtupdate","Conduit-Client-$Version.wtupdate",'Conduit-Owner-stable.json','Conduit-Client-stable.json','SHA256SUMS.txt')
foreach ($file in Get-ChildItem -LiteralPath $assets -File) { if ($file.Name -notin $allowed) { throw "Unexpected publication file: $($file.Name)" } }
$lines = foreach ($file in Get-ChildItem -LiteralPath $assets -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name) { "$( (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() )  $($file.Name)" }
$lines | Set-Content -LiteralPath (Join-Path $assets 'SHA256SUMS.txt') -Encoding utf8NoBOM
$total = (Get-ChildItem -LiteralPath $assets -File | Measure-Object -Property Length -Sum).Sum
if ($total -ge 900000000) { throw 'Release exceeds the Pages deployment budget' }
$template = Join-Path $repository 'distribution\pages'
Copy-Item -LiteralPath (Join-Path $template '.github') -Destination $site -Recurse -Force
Copy-Item -LiteralPath (Join-Path $template 'README.md') -Destination $site -Force
$html = (Get-Content -LiteralPath (Join-Path $template 'index.html') -Raw).Replace('{{VERSION}}',$Version).Replace('{{OWNER_HASH}}',$hashes.Owner).Replace('{{CLIENT_HASH}}',$hashes.Client)
if ($html.Contains('{{')) { throw 'Unresolved public-page placeholder' }
[IO.File]::WriteAllText((Join-Path $site 'index.html'), $html, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $site '.nojekyll'), '')
Write-Output "Prepared public release ${Version}: $assets ($([math]::Round($total/1048576,1)) MiB); site source: $site"
