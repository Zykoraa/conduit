<#
  tether-diag.ps1  -  diagnose why sites are blocked only through the phone path
  Read-only. No admin rights needed. Changes nothing.

  Usage (run while tethered through the phone):
      powershell -ExecutionPolicy Bypass -File .\tether-diag.ps1 site1.com site2.com
  If you pass no sites it uses a couple of defaults - edit them or pass your own.
#>

param(
    [string[]]$Sites = @('www.reddit.com','www.youtube.com')
)

function Line { param($c='-') Write-Host ($c * 64) }

# --- helper: TCP connect test with a short timeout, no admin needed ---
function Test-Tcp {
    param([string]$Target,[int]$Port=443,[int]$TimeoutMs=3000)
    try {
        $c = New-Object System.Net.Sockets.TcpClient
        $iar = $c.BeginConnect($Target,$Port,$null,$null)
        $ok  = $iar.AsyncWaitHandle.WaitOne($TimeoutMs,$false)
        if ($ok -and $c.Connected) { $c.EndConnect($iar); $c.Close(); return $true }
        $c.Close(); return $false
    } catch { return $false }
}

# --- helper: DNS lookup, returns list of A-record IPs (or $null on failure) ---
function Resolve-A {
    param([string]$Name,[string]$Server)
    try {
        $p = @{ Name = $Name; Type = 'A'; ErrorAction = 'Stop' }
        if ($Server) { $p.Server = $Server; $p.DnsOnly = $true }
        (Resolve-DnsName @p | Where-Object { $_.QueryType -eq 'A' }).IPAddress
    } catch { $null }
}

Line '='
Write-Host "TETHER DIAGNOSTIC   $(Get-Date -Format 'yyyy-MM-dd HH:mm')" -ForegroundColor Cyan
Line '='

# ---------------------------------------------------------------
# 1. Which adapter am I actually using, and what's its MTU?
# ---------------------------------------------------------------
Write-Host "`n[1] ACTIVE ADAPTER / MTU" -ForegroundColor Yellow
$defRoute = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
            Sort-Object RouteMetric | Select-Object -First 1
$gw = $defRoute.NextHop
$if = Get-NetIPInterface -InterfaceIndex $defRoute.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue
Write-Host ("  Adapter : {0}" -f $if.InterfaceAlias)
Write-Host ("  Gateway : {0}" -f $gw)
Write-Host ("  MTU     : {0}" -f $if.NlMtu)
$dns = (Get-DnsClientServerAddress -InterfaceIndex $defRoute.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue).ServerAddresses
Write-Host ("  DNS srv : {0}" -f ($dns -join ', '))

# ---------------------------------------------------------------
# 2. MTU probe - find largest packet that passes unfragmented
# ---------------------------------------------------------------
Write-Host "`n[2] MTU PROBE (to $gw)" -ForegroundColor Yellow
$best = 0
foreach ($payload in 1472,1400,1372,1300,1200) {
    $out = & ping.exe -n 1 -f -l $payload $gw
    if ($out -match 'Reply from') { $best = $payload; break }
    Write-Host ("  {0,4} bytes payload : blocked/fragmented" -f $payload) -ForegroundColor DarkGray
}
if ($best -gt 0) {
    $mtu = $best + 28
    Write-Host ("  {0,4} bytes payload : OK  ->  usable MTU ~= {1}" -f $best,$mtu) -ForegroundColor Green
    if ($mtu -lt 1500) {
        Write-Host ("  NOTE: path MTU ($mtu) is below 1500. This alone can make some") -ForegroundColor Magenta
        Write-Host  "        HTTPS sites hang or load half-broken through the phone." -ForegroundColor Magenta
    }
} else {
    Write-Host "  Could not get an unfragmented reply (gateway may block ping)." -ForegroundColor DarkGray
}

# ---------------------------------------------------------------
# 3. Per-site: DNS comparison + TCP reachability + HTTPS fetch
# ---------------------------------------------------------------
Write-Host "`n[3] PER-SITE CHECKS" -ForegroundColor Yellow
foreach ($site in $Sites) {
    Line
    Write-Host "SITE: $site" -ForegroundColor Cyan

    $local = Resolve-A -Name $site                       # via phone/work DNS
    $cf    = Resolve-A -Name $site -Server '1.1.1.1'      # forced Cloudflare

    Write-Host ("  DNS (current) : {0}" -f ($(if($local){$local -join ', '}else{'FAILED'})))
    Write-Host ("  DNS (1.1.1.1) : {0}" -f ($(if($cf){$cf -join ', '}else{'FAILED'})))

    $verdict = @()

    if (-not $local -and $cf) {
        $verdict += "DNS FILTERING: current DNS won't resolve this but Cloudflare does."
        $verdict += "  -> Fix: browser Secure DNS = Cloudflare, or phone Private DNS = one.one.one.one"
    }
    elseif ($local -and $cf) {
        $shared = $local | Where-Object { $cf -contains $_ }
        if (-not $shared) {
            $verdict += "DNS REDIRECT: current DNS returns different IPs than Cloudflare (likely a block/redirect)."
            $verdict += "  -> Fix: browser Secure DNS = Cloudflare, or phone Private DNS = one.one.one.one"
        }
    }

    # reachability - test against a real resolved IP if we have one
    $ip = if ($local) { $local[0] } elseif ($cf) { $cf[0] } else { $null }
    if ($ip) {
        $tcp = Test-Tcp -Target $ip -Port 443
        Write-Host ("  TCP 443       : {0}" -f ($(if($tcp){'reachable'}else{'BLOCKED / no response'}))) `
            -ForegroundColor ($(if($tcp){'Green'}else{'Red'}))

        if ($tcp) {
            try {
                $r = Invoke-WebRequest -Uri "https://$site" -UseBasicParsing -TimeoutSec 8 -MaximumRedirection 3 -ErrorAction Stop
                Write-Host ("  HTTPS GET     : HTTP {0}" -f [int]$r.StatusCode) -ForegroundColor Green
            } catch {
                $code = $_.Exception.Response.StatusCode.value__
                if ($code) { Write-Host ("  HTTPS GET     : HTTP {0} (possible block page / proxy)" -f $code) -ForegroundColor Red }
                else       { Write-Host ("  HTTPS GET     : failed - {0}" -f $_.Exception.Message) -ForegroundColor Red }
            }
        } else {
            $verdict += "CONNECTION BLOCKED: DNS is fine but TCP 443 to the real IP fails."
            $verdict += "  -> This is SNI/IP-level or a restricted network segment for the phone (a deliberate work control)."
            $verdict += "  -> DNS tricks will NOT help this case."
        }
    }

    if ($verdict.Count -eq 0) { $verdict += "Looks unblocked from here right now (DNS + TCP + HTTPS all OK)." }
    Write-Host "  VERDICT:" -ForegroundColor Yellow
    $verdict | ForEach-Object { Write-Host "    $_" }
}

Line '='
Write-Host "Summary:" -ForegroundColor Cyan
Write-Host "  * 'DNS FILTERING/REDIRECT' -> browser Secure DNS or phone Private DNS fixes it (no admin)."
Write-Host "  * 'CONNECTION BLOCKED'     -> not DNS; it's IP/SNI or a restricted segment. DNS won't help."
Write-Host "  * MTU below 1500           -> can cause the 'loads half-broken / hangs' symptom."
Line '='
