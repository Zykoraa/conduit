[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$tracked = @(& git -C $root ls-files)
if ($LASTEXITCODE -ne 0 -or !$tracked.Count) { throw 'A tracked source inventory is required.' }
$forbidden = @()
foreach ($relative in $tracked) {
    if ($relative -match '(?i)(^|/)(secrets|logs|incidents|\.ssh|artifacts/private|bin|obj|dist)/|(^|/)\.env(?:\.|$)|\.(?:dpapi|key|pem|ppk|pdb|map)$|(^|/)(?:profile\.json|.*\.run\.json|history\.jsonl|update-feed\.json|Mapping\.(?:xml|txt)|obfuscar\.xml)$') {
        $forbidden += $relative
    }
    if ([IO.Path]::GetExtension($relative) -notin @('.exe','.dll','.ico')) {
        $text = [IO.File]::ReadAllText((Join-Path $root $relative))
        if ($text -match '(?i)\b[A-Z]:[/\\]+Users[/\\]+(?!Public[/\\]|\[user\])[^/\\\s]+') { $forbidden += $relative }
    }
}
if ($forbidden.Count) { throw ('Personal/runtime/build paths in tracked source: ' + (($forbidden | Sort-Object -Unique) -join ', ')) }
$profileSource = [IO.File]::ReadAllText((Join-Path $root 'Profile.cs'))
if ($profileSource -notmatch 'public string Server \{ get; set; \} = "";') { throw 'New profiles must not contain a default server.' }
$template = Get-Content -LiteralPath (Join-Path $root 'configs/xray.template.json') -Raw | ConvertFrom-Json
$server = $template.outbounds[0].settings.vnext[0]
$reality = $template.outbounds[0].streamSettings.realitySettings
if ($server.address -ne '__SERVER__' -or $server.users[0].id -ne '__UUID__' -or $reality.publicKey -ne '__PUBLIC_KEY__' -or $reality.shortId -ne '__SHORT_ID__') {
    throw 'Connection template must contain placeholders rather than server credentials.'
}
$tun = Get-Content -LiteralPath (Join-Path $root 'configs/singbox-tun.json') -Raw | ConvertFrom-Json
$endpoints = @($tun.route.rules | Where-Object { $_.outbound -eq 'direct' -and $_.ip_cidr } | ForEach-Object ip_cidr)
if ($endpoints.Count -ne 1 -or $endpoints[0] -ne '203.0.113.1/32') { throw 'Default endpoint route must use a documentation address.' }
Write-Output "Public source gate passed for $($tracked.Count) tracked files."
