using System.Security.Cryptography;
using System.Text.Json;

namespace WorkTunnel;

internal sealed record SignedRelease(string Version, string Flavor, string PackageSha256, string KeyId);
internal static class ReleaseSignature
{
    public static SignedRelease Verify(byte[] manifest, byte[] signature, byte[] publicKey)
    {
        VerifySignature(manifest, signature, publicKey);
        UpdateFeedCodec.ExactObject(manifest, "Version", "Flavor", "PackageSha256", "KeyId");
        var release = JsonSerializer.Deserialize<SignedRelease>(manifest) ?? throw new InvalidDataException("Missing release metadata.");
        _ = UpdateFeedCodec.VersionNumber(release.Version);
        string keyId = Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant();
        if (release.KeyId != keyId || release.Flavor is not ("Owner" or "Client") || release.PackageSha256 == null || !System.Text.RegularExpressions.Regex.IsMatch(release.PackageSha256, "^[a-fA-F0-9]{64}$")) throw new InvalidDataException("Release metadata is invalid.");
        return release;
    }
    internal static void VerifySignature(byte[] manifest, byte[] signature, byte[] publicKey)
    {
        if (manifest.Length > 8192 || signature.Length != 64) throw new InvalidDataException("Invalid release signature format.");
        using var signer = ECDsa.Create(); signer.ImportSubjectPublicKeyInfo(publicKey, out int used);
        if (used != publicKey.Length || !signer.VerifyData(manifest, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new CryptographicException("Release signature is invalid. Nothing was installed.");
    }
    public static (string Package, SignedRelease Release) Unpack(string bundle, string destination, byte[] publicKey)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(bundle);
        if (zip.Entries.Count != 3 || zip.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
            throw new InvalidDataException("Invalid signed bundle.");
        byte[] Read(string name, long max)
        {
            var entry = zip.GetEntry(name) ?? throw new InvalidDataException("Incomplete signed bundle.");
            if (entry.Length > max) throw new InvalidDataException("Bundle entry is too large.");
            using var source = entry.Open(); using var memory = new MemoryStream(); source.CopyTo(memory); return memory.ToArray();
        }
        var release = Verify(Read("release.json", 8192), Read("release.sig", 64), publicKey);
        var package = zip.GetEntry("package.zip") ?? throw new InvalidDataException("Package missing.");
        if (package.Length > 300_000_000) throw new InvalidDataException("Package is too large.");
        Directory.CreateDirectory(destination);
        string path = Path.Combine(destination, "package.zip");
        using (var input = package.Open()) using (var output = new FileStream(path, FileMode.CreateNew)) input.CopyTo(output);
        using var file = File.OpenRead(path);
        if (!Convert.ToHexString(SHA256.HashData(file)).Equals(release.PackageSha256, StringComparison.OrdinalIgnoreCase)) throw new CryptographicException("Package does not match its signed manifest.");
        return (path, release);
    }
}
