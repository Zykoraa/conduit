#if OWNER_BUILD
namespace WorkTunnel;
internal static class UsageReader
{
    public static async Task<string> ReadAsync()
    {
        var result = await Ssh.RunAsync("bash ~/usage.sh", 15000);
        return result.output.Length > 0 ? result.output : "Usage data is unavailable.";
    }
}
#endif
