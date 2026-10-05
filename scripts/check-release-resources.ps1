[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$Report,
    [double]$MaxPrivateMiB = 64,
    [ValidateRange(1,5)][int]$Repetitions = 3
)
$ErrorActionPreference = 'Stop'
$appPath = [IO.Path]::GetFullPath($Executable)
$reportPath = [IO.Path]::GetFullPath($Report)
New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($reportPath)) | Out-Null
$samples = for ($index = 1; $index -le $Repetitions; $index++) {
    $samplePath = $reportPath + ".sample-$index.json"
    $process = Start-Process -FilePath $appPath -ArgumentList @('--release-resource-test', ('"' + $samplePath + '"')) -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(60000)) { $process.Kill(); throw 'Offline resource check timed out' }
    if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $samplePath)) { throw 'Offline resource check failed' }
    $sample = Get-Content -LiteralPath $samplePath -Raw | ConvertFrom-Json
    if ($sample.ExitCode -ne 0 -or $null -eq $sample.PrivateMiB -or $sample.PrivateMiB -le 0) { throw 'Invalid resource report' }
    $sample
}
$maximum = ($samples | Measure-Object -Property PrivateMiB -Maximum).Maximum
$result = [pscustomobject]@{
    Scenario = 'Fresh offline app processes; published checks plus dashboard construction/rendering; no engines, traffic or installation'
    BudgetPrivateMiB = $MaxPrivateMiB
    MaximumPrivateMiB = $maximum
    Passed = $maximum -le $MaxPrivateMiB
    Samples = @($samples)
}
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath -Encoding utf8
if (!$result.Passed) { throw "Private memory $maximum MiB exceeds the offline app budget $MaxPrivateMiB MiB. See $reportPath" }
Write-Host ("Offline resource budget passed: {0:N1} / {1:N0} MiB maximum over {2} runs." -f $maximum,$MaxPrivateMiB,$Repetitions)
