using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkTunnel;

internal sealed record UpdateFeedManifest(int Schema, string Channel, string Flavor, string Version,
    string Asset, string BundleSha256, long BundleBytes, DateTimeOffset PublishedAt, DateTimeOffset ExpiresAt, string KeyId);
internal sealed record UpdateFeedEnvelope(string Manifest, string Signature);
internal sealed record VerifiedUpdate(UpdateFeedManifest Release, Uri Feed)
{
    public Uri Download => new(Feed, Release.Asset);
}

internal static class UpdateFeedCodec
{
    internal const int MaximumDocumentBytes = 16384;
    internal const long MaximumBundleBytes = 300_000_000;
    internal static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
    internal static Version VersionNumber(string text)
    {
        if (text == null || !Regex.IsMatch(text, @"^\d{1,5}\.\d{1,5}\.\d{1,5}$") || !Version.TryParse(text, out var version) ||
            version.Major > 65535 || version.Minor > 65535 || version.Build > 65535)
            throw new InvalidDataException("Invalid stable release version.");
        return Normalize(version);
    }
    internal static Uri FeedAddress(string text)
    {
        if (text.Length is < 1 or > 2048 || !Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || uri.IsLoopback || !uri.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Use a direct HTTPS .json feed address without credentials, query parameters or redirects.");
        return uri;
    }
    internal static void ExactObject(byte[] bytes, params string[] properties)
    {
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid update document.");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
            if (!properties.Contains(property.Name, StringComparer.Ordinal) || !found.Add(property.Name))
                throw new InvalidDataException("Duplicate or unexpected update metadata.");
        if (found.Count != properties.Length) throw new InvalidDataException("Incomplete update metadata.");
    }
    internal static UpdateFeedManifest Verify(byte[] document, byte[] publicKey, string flavor, DateTimeOffset now, string? highestSeen = null)
    {
        try
        {
            if (document.Length is < 1 or > MaximumDocumentBytes) throw new InvalidDataException("Update feed is too large.");
            ExactObject(document, "Manifest", "Signature");
            var envelope = JsonSerializer.Deserialize<UpdateFeedEnvelope>(document) ?? throw new InvalidDataException("Missing update feed.");
            byte[] manifest = Convert.FromBase64String(envelope.Manifest), signature = Convert.FromBase64String(envelope.Signature);
            ReleaseSignature.VerifySignature(manifest, signature, publicKey);
            ExactObject(manifest, "Schema", "Channel", "Flavor", "Version", "Asset", "BundleSha256", "BundleBytes", "PublishedAt", "ExpiresAt", "KeyId");
            var release = JsonSerializer.Deserialize<UpdateFeedManifest>(manifest) ?? throw new InvalidDataException("Missing update metadata.");
            Validate(release, publicKey, flavor, now);
            if (highestSeen != null && VersionNumber(release.Version) < VersionNumber(highestSeen))
                throw new InvalidDataException("This feed is older than a release already verified on this PC.");
            return release;
        }
        catch (Exception e) when (e is JsonException or FormatException or ArgumentException)
        { throw new InvalidDataException("Malformed signed update feed.", e); }
    }
    internal static void Validate(UpdateFeedManifest release, byte[] publicKey, string flavor, DateTimeOffset now)
    {
        _ = VersionNumber(release.Version);
        if (release.Schema != 1 || release.Channel != "stable" || release.Flavor != flavor || flavor is not ("Owner" or "Client") ||
            release.KeyId != Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant() ||
            release.Asset != $"Conduit-{flavor}-{release.Version}.wtupdate" || release.BundleSha256 == null ||
            !Regex.IsMatch(release.BundleSha256, "^[a-fA-F0-9]{64}$") || release.BundleBytes is < 1 or > MaximumBundleBytes ||
            release.PublishedAt > now.AddMinutes(5) || release.ExpiresAt <= now ||
            release.ExpiresAt <= release.PublishedAt || release.ExpiresAt - release.PublishedAt > TimeSpan.FromDays(90))
            throw new InvalidDataException("Update metadata has the wrong flavor, invalid fields or expired validity.");
    }
    internal static byte[] Sign(UpdateFeedManifest release, ECDsa key)
    {
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(release);
        byte[] signature = key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return JsonSerializer.SerializeToUtf8Bytes(new UpdateFeedEnvelope(Convert.ToBase64String(manifest), Convert.ToBase64String(signature)));
    }
}
