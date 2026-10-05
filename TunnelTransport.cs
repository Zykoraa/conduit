using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace WorkTunnel;

/// <summary>Conduit's own probes use tunnel DNS and never fall back to the Windows resolver.</summary>
internal static class TunnelDns
{
    internal static byte[] Query(string host)
    {
        string[] labels = host.TrimEnd('.').Split('.');
        if (host.Length is < 1 or > 253 || labels.Any(l => l.Length is < 1 or > 63 ||
            l.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-'))))
            throw new ArgumentException("Invalid probe hostname.");
        var packet = new List<byte>(RandomNumberGenerator.GetBytes(2));
        packet.AddRange([1, 0, 0, 1, 0, 0, 0, 0, 0, 0]);
        foreach (string label in labels) { packet.Add((byte)label.Length); packet.AddRange(Encoding.ASCII.GetBytes(label)); }
        packet.AddRange([0, 0, 1, 0, 1]);
        return packet.ToArray();
    }

    internal static async Task<IPAddress> ResolveIPv4Async(string host, CancellationToken ct,
        Func<byte[], CancellationToken, Task<byte[]>>? exchange = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        byte[] query = Query(host);
        byte[] reply;
        if (exchange != null) reply = await exchange(query, deadline.Token);
        else
        {
            using var socket = new UdpClient(new IPEndPoint(TunnelHealthChecker.TunAddress, 0));
            socket.Connect(new IPEndPoint(IPAddress.Parse("172.19.0.2"), 53));
            await socket.SendAsync(query, deadline.Token);
            reply = (await socket.ReceiveAsync(deadline.Token)).Buffer;
        }
        deadline.Token.ThrowIfCancellationRequested();
        var addresses = ParseReply(reply, query);
        return addresses.FirstOrDefault() ?? throw new IOException("Tunnel DNS returned no valid IPv4 answer.");
    }

    internal static IPAddress[] ParseReply(byte[] packet, byte[] query)
    {
        try
        {
            if (packet.Length is < 12 or > 4096 || query.Length < 17 ||
                !packet.AsSpan(0, 2).SequenceEqual(query.AsSpan(0, 2)) ||
                (packet[2] & 0xfa) != 0x80 || (packet[3] & 0x0f) != 0 || Read16(packet, 4) != 1 || Read16(query, 4) != 1)
                return [];
            int q = 12; string wanted = Name(query, ref q);
            if (Read16(query, q) != 1 || Read16(query, q + 2) != 1 || q + 4 != query.Length) return [];
            int offset = 12; string question = Name(packet, ref offset);
            if (!question.Equals(wanted, StringComparison.OrdinalIgnoreCase) || Read16(packet, offset) != 1 || Read16(packet, offset + 2) != 1) return [];
            offset += 4;
            int answers = Read16(packet, 6), total = answers + Read16(packet, 8) + Read16(packet, 10);
            if (answers is < 1 or > 64 || total > 128) return [];
            var ips = new List<(string Owner, IPAddress Address)>();
            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int record = 0; record < total; record++)
            {
                string owner = Name(packet, ref offset);
                int type = Read16(packet, offset), cls = Read16(packet, offset + 2), length = Read16(packet, offset + 8);
                offset += 10; int end = checked(offset + length);
                if (end > packet.Length) return [];
                // Only answers for the requested name or its CNAME chain count; additional records cannot satisfy it.
                if (record < answers && cls == 1)
                {
                    if (type == 1)
                    {
                        if (length != 4) return [];
                        ips.Add((owner, new IPAddress(packet.AsSpan(offset, 4))));
                    }
                    else if (type == 5)
                    {
                        int aliasOffset = offset; string alias = Name(packet, ref aliasOffset);
                        if (aliasOffset != end || !aliases.TryAdd(owner, alias)) return [];
                    }
                }
                offset = end;
            }
            if (offset != packet.Length) return [];
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { wanted };
            string name = wanted;
            while (aliases.TryGetValue(name, out string? alias))
            { if (!names.Add(alias)) return []; name = alias; }
            return ips.Where(i => i.Owner.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(i => i.Address).Distinct().ToArray();
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException or OverflowException) { return []; }
    }

    private static int Read16(byte[] packet, int offset) => BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset, 2));
    private static string Name(byte[] packet, ref int offset)
    {
        var labels = new List<string>(); int position = offset, consumed = -1, size = 0;
        for (int step = 0; step < 128; step++)
        {
            if (position >= packet.Length) throw new InvalidDataException("Truncated DNS name.");
            byte length = packet[position++];
            if (length == 0) { offset = consumed < 0 ? position : consumed; return string.Join('.', labels); }
            if ((length & 0xc0) == 0xc0)
            {
                if (position >= packet.Length) throw new InvalidDataException("Truncated DNS pointer.");
                int target = ((length & 0x3f) << 8) | packet[position++];
                if (target < 12 || target >= position - 2) throw new InvalidDataException("Invalid DNS pointer.");
                if (consumed < 0) consumed = position;
                position = target; continue;
            }
            if (length > 63 || position + length > packet.Length) throw new InvalidDataException("Invalid DNS label.");
            size += length + 1;
            if (size > 254) throw new InvalidDataException("Oversized DNS name.");
            var label = packet.AsSpan(position, length);
            foreach (byte c in label) if (c is < 33 or > 126 || c == '.') throw new InvalidDataException("Invalid DNS label.");
            labels.Add(Encoding.ASCII.GetString(label)); position += length;
        }
        throw new InvalidDataException("Too many DNS pointers.");
    }
}

internal static class TunnelTransport
{
    internal static SocketsHttpHandler CreateHandler(bool bindTunnel = true, Func<bool>? allowed = null)
    {
        return new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false,
            ConnectCallback = async (context, token) =>
            {
                DemandAllowed(allowed);
                var address = await TunnelDns.ResolveIPv4Async(context.DnsEndPoint.Host, token);
                DemandAllowed(allowed); token.ThrowIfCancellationRequested();
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    if (bindTunnel) socket.Bind(new IPEndPoint(TunnelHealthChecker.TunAddress, 0));
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token);
                    DemandAllowed(allowed);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
    }
    internal static void DemandAllowed(Func<bool>? allowed)
    { if (allowed?.Invoke() == false) throw new IOException("The verified tunnel and network lock are required for this request."); }
}
