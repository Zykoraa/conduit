using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

const int payloadBytes = 1048576;
byte[] payload = new byte[payloadBytes]; Array.Fill(payload, (byte)42);
string expectedHash = Convert.ToHexString(SHA256.HashData(payload));
if (args.Length == 2 && args[0] == "serve")
{
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    File.WriteAllText(args[1], ((IPEndPoint)listener.LocalEndpoint).Port.ToString());
    async Task Serve(TcpClient client)
    {
        using (client)
        try
        {
            var stream = client.GetStream(); using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true);
            while (await reader.ReadLineAsync() is { Length: > 0 } request)
            {
                int headers = request.Length;
                while (await reader.ReadLineAsync() is { Length: > 0 } line) { headers += line.Length; if (headers > 16384) throw new IOException("Headers too large."); }
                if (!request.StartsWith("GET /payload ")) throw new IOException("Invalid fixture request.");
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {payloadBytes}\r\nContent-Type: application/octet-stream\r\nConnection: keep-alive\r\n\r\n"));
                await stream.WriteAsync(payload);
            }
        }
        catch (Exception e) when (e is IOException or SocketException) { }
    }
    while (true) { var client = await listener.AcceptTcpClientAsync(); _ = Serve(client); }
}
if (args.Length is 6 or 7 && args[0] == "load")
{
    var uri = new Uri(args[1]);
    if (uri.Scheme != "http" || !(uri.Host == "1.0.0.1" || uri.IsLoopback)) throw new ArgumentException("Only the fixture target is permitted.");
    int seconds = int.Parse(args[2]), workers = int.Parse(args[3]); double rate = double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);
    if (seconds is < 1 or > 3600 || workers is < 1 or > 8 || rate < 0 || rate > 256) throw new ArgumentException("Invalid bounded workload.");
    Uri? proxy = args.Length == 7 ? new Uri(args[6]) : null;
    if (proxy != null && (proxy.Scheme != "socks5" || !proxy.IsLoopback || proxy.UserInfo.Length != 0 || proxy.AbsolutePath != "/" || proxy.Query.Length != 0)) throw new ArgumentException("Diagnostic proxy must be a local SOCKS fixture.");
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    var clock = Stopwatch.StartNew(); long bytes = 0, completed = 0, failed = 0; string? lastFailure = null; var durations = new System.Collections.Concurrent.ConcurrentBag<double>();
    async Task Worker()
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = proxy != null, Proxy = proxy != null ? new WebProxy(proxy) : null, AllowAutoRedirect = false, MaxConnectionsPerServer = 1 }) { Timeout = TimeSpan.FromSeconds(8) };
        long ownBytes = 0;
        while (!deadline.IsCancellationRequested)
        {
            var requestClock = Stopwatch.StartNew();
            try
            {
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                response.EnsureSuccessStatusCode(); if (response.Content.Headers.ContentLength != payloadBytes) throw new IOException("Fixture size mismatch.");
                using var source = await response.Content.ReadAsStreamAsync(deadline.Token); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[65536]; int count, total = 0;
                while ((count = await source.ReadAsync(buffer, deadline.Token)) != 0) { total += count; if (total > payloadBytes) throw new IOException("Oversized payload."); hash.AppendData(buffer, 0, count); }
                if (total != payloadBytes || Convert.ToHexString(hash.GetHashAndReset()) != expectedHash) throw new IOException("Fixture bytes changed.");
                Interlocked.Add(ref bytes, total); Interlocked.Increment(ref completed); ownBytes += total; durations.Add(requestClock.Elapsed.TotalMilliseconds);
                if (rate > 0)
                {
                    double delay = ownBytes / (rate * 1048576 / workers) - clock.Elapsed.TotalSeconds;
                    if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(Math.Min(delay, 2)), deadline.Token);
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { break; }
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException) { Interlocked.Increment(ref failed); Interlocked.Exchange(ref lastFailure, e.GetType().Name + ": " + e.Message); try { await Task.Delay(100, deadline.Token); } catch (OperationCanceledException) { break; } }
        }
    }
    await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Worker()));
    var ordered = durations.Order().ToArray();
    var result = new { Seconds = clock.Elapsed.TotalSeconds, Workers = workers, TargetMiBPerSecond = rate, VerifiedBytes = bytes, CompletedRequests = completed, FailedRequests = failed,
        MiBPerSecond = bytes / 1048576.0 / clock.Elapsed.TotalSeconds, MedianRequestMs = ordered.Length == 0 ? (double?)null : ordered[ordered.Length / 2], LastFailure = lastFailure,
        Scenario = proxy == null ? "Identical local HTTP payload through TUN, SOCKS and VLESS; no WAN capacity measurement" : "Diagnostic local SOCKS fixture; excludes TUN" };
    File.WriteAllText(args[5], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    if (completed == 0 || failed > 0) { Environment.ExitCode = 1; Console.Error.WriteLine(lastFailure ?? "No complete fixture payload before the deadline."); }
    return;
}
throw new ArgumentException("Usage: serve <ready-file> | load <fixture-url> <seconds> <workers> <MiB/s-or-0> <report> [local-socks-diagnostic-proxy]");
