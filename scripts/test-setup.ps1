# Mutating integration checks: never run on a workstation.
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'Disposable GitHub runner required' }
$root = Split-Path $PSScriptRoot -Parent
$setup = Join-Path $root 'dist/Conduit-Client-Setup.exe'
$installed = Join-Path $env:ProgramFiles 'WorkTunnel-Client/WorkTunnel.exe'
function SetupAction([string]$action) {
    $process = Start-Process -FilePath $setup -ArgumentList $action -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) { $process.Kill(); throw "Setup timed out: $action" }
    if ($process.ExitCode -ne 0) {
        $logRoot = Join-Path $env:ProgramData 'WorkTunnel/updates-Client'
        $lastError = Get-ChildItem -LiteralPath $logRoot -Filter 'setup-error-*.txt' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        if ($lastError) { Get-Content -LiteralPath $lastError.FullName | Write-Host }
        throw "Setup failed: $action"
    }
}
try {
    SetupAction '--install'
    if (-not (Test-Path -LiteralPath $installed)) { throw 'App not installed' }
    $programs = [Environment]::GetFolderPath('CommonPrograms')
    $menu = Join-Path $programs 'Conduit.lnk'
    $legacyMenu = Join-Path $programs 'Conduit Client.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $legacy = $shell.CreateShortcut($legacyMenu)
    $legacy.TargetPath = $installed; $legacy.Save()
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($legacy) | Out-Null
    $expected = (Get-FileHash -LiteralPath $installed).Hash
    [IO.File]::WriteAllText($installed,'damaged test fixture')
    SetupAction '--repair'
    if ((Get-FileHash -LiteralPath $installed).Hash -ne $expected) { throw 'Repair did not restore the executable' }
    if (Test-Path -LiteralPath $legacyMenu) { throw 'Repair left the legacy branded shortcut' }
    if (-not (Test-Path -LiteralPath $menu)) { throw 'Conduit Start menu shortcut missing' }
    $shortcut = $shell.CreateShortcut($menu)
    if ($shortcut.TargetPath -ne $installed -or $shortcut.IconLocation -ne ($installed + ',0')) { throw 'Shortcut target or icon incorrect' }
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) | Out-Null
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
    $registration = Get-ItemProperty 'HKLM:/Software/Microsoft/Windows/CurrentVersion/Uninstall/WorkTunnel-Client'
    if ($registration.DisplayName -ne 'Conduit' -or $registration.DisplayIcon -ne ($installed + ',0')) { throw 'Installed app branding incorrect' }
    Write-Host 'PASS Conduit name, icon registration and legacy shortcut migration'
    dotnet run --project (Join-Path $root 'tests/WorkTunnel.Checks.csproj') -c Release -- --broker-smoke
    if ($LASTEXITCODE -ne 0) { throw 'Broker integration failed' }
    Write-Host 'PASS install, damaged-file repair and host reattachment'
} finally {
    SetupAction '--uninstall'
}
if (Test-Path -LiteralPath $installed) { throw 'Uninstall left the app installed' }
if (Test-Path -LiteralPath $menu) { throw 'Uninstall left the Conduit shortcut' }
Write-Host 'PASS uninstall'
