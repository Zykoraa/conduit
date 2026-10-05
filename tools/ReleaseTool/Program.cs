using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;
using WorkTunnel;

string keyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkTunnelSigning", "release-key.dpapi");
using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
if (File.Exists(keyPath))
{
    byte[] secret = SecretStore.Unprotect(File.ReadAllBytes(keyPath));
    try { key.ImportPkcs8PrivateKey(secret, out _); } finally { CryptographicOperations.ZeroMemory(secret); }
}
else if (args.Length == 2 && args[0] == "init")
{
    byte[] secret = key.ExportPkcs8PrivateKey();
    try { SecretStore.Save(keyPath, secret); } finally { CryptographicOperations.ZeroMemory(secret); }
}
else throw new InvalidOperationException("Signing key unavailable. Restore the original protected key; do not silently create a new trust identity.");
byte[] publicKey = key.ExportSubjectPublicKeyInfo();
if (args.Length == 2 && args[0] == "init")
{
    File.WriteAllText(args[1], "namespace WorkTunnel;\ninternal static class ReleaseTrust { public static byte[] PublicKey => System.Convert.FromBase64String(\"" + Convert.ToBase64String(publicKey) + "\"); }\n");
    Console.WriteLine("Release trust initialized; private key remains Windows-protected outside the repository.");
}
else if (args.Length == 5 && args[0] == "sign")
{
    if (!publicKey.SequenceEqual(ReleaseTrust.PublicKey)) throw new CryptographicException("The signing key does not match the public key embedded in this source tree.");
    string package = args[1], destination = args[4];
    using var input = File.OpenRead(package);
    var metadata = new SignedRelease(args[2], args[3], Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant(), Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant());
    byte[] json = JsonSerializer.SerializeToUtf8Bytes(metadata);
    byte[] signature = key.SignData(json, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    string temporary = destination + ".new";
    if (File.Exists(temporary)) File.Delete(temporary);
    using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
    {
        archive.CreateEntryFromFile(package, "package.zip", CompressionLevel.NoCompression);
        using (var stream = archive.CreateEntry("release.json").Open()) stream.Write(json);
        using (var stream = archive.CreateEntry("release.sig").Open()) stream.Write(signature);
    }
    File.Move(temporary, destination, true); Console.WriteLine("Signed " + Path.GetFileName(destination));
}
else if (args.Length == 3 && args[0] == "feed")
{
    if (!publicKey.SequenceEqual(ReleaseTrust.PublicKey)) throw new CryptographicException("Signing key does not match release trust.");
    string bundle = args[1], asset = Path.GetFileName(args[1]);
    using var archive = ZipFile.OpenRead(bundle);
    byte[] Read(string name, long maximum)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidDataException("Missing signed bundle entry.");
        if (entry.Length > maximum) throw new InvalidDataException("Bundle entry is too large.");
        using var source = entry.Open(); using var memory = new MemoryStream(); source.CopyTo(memory); return memory.ToArray();
    }
    var release = ReleaseSignature.Verify(Read("release.json", 8192), Read("release.sig", 64), publicKey);
    var now = DateTimeOffset.UtcNow;
    using var input = File.OpenRead(bundle);
    var feed = new UpdateFeedManifest(1, "stable", release.Flavor, release.Version, asset,
        Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant(), input.Length, now, now.AddDays(30), release.KeyId);
    UpdateFeedCodec.Validate(feed, publicKey, release.Flavor, now);
    byte[] document = UpdateFeedCodec.Sign(feed, key);
    _ = UpdateFeedCodec.Verify(document, publicKey, release.Flavor, now);
    File.WriteAllBytes(args[2], document);
    Console.WriteLine("Signed update feed: " + Path.GetFileName(args[2]));
}
else throw new ArgumentException("Usage: init <ReleaseTrust.cs> | sign <package.zip> <version> <flavor> <output.wtupdate> | feed <versioned.wtupdate> <output.json>");
