using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using WorkTunnel;

internal static class NativeChecks
{
    public static async Task CheckJob(Action<bool, string> check)
    {
        using var host = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { Arguments = "--job-parent", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        try
        {
            string childId = await host.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8)) ?? throw new IOException("Child did not start");
            using var child = Process.GetProcessById(int.Parse(childId));
            host.Kill(); await host.WaitForExitAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            check(child.HasExited, "A host crash terminates inherited engine processes");
        }
        finally { if (!host.HasExited) host.Kill(true); }
    }
    public static async Task CheckBroker(Action<bool, string> check)
    {
        RequireDisposableRunner();
        string app = Path.Combine(InstallSecurity.InstallRoot, "WorkTunnel.exe");
        using var host = Process.Start(new ProcessStartInfo(app) { Arguments = "--tunnel-host", UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            await Task.Delay(2000);
            using (var client = new BrokerClient()) { await client.EnsureHostAsync(); check(client.State == TunnelState.Disconnected, "Installed host answers an authenticated dashboard client"); }
            check(!host.HasExited, "Closing the dashboard leaves its host running");
            using (var client = new BrokerClient()) { await client.EnsureHostAsync(); await client.ShutdownAsync(); }
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            check(host.HasExited, "A new dashboard reattaches and explicitly shuts down its host");
        }
        finally { if (!host.HasExited) host.Kill(true); }
    }
    public static void RequireDisposableRunner()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" || Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted" || !InstallSecurity.IsAdministrator)
            throw new InvalidOperationException("Network mutation tests run only on a disposable GitHub-hosted administrator runner.");
    }
    public static async Task Run(Action<bool, string> check, string core)
    {
        RequireDisposableRunner();
        async Task<bool> Tcp(string ip, int port, bool diagnostic = false)
        {
            try { var address = IPAddress.Parse(ip); using var socket = new TcpClient(address.AddressFamily); using var cancel = new CancellationTokenSource(2000); await socket.ConnectAsync(address, port, cancel.Token); return true; }
            catch (Exception e) { if (diagnostic) Console.WriteLine("INFO fixture connection: " + e.GetType().Name + " " + e.Message); return false; }
        }
        async Task<bool> Udp(string ip)
        {
            try
            {
                var address = IPAddress.Parse(ip); using var socket = new UdpClient(address.AddressFamily); using var cancel = new CancellationTokenSource(2000);
                byte[] payload = Encoding.ASCII.GetBytes("NATIVE-DUAL-STACK-UDP");
                await socket.SendAsync(payload, new IPEndPoint(address, 9001), cancel.Token);
                var reply = await socket.ReceiveAsync(cancel.Token);
                return reply.Buffer.SequenceEqual(payload) && reply.RemoteEndPoint.Address.Equals(address);
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException or IOException) { return false; }
        }
        const string externalV6 = "2001:db8:203::10", tunnelV6 = "2001:db8:204::10";
        check(await Tcp("1.1.1.1", 443) && await Tcp("1.0.0.1", 443), "Both external test endpoints are reachable before filtering");
        bool ipv6 = await Tcp("2606:4700:4700::1111", 443);
        using var echo = new TcpListener(IPAddress.Loopback, 0); echo.Start();
        int echoPort = ((IPEndPoint)echo.LocalEndpoint).Port;
        string folder = Path.Combine(Path.GetTempPath(), "WorkTunnel-native-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        Process? tun = null, ipv6Fixture = null;
        using var mock = new TcpListener(IPAddress.Loopback, 0); mock.Start();
        using var udpMock = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var stop = new CancellationTokenSource();
        var udpServing = Task.Run(async () =>
        {
            try { while (!stop.IsCancellationRequested) { var datagram = await udpMock.ReceiveAsync(stop.Token); await udpMock.SendAsync(datagram.Buffer, datagram.RemoteEndPoint, stop.Token); } }
            catch (OperationCanceledException) { }
        });
        async Task Respond(TcpClient client)
        {
            using (client)
            try
            {
                var stream = client.GetStream(); byte[] hello = new byte[2]; await stream.ReadExactlyAsync(hello, stop.Token);
                await stream.ReadExactlyAsync(new byte[hello[1]], stop.Token); await stream.WriteAsync(new byte[] { 5, 0 }, stop.Token);
                byte[] connect = new byte[4]; await stream.ReadExactlyAsync(connect, stop.Token);
                int address = connect[3] == 1 ? 4 : connect[3] == 4 ? 16 : stream.ReadByte();
                await stream.ReadExactlyAsync(new byte[address + 2], stop.Token);
                if (connect[1] == 3)
                {
                    int port = ((IPEndPoint)udpMock.Client.LocalEndPoint!).Port;
                    await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, (byte)(port >> 8), (byte)port }, stop.Token);
                    await Task.Delay(Timeout.Infinite, stop.Token); return;
                }
                await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 80 }, stop.Token);
                using var request = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                int headerBytes = 0;
                while (await request.ReadLineAsync(stop.Token) is { Length: > 0 } line)
                { headerBytes += line.Length; if (headerBytes > 8192) throw new IOException("Oversized test request"); }
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 9\r\nConnection: close\r\n\r\nTUN-PASS!"), stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException) { }
        }
        var serving = Task.Run(async () => { try { while (!stop.IsCancellationRequested) { var c = await mock.AcceptTcpClientAsync(stop.Token); _ = Respond(c); } } catch (OperationCanceledException) { } });
        Process StartTun(string name, string[] addresses, string[] routes)
        {
            string config = Path.Combine(folder, name + ".json");
            File.WriteAllText(config, JsonSerializer.Serialize(new {
                log = new { level = "warn" },
                inbounds = new[] { new { type = "tun", tag = "tun", interface_name = name, address = addresses, mtu = 1400, auto_route = true, strict_route = false, route_address = routes, stack = "gvisor" } },
                outbounds = new[] { new { type = "socks", tag = "proxy", server = "127.0.0.1", server_port = ((IPEndPoint)mock.LocalEndpoint).Port, version = "5" } },
                route = new { auto_detect_interface = true, final = "proxy" }
            }));
            var start = new ProcessStartInfo(Path.GetFullPath(core)) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("run"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(config);
            return Process.Start(start)!;
        }
        try
        {
            // Real reachable IPv6 packets on a separate non-exempt interface, even on IPv4-only CI hosts.
            // This exercises transport filtering; it does not establish IPv6 internet availability.
            ipv6Fixture = StartTun("conduit-v6", ["172.29.0.1/30", "fdcd:203::1/126"], [externalV6 + "/128"]);
            for (int i = 0; i < 40 && !ipv6Fixture.HasExited; i++)
            {
                if (NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name == "conduit-v6" && n.OperationalStatus == OperationalStatus.Up &&
                    n.GetIPProperties().UnicastAddresses.Any(a => a.Address.ToString() == "fdcd:203::1" && a.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred))) break;
                await Task.Delay(250);
            }
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.Name == "conduit-v6"))
                Console.WriteLine("INFO fixture interface: " + nic.OperationalStatus + " " + string.Join(", ", nic.GetIPProperties().UnicastAddresses.Select(a => a.Address + " " + a.DuplicateAddressDetectionState)));
            bool fixtureReady = false;
            for (int attempt = 0; attempt < 3 && !ipv6Fixture.HasExited && !fixtureReady; attempt++) { fixtureReady = await Tcp(externalV6, 80, true); if (!fixtureReady) await Task.Delay(500); }
            check(fixtureReady, "Independent non-exempt IPv6 fixture is reachable before filtering");
            check(await Udp(externalV6), "IPv6 UDP fixture has a successful baseline reply");
            NetworkLock.Apply("1.1.1.1", 443, 0);
            check(NetworkLock.Observe() is { PolicyPresent: true, Known: true, BlockingVerified: true }, "Actual WFP inspection confirms both persistent blocking layers");
            RemoveIpv6Block();
            check(NetworkLock.HasPolicy() && NetworkLock.Observe() is { PolicyPresent: true, Known: true, BlockingVerified: false },
                "A surviving sublayer with a missing IPv6 block cannot report an active lock");
            check(await Tcp(externalV6, 80), "Removing the IPv6 block restores fixture packets and exposes the missing rule");
            NetworkLock.Apply("1.1.1.1", 443, 0);
            check(NetworkLock.IsActive(), "Transactional policy repair restores confirmed dual-stack blocking");
            check(!await Tcp(externalV6, 80) && !await Udp(externalV6), "Repaired lock blocks reachable IPv6 TCP and UDP on a non-exempt interface");
            check(await Tcp("1.1.1.1", 443), "Network lock permits only the configured external TCP endpoint");
            check(!await Tcp("1.0.0.1", 443), "Network lock blocks another reachable external endpoint");
            check(await Tcp("127.0.0.1", echoPort), "Network lock permits local engine communication");
            if (ipv6) check(!await Tcp("2606:4700:4700::1111", 443), "Network lock blocks physical IPv6 egress");
            else Console.WriteLine("INFO IPv6 internet baseline unavailable; deterministic IPv6 packet fixture remains required");
            NetworkLock.Remove();
            check(await Tcp("1.0.0.1", 443), "Explicit recovery restores ordinary outbound connectivity");
            check(await Tcp(externalV6, 80) && await Udp(externalV6), "Explicit recovery restores IPv6 TCP and UDP fixture traffic");

            // Closing the applying process is not sufficient to lift a persistent lock.
            var childStart = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            childStart.ArgumentList.Add("--apply-lock-and-exit");
            using (var child = Process.Start(childStart)!) { await child.WaitForExitAsync(); check(child.ExitCode == 0, "Separate lock owner applied its policy"); }
            check(!await Tcp("1.0.0.1", 443), "Network lock survives the applying process exiting");
            check(!await Tcp(externalV6, 80), "IPv6 blocking survives the applying process exiting");

            // Route ONLY a documentation/test address to a real TUN; mock SOCKS returns a deterministic response.
            tun = StartTun("worktunnel", ["172.19.0.1/30", "fdcd:204::1/126"], ["198.51.100.1/32", tunnelV6 + "/128"]);
            ulong luid = 0;
            for (int i = 0; i < 40 && luid == 0 && !tun.HasExited; i++) { await Task.Delay(250); luid = NetworkLock.TunnelLuid(); }
            check(luid != 0, "Bundled sing-box creates the test TUN adapter");
            NetworkLock.Apply("1.1.1.1", 443, luid);
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
            check(await http.GetStringAsync("http://198.51.100.1/") == "TUN-PASS!", "Network lock permits traffic routed through the actual TUN interface");
            check(await http.GetStringAsync("http://[" + tunnelV6 + "]/") == "TUN-PASS!" && await Udp(tunnelV6),
                "Network lock permits actual IPv6 TCP and UDP through the selected TUN interface");
            using (var udp = new UdpClient())
            using (var deadline = new CancellationTokenSource(5000))
            {
                byte[] payload = Encoding.ASCII.GetBytes("TUN-UDP-PASS");
                await udp.SendAsync(payload, new IPEndPoint(IPAddress.Parse("198.51.100.1"), 9001), deadline.Token);
                var reply = await udp.ReceiveAsync(deadline.Token);
                check(reply.Buffer.SequenceEqual(payload), "UDP traverses the actual TUN and SOCKS adapter while network lock is active");
            }
            check(!await Tcp("1.0.0.1", 443), "Physical bypass stays blocked while the TUN is healthy");
            check(!await Tcp(externalV6, 80), "The selected TUN exception does not unlock a separate IPv6 interface");
            tun.Kill(true); await tun.WaitForExitAsync();
            check(!await Tcp("1.0.0.1", 443), "TUN crash does not unlock physical egress");
            check(!await Tcp(externalV6, 80) && !await Udp(externalV6), "TUN crash does not unlock reachable IPv6 TCP or UDP on another interface");
            NetworkLock.Remove();
            check(await Tcp(externalV6, 80) && await Udp(externalV6), "Post-crash explicit recovery restores IPv6 fixture traffic");
        }
        finally
        {
            if (tun != null) { if (!tun.HasExited) { tun.Kill(true); await tun.WaitForExitAsync(); } tun.Dispose(); }
            NetworkLock.Remove();
            if (ipv6Fixture != null) { if (!ipv6Fixture.HasExited) { ipv6Fixture.Kill(true); await ipv6Fixture.WaitForExitAsync(); } ipv6Fixture.Dispose(); }
            stop.Cancel(); mock.Stop(); await Task.WhenAll(serving, udpServing); Directory.Delete(folder, true);
        }
        check(await Tcp("1.0.0.1", 443), "Test teardown restores the runner's network");
    }
    private static unsafe void RemoveIpv6Block()
    {
        RequireDisposableRunner();
        Windows.Win32.NetworkManagement.WindowsFilteringPlatform.FWPM_ENGINE_HANDLE engine;
        uint result = Windows.Win32.PInvoke.FwpmEngineOpen0(null, 10, null, null, &engine);
        if (result != 0) throw new System.ComponentModel.Win32Exception((int)result);
        try
        {
            Guid key = new("95a2e590-31dd-440a-91a3-000000000004");
            result = Windows.Win32.PInvoke.FwpmFilterDeleteByKey0(engine, &key);
            if (result != 0) throw new System.ComponentModel.Win32Exception((int)result);
        }
        finally { Windows.Win32.PInvoke.FwpmEngineClose0(engine); }
    }
}
