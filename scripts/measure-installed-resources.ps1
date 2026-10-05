[CmdletBinding()]
param([Parameter(Mandatory)][string]$Report, [ValidateRange(30,86400)][int]$Seconds = 600, [ValidateRange(1,60)][int]$IntervalSeconds = 5, [ValidateSet('Owner','Client')][string]$Flavor = 'Owner')
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ConduitMeasureNative {
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint process);
}
'@
$installed = Join-Path $env:ProgramFiles "WorkTunnel-$Flavor\WorkTunnel.exe"
$version = (Get-Item -LiteralPath $installed).VersionInfo.FileVersion
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
function Snapshot {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', "WorkTunnel.v2.$sid", [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
    $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(8))
    try {
        $pipe.Connect(5000)
        $data = [Text.Encoding]::UTF8.GetBytes('{"Command":"status"}')
        $pipe.Write([BitConverter]::GetBytes([int]$data.Length),0,4); $pipe.Write($data,0,$data.Length); $pipe.Flush()
        $header = [byte[]]::new(4); [void]$pipe.ReadExactlyAsync([Memory[byte]]::new($header),$deadline.Token).AsTask().GetAwaiter().GetResult()
        $length = [BitConverter]::ToInt32($header,0); if ($length -lt 1 -or $length -gt 16384) { throw 'Invalid bounded host response' }
        $buffer = [byte[]]::new($length); [void]$pipe.ReadExactlyAsync([Memory[byte]]::new($buffer),$deadline.Token).AsTask().GetAwaiter().GetResult()
        [uint32]$hostProcess = 0
        if (-not [ConduitMeasureNative]::GetNamedPipeServerProcessId($pipe.SafePipeHandle.DangerousGetHandle(),[ref]$hostProcess)) { throw 'Host identity unavailable' }
        $state = [Text.Encoding]::UTF8.GetString($buffer) | ConvertFrom-Json
        return @{Host=$hostProcess;Xray=$state.Xray;SingBox=$state.SingBox;Verified=$state.Verified;Desired=$state.Desired}
    } finally { $pipe.Dispose(); $deadline.Dispose() }
}
$rows = [Collections.Generic.List[object]]::new()
$previous = @{}
$clock = [Diagnostics.Stopwatch]::StartNew()
$lastTime = 0.0; $nextUpdate = 60.0
do {
    $state = Snapshot
    $targets = @(@{Role='Host';Id=$state.Host},@{Role='Xray';Id=$state.Xray},@{Role='sing-box';Id=$state.SingBox})
    $dashboard = @(Get-Process -Name WorkTunnel -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $state.Host -and $_.Path -eq $installed })
    foreach ($ui in $dashboard) { $targets += @{Role='Dashboard';Id=$ui.Id} }
    $time = $clock.Elapsed.TotalSeconds
    $measurements = foreach ($target in $targets) {
        if (!$target.Id) { continue }
        $process = Get-Process -Id $target.Id -ErrorAction SilentlyContinue
        if (!$process) { continue }
        $key = "$($target.Role):$($target.Id):$($process.StartTime.ToUniversalTime().Ticks)"
        $totalCpu = $process.TotalProcessorTime.TotalSeconds
        $cpu = if ($previous.ContainsKey($key) -and $time -gt $lastTime) { 100*($totalCpu-$previous[$key])/($time-$lastTime) } else { $null }
        $previous[$key] = $totalCpu
        [pscustomobject]@{Role=$target.Role;ProcessId=$target.Id;PrivateMiB=$process.PrivateMemorySize64/1048576;WorkingSetMiB=$process.WorkingSet64/1048576;CpuOneCorePercent=$cpu;CpuMachinePercent=if($null -ne $cpu){$cpu/[Environment]::ProcessorCount}else{$null}}
    }
    $rows.Add([pscustomobject]@{Seconds=$time;IntervalSeconds=if($rows.Count){$time-$lastTime}else{0};Verified=$state.Verified;Desired=$state.Desired;Rows=@($measurements)})
    $lastTime = $time
    if ($time -ge $nextUpdate) { Write-Host ("Resource observation {0:N0}/{1}s, fresh verification: {2}" -f $time,$Seconds,$state.Verified); $nextUpdate += 60 }
    $remaining = $Seconds-$clock.Elapsed.TotalSeconds
    if ($remaining -gt 0) { Start-Sleep -Milliseconds ([int][math]::Ceiling(1000*[math]::Min($IntervalSeconds,$remaining))) }
} while ($clock.Elapsed.TotalSeconds -lt $Seconds)
$totals = @($rows | ForEach-Object { [pscustomobject]@{Weight=$_.IntervalSeconds;AppMiB=($_.Rows | Where-Object Role -in 'Dashboard','Host' | Measure-Object PrivateMiB -Sum).Sum;AllMiB=($_.Rows | Measure-Object PrivateMiB -Sum).Sum;Cpu=($_.Rows | Measure-Object CpuMachinePercent -Sum).Sum} })
function WeightedMean([string]$property) { $weight=($totals|Measure-Object Weight -Sum).Sum; $sum=($totals|ForEach-Object {$_.$property*$_.Weight}|Measure-Object -Sum).Sum; return $sum/$weight }
$warm = @($totals | Select-Object -Skip 3 -First 5)
$last = @($totals | Select-Object -Last 5)
$result = [pscustomobject]@{Schema=1;Version=$version;Seconds=$clock.Elapsed.TotalSeconds;LogicalProcessors=[Environment]::ProcessorCount;
    Scenario='Read-only existing connection; current window state and user traffic; no generated workload or reconnect';
    MeanAppPrivateMiB=(WeightedMean 'AppMiB');MaximumAppPrivateMiB=($totals | Measure-Object AppMiB -Maximum).Maximum;
    AppPrivateGrowthMiB=($last | Measure-Object AppMiB -Average).Average-($warm | Measure-Object AppMiB -Average).Average;
    MeanAllPrivateMiB=(WeightedMean 'AllMiB');MeanMachineCpuPercent=(WeightedMean 'Cpu');
    VerifiedSamples=@($rows | Where-Object Verified).Count;Samples=$rows.ToArray()}
$destination = [IO.Path]::GetFullPath($Report); New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
$result | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $destination -Encoding utf8
$result | Select-Object Version,Seconds,MeanAppPrivateMiB,MaximumAppPrivateMiB,AppPrivateGrowthMiB,MeanAllPrivateMiB,MeanMachineCpuPercent,VerifiedSamples | Format-List
