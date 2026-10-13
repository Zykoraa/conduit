[CmdletBinding()]
param([string]$Assets = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content -LiteralPath (Join-Path $root 'WorkTunnel.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ }
if (!$Assets) { $Assets = Join-Path $root "artifacts/private/public-release/$version/assets" }
$expectedGuide = (Get-FileHash -LiteralPath (Join-Path $root 'VM_SETUP.md')).Hash
if ((Get-FileHash -LiteralPath (Join-Path $Assets "Conduit-VM-Setup-$version.md")).Hash -ne $expectedGuide) { throw 'Standalone guide differs from repository source.' }
$expected = @('VM_SETUP.md','RELEASE_NOTES.md') + @(& git -C $root ls-files -- server/)
if ($LASTEXITCODE -ne 0) { throw 'Could not read tracked server inventory.' }
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $Assets "Conduit-Server-$version.zip"))
try {
    if ($archive.Entries.Count -ne $expected.Count) { throw 'Server archive contains missing or extra files.' }
    foreach ($relative in $expected) {
        $entry = $archive.GetEntry($relative)
        if ($null -eq $entry) { throw "Missing server archive entry: $relative" }
        $sourceBytes = [IO.File]::ReadAllBytes((Join-Path $root $relative))
        $entryStream = $entry.Open()
        $buffer = [IO.MemoryStream]::new()
        try { $entryStream.CopyTo($buffer); $entryBytes = $buffer.ToArray() }
        finally { $entryStream.Dispose(); $buffer.Dispose() }
        $sourceHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$sourceBytes))
        $entryHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$entryBytes))
        if ($sourceHash -ne $entryHash) { throw "Server archive differs from source: $relative" }
        if ([IO.Path]::GetExtension($relative) -in @('.sh','.py') -and [Array]::IndexOf($entryBytes, [byte]13) -ge 0) { throw "Linux executable source contains Windows line endings: $relative" }
    }
} finally { $archive.Dispose() }
Write-Output 'Standalone VM guide and complete server archive verified against source.'
