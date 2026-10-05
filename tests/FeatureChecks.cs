using System.IO.Compression;
using System.Text.Json;
using WorkTunnel;

internal static class FeatureChecks
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var q = QualityResult.FromSamples([10, 20, null, 100, 110]);
        check(q.MedianMs == 60 && q.VariationMs == 10 && q.FailurePercent == 20 && q.P95Ms == 110, "Quality statistics keep timeout gaps out of RTT variation");
        var none = QualityResult.FromSamples([null, null]);
        check(none.MedianMs == null && none.VariationMs == null && none.FailurePercent == 100, "All failed probes do not report zero ping");
        string secret = "test-short-secret";
        string raw = "password=small token=abc api_key=xyz vless://identity@example.com secret='tiny' 11111111-2222-3333-4444-555555555555 " + secret;
        string scrubbed = DiagnosticRedactor.Scrub(raw, [secret]);
        check(!new[] { "small", "abc", "xyz", "tiny", secret, "vless://", "555555555555" }.Any(scrubbed.Contains), "Export removes short credentials, links, UUIDs and known secrets");
        string json = DiagnosticRedactor.ScrubJson(JsonSerializer.Serialize(new { message = raw, large = new string('x', 3000), n = 12 }), [secret]);
        using (var document = JsonDocument.Parse(json)) check(document.RootElement.GetProperty("n").GetInt32() == 12 && !json.Contains(secret), "Redacted JSON remains parseable with typed measurements");
        check(!DiagnosticRedactor.Scrub("-----BEGIN OPENSSH PRIVATE KEY-----\nabc\n-----END OPENSSH PRIVATE KEY-----").Contains("abc"), "Private key blocks are removed");
        var ok = new HealthCheck(true, "ok"); var fail = new HealthCheck(false, "failed");
        var h = new TunnelHealth(ok, fail, fail, fail, null, DateTimeOffset.Now, new(ok, ok, ok));
        check(ProblemExplanation.Explain(TunnelState.Connected, h).Contains("unknown"), "Combined failures do not invent a root cause");
        check(ProblemExplanation.Explain(TunnelState.Connected, h with { Internet = ok, Dns = ok }).Contains("UDP is unverified"), "UDP failure is distinguished from general internet failure");

        string root = Path.Combine(Path.GetTempPath(), "WorkTunnel-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string historyPath = Path.Combine(root, "history.jsonl");
            var history = new HistoryStore(historyPath); history.Add("Recovery", raw);
            File.AppendAllText(historyPath, "broken JSON\n");
            var loaded = new HistoryStore(historyPath);
            check(loaded.Entries.Length == 1 && !loaded.Entries[0].Message.Contains("vless://"), "History survives restart and skips a damaged line");
            var memory = new HistoryStore(null); for (int i = 0; i < 2100; i++) memory.Add("test", i.ToString());
            check(memory.Entries.Length == 2000 && memory.Entries[0].Message == "100", "History retains only the recent 2000 events");
            foreach (string name in new[] { "../outside", "/absolute", "C:/target", "a/../b", "a:stream", "a./b", "a /b" })
                reject(() => UpdateInstaller.SafeRelative(name), "Reject update path " + name);
            string source = Path.Combine(root, "source"), target = Path.Combine(root, "target"), storage = Path.Combine(root, "updates");
            Directory.CreateDirectory(source); Directory.CreateDirectory(Path.Combine(target, "configs"));
            File.WriteAllText(Path.Combine(target, "app.txt"), "original");
            File.WriteAllText(Path.Combine(target, "configs/profile.json"), "my identity");
            File.WriteAllText(Path.Combine(source, "app.txt"), "updated"); File.WriteAllText(Path.Combine(source, "new.txt"), "new file");
            var files = new[] { "app.txt", "new.txt" };
            var plan = new UpdatePlan(source, target, "99.0.0", files, files.ToDictionary(f => f, f => UpdateInstaller.Hash(Path.Combine(source, f))), 0, 0, false);
            UpdateInstaller.ApplyFiles(plan, storage);
            check(File.ReadAllText(Path.Combine(target, "app.txt")) == "updated" && File.ReadAllText(Path.Combine(target, "configs/profile.json")) == "my identity", "Update replaces selected files and preserves the profile");
            UpdateInstaller.ApplyFiles(UpdateInstaller.PrepareRollback(target, false, storage), storage);
            check(File.ReadAllText(Path.Combine(target, "app.txt")) == "original" && !File.Exists(Path.Combine(target, "new.txt")), "Rollback restores old files and removes newly introduced files");
            File.WriteAllText(Path.Combine(source, "app.txt"), "tampered");
            reject(() => UpdateInstaller.ApplyFiles(plan, storage), "Changed staging file fails hash verification before replacement");
            check(File.ReadAllText(Path.Combine(target, "app.txt")) == "original", "Failed staging verification leaves installation intact");
            reject(() => UpdateInstaller.ApplyFiles(plan with { Files = ["configs/profile.json"] }, storage), "Updater refuses profile replacement even in a supplied job");
            File.WriteAllText(Path.Combine(source, "app.txt"), "updated");
            Directory.CreateDirectory(Path.Combine(target, "new.txt"));
            reject(() => UpdateInstaller.ApplyFiles(plan, storage), "Copy failure aborts the update");
            check(File.ReadAllText(Path.Combine(target, "app.txt")) == "original", "Copy failure restores files already replaced");

            string zip = Path.Combine(root, "package.zip");
            void Package(string flavor, bool traversal = false)
            {
                if (File.Exists(zip)) File.Delete(zip);
                using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
                foreach (string file in new[] { "WorkTunnel.exe", "cores/xray/xray.exe", "cores/sing_box/sing-box.exe", "configs/xray.template.json", "configs/singbox-tun.json", "configs/profile.json" })
                { using var w = new StreamWriter(z.CreateEntry(file).Open()); w.Write("test fixture"); }
                using (var w = new StreamWriter(z.CreateEntry("release-manifest.json").Open())) w.Write(JsonSerializer.Serialize(new ReleaseManifest("99.0.0", flavor)));
                if (traversal) { using var w = new StreamWriter(z.CreateEntry("../escaped").Open()); w.Write("bad"); }
            }
            Package(UpdateInstaller.Flavor);
            reject(() => UpdateInstaller.Prepare(zip, new string('0', 64), target, false, storage), "Wrong whole-package hash is rejected");
            var prepared = UpdateInstaller.Prepare(zip, UpdateInstaller.Hash(zip), target, false, storage);
            check(!prepared.Files.Contains("configs/profile.json"), "Valid package excludes bundled identity from installation");
            Package("WrongFlavor"); reject(() => UpdateInstaller.Prepare(zip, UpdateInstaller.Hash(zip), target, false, storage), "Owner/client package mismatch is rejected");
            Package(UpdateInstaller.Flavor, true); reject(() => UpdateInstaller.Prepare(zip, UpdateInstaller.Hash(zip), target, false, storage), "ZIP traversal is rejected during extraction");
            check(!File.Exists(Path.Combine(storage, "escaped")), "Extraction never writes the traversal entry");
        }
        finally { Directory.Delete(root, true); }
    }
}
