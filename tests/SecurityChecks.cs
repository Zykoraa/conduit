using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WorkTunnel;

internal static class SecurityChecks
{
    public static async Task Run(Action<bool, string> check, Action<Action, string> reject)
    {
        byte[] plain = Encoding.UTF8.GetBytes("device-secret-" + Guid.NewGuid());
        byte[] protectedBytes = SecretStore.Protect(plain);
        check(SecretStore.Unprotect(protectedBytes).SequenceEqual(plain), "DPAPI restores the current user's profile");
        check(!Encoding.UTF8.GetString(protectedBytes).Contains(Encoding.UTF8.GetString(plain)), "Saved profile ciphertext does not contain the connection secret");
        protectedBytes[^1] ^= 1;
        reject(() => SecretStore.Unprotect(protectedBytes), "Tampered protected profile fails closed");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] pub = key.ExportSubjectPublicKeyInfo();
        string id = Convert.ToHexString(SHA256.HashData(pub)).ToLowerInvariant();
        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(new SignedRelease("99.0.0", "Client", new string('a', 64), id));
        byte[] signature = key.SignData(metadata, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        check(ReleaseSignature.Verify(metadata, signature, pub).Version == "99.0.0", "Trusted release signature verifies");
        reject(() => ReleaseSignature.Verify(metadata, signature, other.ExportSubjectPublicKeyInfo()), "An unrelated signing key cannot authorize an update");
        var tampered = (byte[])metadata.Clone(); tampered[4] ^= 1;
        reject(() => ReleaseSignature.Verify(tampered, signature, pub), "Changing signed metadata invalidates the signature");
        reject(() => ReleaseSignature.Verify(metadata, signature[..63], pub), "Truncated release signature is rejected");
        string root = Path.Combine(Path.GetTempPath(), "WorkTunnel-signature-check-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string bundle = Path.Combine(root, "bad.wtupdate");
            using (var archive = ZipFile.Open(bundle, ZipArchiveMode.Create))
            {
                using (var s = archive.CreateEntry("release.json").Open()) s.Write(metadata);
                using (var s = archive.CreateEntry("release.sig").Open()) s.Write(signature);
                using (var s = archive.CreateEntry("package.zip").Open()) s.Write(plain);
            }
            reject(() => ReleaseSignature.Unpack(bundle, Path.Combine(root, "out"), pub), "Valid signature cannot authorize substituted package bytes");
        }
        finally { Directory.Delete(root, true); }
        using var wire = new MemoryStream();
        await BrokerWire.WriteAsync(wire, new BrokerRequest("status"), default); wire.Position = 0;
        check((await BrokerWire.ReadAsync<BrokerRequest>(wire, default)).Command == "status", "Bounded IPC framing round trips");
        foreach (int size in new[] { -1, 0, 16385, int.MaxValue })
        {
            byte[] prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, size);
            using var stream = new MemoryStream(prefix);
            reject(() => BrokerWire.ReadAsync<BrokerRequest>(stream, default).GetAwaiter().GetResult(), "IPC rejects invalid allocation length " + size);
        }
        using var truncated = new MemoryStream(new byte[] { 10, 0, 0, 0, 123 });
        reject(() => BrokerWire.ReadAsync<BrokerRequest>(truncated, default).GetAwaiter().GetResult(), "IPC rejects interrupted requests");
    }
}
