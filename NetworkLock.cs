using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.WindowsFilteringPlatform;
using Windows.Win32.NetworkManagement.Ndis;

namespace WorkTunnel;

internal sealed record NetworkLockObservation(bool PolicyPresent, bool BlockingVerified, bool Known, string Detail)
{
    internal static NetworkLockObservation Unavailable { get; } = new(false, false, false, "Windows network-lock status could not be read.");
}

/// <summary>Dedicated persistent WFP sublayer. Never changes Windows Firewall profiles or other providers.</summary>
internal static unsafe class NetworkLock
{
    private static readonly Guid Sublayer = new("d6bd188e-9957-4a4a-99c8-811c1310bc35");
    private static Guid Key(int number) => new($"95a2e590-31dd-440a-91a3-{number:000000000000}");
    private const int FilterCount = 9;
    private static void Check(uint result) { if (result != 0) throw new Win32Exception(unchecked((int)result), "Network lock operation failed (0x" + result.ToString("X8") + ")."); }
    private static FWPM_ENGINE_HANDLE Open()
    {
        FWPM_ENGINE_HANDLE handle; Check(PInvoke.FwpmEngineOpen0(null, 10, null, null, &handle)); return handle;
    }
    public static ulong TunnelLuid()
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == "worktunnel" && n.OperationalStatus == OperationalStatus.Up);
        if (nic == null) return 0;
        NET_LUID_LH luid;
        Check((uint)PInvoke.ConvertInterfaceIndexToLuid((uint)nic.GetIPProperties().GetIPv4Properties().Index, &luid)); return luid.Value;
    }
    public static bool HasPolicy()
    {
        var engine = Open();
        FWPM_SUBLAYER0* layer = null;
        try { Guid key = Sublayer; uint result = PInvoke.FwpmSubLayerGetByKey0(engine, &key, &layer); if (result == 0x80320007) return false; Check(result); return true; }
        finally { if (layer != null) { void* memory = layer; PInvoke.FwpmFreeMemory0(&memory); } PInvoke.FwpmEngineClose0(engine); }
    }
    public static bool IsActive() => Observe().BlockingVerified;
    public static NetworkLockObservation Observe()
    {
        try
        {
            var engine = Open();
            try
            {
                // Read both layers in a single snapshot, including during transactional replacement.
                Check(PInvoke.FwpmTransactionBegin0(engine, PInvoke.FWPM_TXN_READ_ONLY));
                try
                {
                    FWPM_SUBLAYER0* layer = null;
                    Guid key = Sublayer;
                    uint result = PInvoke.FwpmSubLayerGetByKey0(engine, &key, &layer);
                    if (result == 0x80320007) return new(false, false, true, "No Conduit network lock is installed.");
                    Check(result);
                    try
                    {
                        bool persistent = (layer->flags & PInvoke.FWPM_SUBLAYER_FLAG_PERSISTENT) != 0;
                        bool blocks = persistent && ReadBlock(engine, 1, PInvoke.FWPM_LAYER_OUTBOUND_TRANSPORT_V4) &&
                            ReadBlock(engine, 4, PInvoke.FWPM_LAYER_OUTBOUND_TRANSPORT_V6);
                        return new(true, blocks, true, blocks ? "Conduit's persistent IPv4 and IPv6 blocking rules are present. Configured tunnel, server, loopback and DHCP exceptions still apply."
                            : "Conduit policy exists, but its required IPv4 / IPv6 blocking rules are incomplete. Protection is unverified.");
                    }
                    finally { if (layer != null) { void* memory = layer; PInvoke.FwpmFreeMemory0(&memory); } }
                }
                finally { PInvoke.FwpmTransactionAbort0(engine); }
            }
            finally { PInvoke.FwpmEngineClose0(engine); }
        }
        catch (Exception e) when (e is Win32Exception or DllNotFoundException or EntryPointNotFoundException) { return NetworkLockObservation.Unavailable; }
    }
    private static bool ReadBlock(FWPM_ENGINE_HANDLE engine, int id, Guid layer)
    {
        FWPM_FILTER0* filter = null;
        try
        {
            Guid key = Key(id);
            uint result = PInvoke.FwpmFilterGetByKey0(engine, &key, &filter);
            if (result == 0x80320003) return false;
            Check(result);
            return ValidBlock(*filter, key, layer);
        }
        finally { if (filter != null) { void* memory = filter; PInvoke.FwpmFreeMemory0(&memory); } }
    }
    internal static bool ValidBlock(FWPM_FILTER0 filter, Guid key, Guid layer) =>
        filter.filterKey == key && filter.subLayerKey == Sublayer && filter.layerKey == layer &&
        filter.action.type == FWP_ACTION_TYPE.FWP_ACTION_BLOCK && filter.numFilterConditions == 0 &&
        (filter.flags & FWPM_FILTER_FLAGS.FWPM_FILTER_FLAG_PERSISTENT) != 0 &&
        filter.weight.type == FWP_DATA_TYPE.FWP_UINT64 && filter.weight.uint64 != null && *filter.weight.uint64 == 10;
    public static void Apply(string server, int port, ulong tunLuid, bool allowServerUdp = false)
    {
        byte[] bytes = IPAddress.Parse(server).GetAddressBytes(); if (bytes.Length != 4 || port is < 1 or > 65535) throw new InvalidDataException("Invalid network-lock endpoint.");
        uint address = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        var handle = Open();
        try
        {
            Check(PInvoke.FwpmTransactionBegin0(handle, 0));
            try
            {
                RemoveFilters(handle);
                fixed (char* name = "Conduit network lock")
                {
                    var sublayer = new FWPM_SUBLAYER0 { subLayerKey = Sublayer, displayData = new() { name = new PWSTR(name) }, flags = PInvoke.FWPM_SUBLAYER_FLAG_PERSISTENT, weight = 0x7fff };
                    uint add = PInvoke.FwpmSubLayerAdd0(handle, &sublayer, default);
                    if (add != 0x80320009) Check(add); // FWP_E_ALREADY_EXISTS
                    int id = 1;
                    foreach (var layer in new[] { PInvoke.FWPM_LAYER_OUTBOUND_TRANSPORT_V4, PInvoke.FWPM_LAYER_OUTBOUND_TRANSPORT_V6 })
                    {
                        Add(handle, Key(id++), layer, 10, false, [], name);
                        Add(handle, Key(id++), layer, 100, true, [Condition(PInvoke.FWPM_CONDITION_FLAGS, FWP_DATA_TYPE.FWP_UINT32, PInvoke.FWP_CONDITION_FLAG_IS_LOOPBACK, FWP_MATCH_TYPE.FWP_MATCH_FLAGS_ALL_SET)], name);
                        if (tunLuid != 0)
                        {
                            var nic = new FWPM_FILTER_CONDITION0 { fieldKey = PInvoke.FWPM_CONDITION_IP_LOCAL_INTERFACE, matchType = FWP_MATCH_TYPE.FWP_MATCH_EQUAL, conditionValue = new() { type = FWP_DATA_TYPE.FWP_UINT64 } };
                            nic.conditionValue.uint64 = &tunLuid; Add(handle, Key(id), layer, 80, true, [nic], name);
                        }
                        id++;
                    }
                    Add(handle, Key(7), PInvoke.FWPM_LAYER_OUTBOUND_TRANSPORT_V4, 90, true,
                        [Condition(PInvoke.FWPM_CONDITION_IP_REMOTE_ADDRESS, FWP_DATA_TYPE.FWP_UINT32, address),
                         Condition(PInvoke.FWPM_CONDITION_IP_REMOTE_PORT, FWP_DATA_TYPE.FWP_UINT16, (uint)port),
                         Condition(PInvoke.FWPM_CONDITION_IP_PROTOCOL, FWP_DATA_TYPE.FWP_UINT8, 6)], name);
                    Add(handle, Key(8), PInvoke.FWPM_LAYER_OUTBOUND_TRANSPORT_V4, 90, true,
                        [Condition(PInvoke.FWPM_CONDITION_IP_REMOTE_ADDRESS, FWP_DATA_TYPE.FWP_UINT32, uint.MaxValue),
                         Condition(PInvoke.FWPM_CONDITION_IP_REMOTE_PORT, FWP_DATA_TYPE.FWP_UINT16, 67),
                         Condition(PInvoke.FWPM_CONDITION_IP_LOCAL_PORT, FWP_DATA_TYPE.FWP_UINT16, 68),
                         Condition(PInvoke.FWPM_CONDITION_IP_PROTOCOL, FWP_DATA_TYPE.FWP_UINT8, 17)], name);
                }
                    if (allowServerUdp)
                    {
                        fixed (char* udpName = "Conduit server UDP")
                            Add(handle, Key(9), PInvoke.FWPM_LAYER_OUTBOUND_TRANSPORT_V4, 90, true,
                                [Condition(PInvoke.FWPM_CONDITION_IP_REMOTE_ADDRESS, FWP_DATA_TYPE.FWP_UINT32, address),
                                 Condition(PInvoke.FWPM_CONDITION_IP_REMOTE_PORT, FWP_DATA_TYPE.FWP_UINT16, (uint)port),
                                 Condition(PInvoke.FWPM_CONDITION_IP_PROTOCOL, FWP_DATA_TYPE.FWP_UINT8, 17)], udpName);
                    }
                Check(PInvoke.FwpmTransactionCommit0(handle));
            }
            catch { PInvoke.FwpmTransactionAbort0(handle); throw; }
        }
        finally { PInvoke.FwpmEngineClose0(handle); }
    }
    private static FWPM_FILTER_CONDITION0 Condition(Guid key, FWP_DATA_TYPE type, uint value, FWP_MATCH_TYPE match = FWP_MATCH_TYPE.FWP_MATCH_EQUAL)
    {
        var condition = new FWPM_FILTER_CONDITION0 { fieldKey = key, matchType = match, conditionValue = new() { type = type } };
        if (type == FWP_DATA_TYPE.FWP_UINT8) condition.conditionValue.uint8 = (byte)value;
        else if (type == FWP_DATA_TYPE.FWP_UINT16) condition.conditionValue.uint16 = (ushort)value;
        else condition.conditionValue.uint32 = value;
        return condition;
    }
    private static void Add(FWPM_ENGINE_HANDLE engine, Guid key, Guid layer, ulong weight, bool permit, FWPM_FILTER_CONDITION0[] conditions, char* name)
    {
        fixed (FWPM_FILTER_CONDITION0* array = conditions)
        {
            var filter = new FWPM_FILTER0 { filterKey = key, displayData = new() { name = new PWSTR(name) }, flags = FWPM_FILTER_FLAGS.FWPM_FILTER_FLAG_PERSISTENT,
                layerKey = layer, subLayerKey = Sublayer, weight = new() { type = FWP_DATA_TYPE.FWP_UINT64 }, numFilterConditions = (uint)conditions.Length, filterCondition = array,
                action = new() { type = permit ? FWP_ACTION_TYPE.FWP_ACTION_PERMIT : FWP_ACTION_TYPE.FWP_ACTION_BLOCK } };
            filter.weight.uint64 = &weight; Check(PInvoke.FwpmFilterAdd0(engine, &filter, default, null));
        }
    }
    private static void RemoveFilters(FWPM_ENGINE_HANDLE engine)
    {
        for (int i = 1; i <= FilterCount; i++) { Guid key = Key(i); uint result = PInvoke.FwpmFilterDeleteByKey0(engine, &key); if (result != 0x80320003) Check(result); }
    }
    public static void Remove()
    {
        var engine = Open();
        try
        {
            Check(PInvoke.FwpmTransactionBegin0(engine, 0));
            try { RemoveFilters(engine); Guid key = Sublayer; uint result = PInvoke.FwpmSubLayerDeleteByKey0(engine, &key); if (result != 0x80320007) Check(result); Check(PInvoke.FwpmTransactionCommit0(engine)); }
            catch { PInvoke.FwpmTransactionAbort0(engine); throw; }
        }
        finally { PInvoke.FwpmEngineClose0(engine); }
    }
}
