[CmdletBinding()]
param([ValidateRange(30,300)][int]$SoakSeconds=120, [string]$Report='')
$ErrorActionPreference='Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or -not ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Full-app network benchmarks require a disposable GitHub-hosted administrator runner' }
$repository=Split-Path $PSScriptRoot -Parent
if (!$Report) { $Report=Join-Path $repository 'artifacts\private\comparison\report.json' }
$reportFile=[IO.Path]::GetFullPath($Report)
$workspace=Join-Path ([IO.Path]::GetDirectoryName($reportFile)) ('fixture-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workspace -Force | Out-Null
$checks=Join-Path $repository 'tests\bin\Release\net10.0-windows\WorkTunnel.Checks.dll'
$driver=Join-Path $repository 'tools\BenchmarkDriver\bin\Release\net10.0\BenchmarkDriver.dll'
$app=Join-Path $env:ProgramFiles 'WorkTunnel-Client\WorkTunnel.exe'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ConduitBenchmarkWindows {
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    private delegate bool EnumWindow(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
    public static IntPtr WindowFor(int process) {
        IntPtr found = IntPtr.Zero; long largest = 0;
        EnumWindows((window, data) => {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner == process && GetWindowRect(window, out Rect rectangle)) {
                long width = rectangle.Right - rectangle.Left, height = rectangle.Bottom - rectangle.Top;
                if (width > 200 && height > 200 && width * height > largest) { found = window; largest = width * height; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@
function JsonFile([string]$path,$value) { $value | ConvertTo-Json -Depth 24 | Set-Content -LiteralPath $path -Encoding utf8 }
function Broker([string]$command) { $output=& dotnet $checks --benchmark-broker $command; if($LASTEXITCODE -ne 0){throw 'Benchmark broker failed'}; return ($output | Select-Object -Last 1 | ConvertFrom-Json) }
function WaitFile([string]$file) { for($attempt=0;$attempt -lt 120;$attempt++){if(Test-Path -LiteralPath $file){return};Start-Sleep -Milliseconds 250};throw "Fixture readiness timed out: $([IO.Path]::GetFileName($file))" }
function StopOwned($process) { if($process){try{if(!$process.HasExited){$process.Kill($true);$process.WaitForExit(5000)|Out-Null}}catch{}} }
$manifestPath=Join-Path $workspace 'latest.json';$signaturePath=$manifestPath+'.sig'
Invoke-WebRequest 'https://vurso.io/tunmon/latest.json' -OutFile $manifestPath -TimeoutSec 30
Invoke-WebRequest 'https://vurso.io/tunmon/latest.json.sig' -OutFile $signaturePath -TimeoutSec 30
$trust=[Convert]::FromBase64String('MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE89ugqnnsiX+Q5H6ytBgM/g3L09xsPq8yfC9X50rryl5s+NjdU78iIktE375PtGAgTI6uXf/RM7S0IfSLk/+X5g==')
$signer=[Security.Cryptography.ECDsa]::Create()
try{[int]$read=0;$signer.ImportSubjectPublicKeyInfo($trust,[ref]$read);if(!$signer.VerifyData([IO.File]::ReadAllBytes($manifestPath),[Convert]::FromBase64String((Get-Content -LiteralPath $signaturePath -Raw).Trim()),[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)){throw 'TunMon release signature invalid'}}finally{$signer.Dispose()}
$manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
if($manifest.version -notmatch '^\d+\.\d+\.\d+$' -or $manifest.exe.file -ne "TunMon-$($manifest.version).exe" -or $manifest.exe.size -lt 1 -or $manifest.exe.size -gt 67108864 -or $manifest.exe.sha256 -notmatch '^[a-fA-F0-9]{64}$'){throw 'Unexpected signed TunMon release'}
$tunmon=Join-Path $workspace $manifest.exe.file
Invoke-WebRequest ("https://vurso.io/tunmon/"+$manifest.exe.file) -OutFile $tunmon -TimeoutSec 60
if((Get-FileHash -LiteralPath $tunmon -Algorithm SHA256).Hash -ne $manifest.exe.sha256 -or (Get-Item -LiteralPath $tunmon).Length -ne $manifest.exe.size){throw 'TunMon executable differs from its signed release'}
$v2root=Join-Path $workspace 'v2rayN';New-Item -ItemType Directory -Path (Join-Path $v2root 'bin'),(Join-Path $v2root 'binConfigs') -Force|Out-Null
Copy-Item -LiteralPath (Join-Path $env:ProgramFiles 'WorkTunnel-Client\cores\xray'),(Join-Path $env:ProgramFiles 'WorkTunnel-Client\cores\sing_box') -Destination (Join-Path $v2root 'bin') -Recurse
$xrayClient=@{log=@{loglevel='warning'};inbounds=@(@{listen='127.0.0.1';port=10808;protocol='socks';settings=@{udp=$true}});outbounds=@(@{tag='proxy';protocol='vless';settings=@{vnext=@(@{address='127.0.0.1';port=18443;users=@(@{id='00000000-0000-4000-8000-000000000123';encryption='none'})})};streamSettings=@{network='tcp';security='none'};mux=@{enabled=$false}})}
$sourceXray=Join-Path $v2root 'binConfigs\config.json';JsonFile $sourceXray $xrayClient
$data=Join-Path $workspace 'TunMon-data';New-Item -ItemType Directory -Path (Join-Path $data 'configs') -Force|Out-Null
$trace=Invoke-RestMethod -Uri 'https://1.1.1.1/cdn-cgi/trace' -TimeoutSec 15
$expected=[regex]::Match($trace,'(?m)^ip=(\d+\.\d+\.\d+\.\d+)').Groups[1].Value;if(!$expected){throw 'Baseline IPv4 exit unavailable'}
JsonFile (Join-Path $data 'settings.json') @{V2rayNRoot=$v2root;ExpectedEgressIpOverride=$expected}
JsonFile (Join-Path $data 'configs\xray.json') $xrayClient
$sing=Get-Content -LiteralPath (Join-Path $repository 'configs\singbox-tun.json') -Raw|ConvertFrom-Json -AsHashtable
$sing.inbounds[0].interface_name='singbox_tun';$sing.inbounds[0].address=@('172.18.0.1/30','fdfe:dcba:9876::1/126')
$sing.route.rules[3].ip_cidr=@('172.18.0.0/30','fdfe:dcba:9876::/126');$sing.route.rules[4].ip_cidr=@('127.0.0.1/32');$sing.route.rules[4].port=18443
$sing.experimental=@{clash_api=@{external_controller='127.0.0.1:10814'}}
JsonFile (Join-Path $data 'configs\singbox.json') $sing
JsonFile (Join-Path $data 'configs\import.json') @{ImportedUtc=[DateTime]::UtcNow.ToString('o');SourceXrayHash=(Get-FileHash -LiteralPath $sourceXray).Hash;ExpectedEgressIp=$expected;ServerAddress='127.0.0.1';ServerPort=18443;Protocol='vless';Security='none';Sni='';XrayRuleCount=0;TunMtu=1400;ClashApi='127.0.0.1:10814';ProxyInboundPort=10808;SingboxSource='v2rayN'}
& dotnet $checks --benchmark-profile $expected;if($LASTEXITCODE -ne 0){throw 'Conduit fixture preparation failed'}
$ready=Join-Path $workspace 'server.ready'
$server=Start-Process -FilePath 'dotnet' -ArgumentList @(('"'+$driver+'"'),'serve',('"'+$ready+'"')) -WindowStyle Hidden -PassThru
$relay=$null;$ui=$null;$owned=@();$runs=[Collections.Generic.List[object]]::new()
try{
    WaitFile $ready;$fixturePort=[int](Get-Content -LiteralPath $ready)
    $uplink=(Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' | Sort-Object RouteMetric | Select-Object -First 1).InterfaceAlias
    $relayConfig=Join-Path $workspace 'relay.json'
    $relayAccess=Join-Path $workspace 'relay-access.log';$relayErrors=Join-Path $workspace 'relay-errors.log'
    # Xray's server-side safety policy blocks loopback destinations by default.
    # Permit only this disposable TCP payload port on the fixture outbound.
    JsonFile $relayConfig @{log=@{loglevel='warning';access=$relayAccess;error=$relayErrors};inbounds=@(@{listen='127.0.0.1';port=18443;protocol='vless';settings=@{clients=@(@{id='00000000-0000-4000-8000-000000000123'});decryption='none'}});outbounds=@(@{tag='internet';protocol='freedom';settings=@{domainStrategy='UseIPv4'};streamSettings=@{sockopt=@{interface=$uplink}}},@{tag='fixture';protocol='freedom';settings=@{redirect="127.0.0.1:$fixturePort";finalRules=@(@{action='allow';network='tcp';ip=@('127.0.0.1/32');port="$fixturePort"})}});routing=@{rules=@(@{type='field';ip=@('1.0.0.1/32');port='80';outboundTag='fixture'},@{type='field';domain=@('full:1.0.0.1');port='80';outboundTag='fixture'})}}
    $relayExe=Join-Path $workspace 'fixture-relay.exe';Copy-Item -LiteralPath (Join-Path $v2root 'bin\xray\xray.exe') -Destination $relayExe
    $relay=Start-Process -FilePath $relayExe -ArgumentList @('run','-c',('"'+$relayConfig+'"')) -WindowStyle Hidden -PassThru
    # A public destination avoids both apps' reserved-address direct rules. The VLESS relay
    # redirects only this address/port to loopback; the payload hash proves that interception.
    $url='http://1.0.0.1/payload'
    foreach($round in 1,2){
        $order=if($round -eq 1){@('TunMon','Conduit')}else{@('Conduit','TunMon')}
        foreach($product in $order){
            Write-Host "Starting $product round $round on the identical local VLESS fixture."
            $launchUtc=[DateTimeOffset]::UtcNow;$launchClock=[Diagnostics.Stopwatch]::StartNew()
            if($product -eq 'Conduit'){$ui=Start-Process -FilePath $app -ArgumentList '--connect' -WindowStyle Hidden -PassThru}
            else{$start=[Diagnostics.ProcessStartInfo]::new($tunmon);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.Environment['TUNMON_DATA']=$data;$ui=[Diagnostics.Process]::Start($start)}
            $facts=$null;$readyProbe=$null
            while($true){
                try{
                    if($ui.HasExited){throw 'Dashboard exited during startup'}
                    if($product -eq 'Conduit'){$facts=Broker 'status';if(!$facts.Xray -or !$facts.SingBox){throw 'Cores starting'}}
                    else{$status=Get-Content -LiteralPath (Join-Path $data 'state\status.json') -Raw|ConvertFrom-Json;if([DateTimeOffset]::Parse($status.updated) -lt $launchUtc){throw 'Waiting for the new TunMon status snapshot'};$facts=@{Xray=$status.cores.xray.pid;SingBox=$status.cores.singbox.pid;Host=$null};if(!$facts.Xray -or !$facts.SingBox){throw 'Cores starting'}}
                    $readyProbe=Join-Path $workspace "ready-$round-$product.json"
                    & dotnet $driver load $url 3 1 1 $readyProbe
                    if($LASTEXITCODE -ne 0){throw 'Fixture path starting'}
                    break
                }catch{
                    if($relay.HasExited){throw 'Local VLESS relay exited'}
                    if($ui.HasExited -or $launchClock.Elapsed.TotalSeconds -ge 90){
                        if($facts){$facts|ConvertTo-Json|Write-Host}
                        $diagnostics=Join-Path ([IO.Path]::GetDirectoryName($reportFile)) 'fixture-diagnostics.txt'
                        $detail=@("Startup failure: $($_.Exception.Message)","Dashboard exited: $($ui.HasExited)")
                        $detail+=foreach($file in @($readyProbe,$relayAccess,$relayErrors)){if($file -and (Test-Path -LiteralPath $file)){"File: $([IO.Path]::GetFileName($file))";Get-Content -LiteralPath $file -Tail 15}}
                        if($product -eq 'TunMon'){
                            foreach($root in @($data,(Join-Path $env:LOCALAPPDATA 'TunMon'))){
                                $detail+="Data root present: $root = $(Test-Path -LiteralPath $root)"
                                if(Test-Path -LiteralPath $root){$detail+=Get-ChildItem -LiteralPath $root -Recurse -File|Select-Object -First 10 -ExpandProperty FullName}
                            }
                            if(Test-Path -LiteralPath (Join-Path $data 'logs')){$detail+=Get-ChildItem -LiteralPath (Join-Path $data 'logs') -File -Filter '*.jsonl'|ForEach-Object {Get-Content -LiteralPath $_.FullName -Tail 5}}
                        }
                        $detail|Set-Content -LiteralPath $diagnostics;$detail|Write-Host
                        throw
                    }
                    Start-Sleep -Milliseconds 500
                }
            }
            $startup=$launchClock.Elapsed.TotalSeconds
            Start-Sleep -Seconds 30
            if($product -eq 'TunMon'){$actualConfig=Get-Content -LiteralPath (Join-Path $data 'configs\singbox.json') -Raw|ConvertFrom-Json;if($actualConfig.inbounds[0].mtu -ne 1400 -or $actualConfig.inbounds[0].stack -ne 'gvisor'){throw 'TunMon changed the aligned benchmark transport configuration'}}
            if($product -eq 'Conduit'){$facts=Broker 'status'}else{$status=Get-Content -LiteralPath (Join-Path $data 'state\status.json') -Raw|ConvertFrom-Json;$facts=@{Xray=$status.cores.xray.pid;SingBox=$status.cores.singbox.pid;Host=$null}}
            $targets=@(@{Role='Dashboard';Id=$ui.Id},@{Role='Xray';Id=$facts.Xray},@{Role='sing-box';Id=$facts.SingBox});if($facts.Host){$targets+=@{Role='Host';Id=$facts.Host}}
            $owned=@($targets | ForEach-Object {Get-Process -Id $_.Id})
            $phases=[Collections.Generic.List[object]]::new()
            foreach($phase in @(@{Name='Idle shown';Seconds=15;Minimized=$false;Load=$false;Rate=0},@{Name='Idle minimized';Seconds=15;Minimized=$true;Load=$false;Rate=0},@{Name='Uncapped local transfer';Seconds=20;Minimized=$false;Load=$true;Rate=0},@{Name='Sustained 2 MiB/s';Seconds=$SoakSeconds;Minimized=$false;Load=$true;Rate=2},@{Name='Idle after load';Seconds=15;Minimized=$false;Load=$false;Rate=0})){
                $ui.Refresh();$window=$ui.MainWindowHandle
                # Process.MainWindowHandle excludes windows started hidden. Locate only
                # this owned dashboard's largest top-level window before showing it.
                if($window -eq [IntPtr]::Zero){$window=[ConduitBenchmarkWindows]::WindowFor($ui.Id)}
                if($window -eq [IntPtr]::Zero){throw 'Dashboard window unavailable'}
                [void][ConduitBenchmarkWindows]::ShowWindowAsync($window, $(if($phase.Minimized){6}else{9}))
                $foreground=if(!$phase.Minimized){[ConduitBenchmarkWindows]::SetForegroundWindow($window)}else{$false}
                $loadReport=Join-Path $workspace "load-$round-$product-$($phases.Count).json";$load=$null
                if($phase.Load){$load=Start-Process -FilePath 'dotnet' -ArgumentList @(('"'+$driver+'"'),'load',$url,$phase.Seconds,2,$phase.Rate,('"'+$loadReport+'"')) -WindowStyle Hidden -PassThru}
                $samples=[Collections.Generic.List[object]]::new();$before=@{};$phaseClock=[Diagnostics.Stopwatch]::StartNew();$previousTime=0.0
                try{
                    do{
                        $time=$phaseClock.Elapsed.TotalSeconds
                        $measurements=foreach($target in $targets){$process=Get-Process -Id $target.Id;$cpu=$process.TotalProcessorTime.TotalSeconds;$delta=if($before.ContainsKey($target.Id)-and $time -gt $previousTime){100*($cpu-$before[$target.Id])/($time-$previousTime)}else{$null};$before[$target.Id]=$cpu;[pscustomobject]@{Role=$target.Role;PrivateMiB=$process.PrivateMemorySize64/1048576;WorkingSetMiB=$process.WorkingSet64/1048576;CpuOneCorePercent=$delta}}
                        $samples.Add([pscustomobject]@{Seconds=$time;AppPrivateMiB=($measurements | Where-Object Role -in 'Dashboard','Host'|Measure-Object PrivateMiB -Sum).Sum;AllPrivateMiB=($measurements | Measure-Object PrivateMiB -Sum).Sum;AppCpuOneCorePercent=($measurements | Where-Object Role -in 'Dashboard','Host'|Measure-Object CpuOneCorePercent -Sum).Sum;AllCpuOneCorePercent=($measurements | Measure-Object CpuOneCorePercent -Sum).Sum;Rows=@($measurements)})
                        $previousTime=$time;Start-Sleep -Seconds 1
                    }while($phaseClock.Elapsed.TotalSeconds -lt $phase.Seconds)
                    if($load){if(!$load.WaitForExit(10000)){throw 'Load driver failed to stop'};if($load.ExitCode -ne 0){throw 'Payload validation or transfer failed'}}
                }finally{StopOwned $load}
                $growth=($samples|Select-Object -Last 8|Measure-Object AppPrivateMiB -Average).Average-($samples|Select-Object -First 8|Measure-Object AppPrivateMiB -Average).Average
                $stats=[pscustomobject]@{Name=$phase.Name;Seconds=$phaseClock.Elapsed.TotalSeconds;ForegroundRequestSucceeded=$foreground;MeanAppPrivateMiB=($samples|Measure-Object AppPrivateMiB -Average).Average;PeakAppPrivateMiB=($samples|Measure-Object AppPrivateMiB -Maximum).Maximum;MeanAllPrivateMiB=($samples|Measure-Object AllPrivateMiB -Average).Average;MeanAppCpuOneCorePercent=($samples|Select-Object -Skip 1|Measure-Object AppCpuOneCorePercent -Average).Average;MeanAllCpuOneCorePercent=($samples|Select-Object -Skip 1|Measure-Object AllCpuOneCorePercent -Average).Average;AppPrivateGrowthMiB=$growth;Transfer=if($phase.Load){Get-Content -LiteralPath $loadReport -Raw|ConvertFrom-Json}else{$null};Samples=$samples.ToArray()}
                $phases.Add($stats);Write-Host ("{0} round {1}: {2}; app memory {3:N1} MiB, app CPU {4:N2}% of one core" -f $product,$round,$phase.Name,$stats.MeanAppPrivateMiB,$stats.MeanAppCpuOneCorePercent)
            }
            $health=if($product -eq 'Conduit'){Broker 'status'}else{$lastStatus=Get-Content -LiteralPath (Join-Path $data 'state\status.json') -Raw|ConvertFrom-Json;@{State=$lastStatus.state;Restarts=$lastStatus.recovery.restarts_in_window}}
            $run=[pscustomobject]@{Product=$product;Version=if($product -eq 'Conduit'){(Get-Item -LiteralPath $app).VersionInfo.FileVersion}else{$manifest.version};Round=$round;StartupToFirstVerifiedPayloadSeconds=$startup;FinalHealth=$health;Phases=$phases.ToArray()}
            $runs.Add($run)
            JsonFile $reportFile @{Schema=1;Complete=$false;Method='Two counterbalanced rounds; collection still in progress';Runs=$runs.ToArray()}
            if($product -eq 'Conduit'){$null=Broker 'shutdown'}
            StopOwned $ui;foreach($process in $owned){StopOwned $process};$ui=$null;$owned=@();Start-Sleep -Seconds 3
        }
    }
    $failures=[Collections.Generic.List[string]]::new()
    foreach($run in $runs | Where-Object Product -eq 'Conduit'){
        $soak=$run.Phases|Where-Object Name -eq 'Sustained 2 MiB/s'
        if($soak.PeakAppPrivateMiB -gt 128){$failures.Add('Conduit app plus host exceeded 128 MiB in the sustained fixture')}
        if($soak.AppPrivateGrowthMiB -gt 64){$failures.Add('Conduit app plus host grew by more than 64 MiB during sustained load')}
        if($soak.Transfer.MiBPerSecond -lt 1.8){$failures.Add('Conduit did not sustain the fixture target of at least 1.8 MiB/s')}
        foreach($idle in $run.Phases|Where-Object Name -like 'Idle*'){if($idle.MeanAppCpuOneCorePercent -gt 15){$failures.Add('Conduit idle app plus host exceeded 15 percent of one core')}}
    }
    $result=[pscustomobject]@{Schema=1;Complete=$true;Utc=[DateTime]::UtcNow.ToString('o');LogicalProcessors=[Environment]::ProcessorCount;Cpu=(Get-CimInstance Win32_Processor|Select-Object -First 1).Name;OS=[Environment]::OSVersion.VersionString;
        Method='Actual installed Conduit Client and publisher-signed TunMon executable, two counterbalanced rounds on one disposable Windows runner; identical pinned engine bytes, plain local VLESS fixture, MTU 1400 and gVisor; leak blocking off in both';
        Limits='Local HTTP throughput excludes WAN latency and TLS. Foreground focus requests may be refused by Windows. Imported benchmark configs align transport options and differ from TunMon default routing. This is a resource and local-transport comparison, not a security ranking, battery test or day-long soak.';
        TunMonManifestSignatureVerified=$true;TunMonExecutableSha256=$manifest.exe.sha256;EnginePins=Get-Content -LiteralPath (Join-Path $repository 'engines.lock.json') -Raw|ConvertFrom-Json;Runs=$runs.ToArray();ConduitBudgetPassed=$failures.Count -eq 0;Failures=$failures.ToArray()}
    JsonFile $reportFile $result
    if($failures.Count){throw ($failures -join '; ')}
    Write-Host "PASS controlled resource/transport comparison and Conduit sustained-load budgets: $reportFile"
}finally{
    if($ui -and !$ui.HasExited -and $product -eq 'Conduit'){try{$null=Broker 'shutdown'}catch{}}
    StopOwned $ui;foreach($process in $owned){StopOwned $process};StopOwned $relay;StopOwned $server
}
