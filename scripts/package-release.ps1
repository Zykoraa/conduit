[CmdletBinding()]
param([ValidateSet('Owner','Client')][string]$Flavor = 'Client')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'check-engines.ps1')
Push-Location $root
try { dotnet tool restore; if ($LASTEXITCODE -ne 0) { throw 'Obfuscator restore failed' } }
finally { Pop-Location }
$dist = [IO.Path]::GetFullPath((Join-Path $root 'dist'))
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$id = [guid]::NewGuid().ToString('N')
$stage = Join-Path $dist ('stage-' + $id)
$setupStage = Join-Path $dist ('setup-' + $id)
$zip = Join-Path $dist "Conduit-$Flavor.zip"
$bundle = Join-Path $dist "Conduit-$Flavor.wtupdate"
$owner = if ($Flavor -eq 'Owner') { 'true' } else { 'false' }
$project = Join-Path $root 'WorkTunnel.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ }
try {
    function Test-PublishedExecutable([string]$exe, [string]$label) {
        $reports = Join-Path $root "artifacts\private\obfuscation\$version\$Flavor"
        New-Item -ItemType Directory -Force -Path $reports | Out-Null
        $report = Join-Path $reports ($label + '-' + $id + '.txt')
        $process = Start-Process -FilePath $exe -ArgumentList @('--release-smoke-test', ('"' + $report + '"')) -WindowStyle Hidden -PassThru
        if (!$process.WaitForExit(60000)) { $process.Kill(); throw "$label release check timed out" }
        if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $report)) { throw "$label release check failed; see $report" }
        Get-Content -LiteralPath $report | Select-Object -Last 1 | Write-Host
    }
    dotnet publish $project -c Release "-p:OwnerBuild=$owner" -p:ConduitObfuscate=true -o $stage -v q
    if ($LASTEXITCODE -ne 0) { throw 'App publish failed' }
    Test-PublishedExecutable (Join-Path $stage 'WorkTunnel.exe') 'app'
    & (Join-Path $PSScriptRoot 'check-release-resources.ps1') -Executable (Join-Path $stage 'WorkTunnel.exe') -Report (Join-Path $root "artifacts\private\resources\$version\$Flavor.json")
    [IO.File]::WriteAllText((Join-Path $stage 'release-manifest.json'), (@{Version="$version";Flavor=$Flavor}|ConvertTo-Json))
    Copy-Item -LiteralPath (Join-Path $root 'SETUP.md'),(Join-Path $root 'engines.lock.json') -Destination $stage
    if (Test-Path -LiteralPath (Join-Path $stage 'configs/profile.json')) { throw 'An enrolled profile must never ship' }
    $legacyProfile = Join-Path $root 'configs/profile.json'
    $identity = if (Test-Path -LiteralPath $legacyProfile) { (Get-Content -LiteralPath $legacyProfile -Raw | ConvertFrom-Json).uuid } else { '' }
    foreach ($file in (Get-ChildItem -LiteralPath $stage -Recurse -File)) {
        if ($file.Extension -in @('.pdb','.cs','.csproj','.map') -or $file.Name -in @('Mapping.xml','Mapping.txt','obfuscar.xml')) { throw "Private build information in package: $($file.Name)" }
        if ($file.Extension -in @('.key','.pem','.ppk','.dpapi')) { throw 'Unexpected credential file in package' }
        if ($file.Extension -in @('.json','.txt','.ps1','.config','.cmd','.bat','.md')) {
            $text = Get-Content -LiteralPath $file.FullName -Raw
            if (($identity -and $text.Contains($identity)) -or $text -match 'BEGIN .*PRIVATE KEY|vless://[a-f0-9-]{36}@|discord(app)?\.com/api/webhooks') { throw "Unexpected credential in $($file.Name)" }
        }
    }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
    dotnet run --project (Join-Path $root 'tools/ReleaseTool/ReleaseTool.csproj') -c Release -- sign $zip "$version" $Flavor $bundle
    if ($LASTEXITCODE -ne 0) { throw 'Release signing failed' }
    dotnet publish (Join-Path $root 'installer/WorkTunnel.Setup.csproj') -c Release "-p:OwnerBuild=$owner" -p:ConduitObfuscate=true "-p:PayloadFile=$bundle" -o $setupStage -v q
    if ($LASTEXITCODE -ne 0) { throw 'Setup publish failed' }
    Test-PublishedExecutable (Join-Path $setupStage 'WorkTunnel.Setup.exe') 'setup'
    $setup = Join-Path $dist "Conduit-$Flavor-Setup.exe"
    Copy-Item -LiteralPath (Join-Path $setupStage 'WorkTunnel.Setup.exe') -Destination $setup -Force
    $versionedBundle = Join-Path $dist "Conduit-$Flavor-$version.wtupdate"
    Copy-Item -LiteralPath $bundle -Destination $versionedBundle -Force
    $feed = Join-Path $dist "Conduit-$Flavor-stable.json"
    dotnet run --project (Join-Path $root 'tools/ReleaseTool/ReleaseTool.csproj') -c Release -- feed $versionedBundle $feed
    if ($LASTEXITCODE -ne 0) { throw 'Signed update-feed creation failed' }
    foreach ($file in @($setup,$bundle)) { Write-Host ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash + '  ' + [IO.Path]::GetFileName($file)) }
} finally {
    foreach ($folder in @($stage,$setupStage)) {
        $resolved = [IO.Path]::GetFullPath($folder)
        if (-not $resolved.StartsWith($dist + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe staging path' }
        if (Test-Path -LiteralPath $resolved) {
            if ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unexpected staging link' }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
