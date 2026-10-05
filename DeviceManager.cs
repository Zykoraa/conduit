#if OWNER_BUILD
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WorkTunnel;

internal sealed record TunnelDevice(string Id, string Name, bool Managed)
{
    public string DisplayName => Id == Profile.Load().Uuid ? Name + " (this PC)" : Name + (Managed ? "" : $" (protected · {Id[^6..]})");
}
internal sealed record DeviceList(TunnelDevice[] Devices, string Sni, string ShortId, int Port);

internal sealed class DeviceManager(ITunnelController controller)
{
    private const string Command = "sudo -n /usr/local/bin/worktunnel-devices";
    private static string PendingPath => Path.Combine(AppPaths.DataDir, "pending-device-operation.json");
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    public bool Pending => File.Exists(PendingPath);

    private static async Task<JsonObject> RequestAsync(object request)
    {
        var result = await Ssh.RunAsync(Command, 12000, JsonSerializer.Serialize(request));
        JsonObject? json = null;
        try { json = JsonNode.Parse(result.output)?.AsObject(); } catch (JsonException) { }
        if (json?["error"] != null) throw new InvalidOperationException(json["error"]!.GetValue<string>());
        if (result.code != 0 || json == null) throw new IOException("Cannot reach device management on the VM. Check your connection and owner SSH key.");
        return json;
    }

    public async Task<DeviceList> ListAsync() => (await RequestAsync(new { action = "list" })).Deserialize<DeviceList>(Options)!;

    public async Task ChangeAsync(string operation, string id, string name, Action<string> progress)
    {
        if (Pending) throw new InvalidOperationException("Refresh the pending device operation before starting another.");
        var profile = Profile.Load();
        if (profile.Imported != null) throw new InvalidOperationException("Owner device management is unavailable for an imported v2rayN server.");
        var request = new { action = "submit", operation, id, name, requester = profile.Uuid, job = Guid.NewGuid().ToString() };
        var pending = new { server = profile.Server, request };
        File.WriteAllText(PendingPath, JsonSerializer.Serialize(pending), AppPaths.Utf8NoBom);
        try { await RequestAsync(request); }
        catch (IOException) { /* SSH may drop as Xray restarts. Poll the durable job. */ }
        catch { File.Delete(PendingPath); throw; }
        progress("Applying on the VM. Devices may briefly reconnect…");
        await Task.Delay(2500);
        if (controller.ConnectionRequested) await controller.ReconnectAsync();
        await ResumeAsync(progress);
    }

    public async Task ResumeAsync(Action<string> progress)
    {
        if (!Pending) return;
        var pending = JsonNode.Parse(File.ReadAllText(PendingPath))!;
        if (pending["server"]!.GetValue<string>() != Profile.Load().Server)
            throw new InvalidOperationException("A device change is pending on another VM. Switch back to that profile first.");
        var job = pending["request"]!["job"]!.GetValue<string>();
        var end = DateTime.UtcNow.AddSeconds(100);
        while (DateTime.UtcNow < end)
        {
            try
            {
                var result = await RequestAsync(new { action = "status", job });
                var state = result["state"]!.GetValue<string>();
                if (state is "done" or "failed")
                {
                    File.Delete(PendingPath);
                    if (state == "failed") throw new InvalidOperationException(result["message"]!.GetValue<string>());
                    progress(result["message"]!.GetValue<string>());
                    return;
                }
            }
            catch (IOException) { progress("Waiting for the VM to reconnect. Your operation is saved…"); }
            catch (InvalidOperationException error) when (error.Message.Contains("No such file"))
            {
                // A lost SSH request may never have reached the server. Same job+UUID is idempotent.
                try { await RequestAsync(pending["request"]!); } catch (IOException) { }
            }
            await Task.Delay(2000);
        }
        throw new IOException("The operation is still pending. Use Refresh later; do not add the device again.");
    }

    public static Profile ConnectionFor(TunnelDevice device, DeviceList list)
    {
        var profile = Profile.Load();
        if (profile.PublicKey.Length == 0)
        {
            var template = JsonNode.Parse(File.ReadAllText(AppPaths.XrayTemplate))!;
            profile.PublicKey = template["outbounds"]![0]!["streamSettings"]!["realitySettings"]!["publicKey"]!.GetValue<string>();
        }
        profile.Uuid = device.Id; profile.User = device.Name;
        profile.Sni = list.Sni; profile.ShortId = list.ShortId; profile.Port = list.Port;
        return profile;
    }
}
#endif
