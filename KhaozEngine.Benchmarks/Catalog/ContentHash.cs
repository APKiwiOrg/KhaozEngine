using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>
/// Every digest in the pack, domain separated under <c>kec/</c> with the scheme version folded into the
/// prefix (contracts 7.3 and 15, spec section 7.8). The chunk digests take a UTF-8 domain prefix followed
/// by the canonical bytes raw. The manifest digests take a canonical TEXT, length prefixed and invariant
/// formatted, the way <c>TileWorldHash</c> does.
/// </summary>
public static class ContentHash
{
    public const int SchemeVersion = ContentPackFormat.HashSchemeVersion;

    public const string ChunkDomain = "kec/chunk/";
    public const string RuleDomain = "kec/rules/";
    public const string TextDomain = "kec/text/";
    public const string ServerManifestDomain = "kec/manifest/server/";
    public const string ClientManifestDomain = "kec/manifest/client/";

    public static string OfChunk(ReadOnlySpan<byte> canonical) => OverBytes(ChunkDomain, canonical);

    public static string OfRuleChunk(ReadOnlySpan<byte> canonical) => OverBytes(RuleDomain, canonical);

    public static string OfTextChunk(ReadOnlySpan<byte> canonical) => OverBytes(TextDomain, canonical);

    public static string OfManifestText(string canonicalText) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalText)));

    public static string Domain(string subDomain) =>
        subDomain + SchemeVersion.ToString(CultureInfo.InvariantCulture) + "\n";

    /// <summary>Length prefixed as <c>"{len}:{value} "</c>, with a bare <c>"- "</c> for null.</summary>
    public static void AppendText(StringBuilder builder, string? value)
    {
        if (value is null) { builder.Append("- "); return; }
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
        builder.Append(' ');
    }

    public static void AppendNumber(StringBuilder builder, long value) =>
        builder.Append(value.ToString(CultureInfo.InvariantCulture));

    private static string OverBytes(string subDomain, ReadOnlySpan<byte> canonical)
    {
        byte[] prefix = Encoding.UTF8.GetBytes(Domain(subDomain));
        using var sha = SHA256.Create();
        byte[] buffer = new byte[prefix.Length + canonical.Length];
        prefix.CopyTo(buffer.AsSpan());
        canonical.CopyTo(buffer.AsSpan(prefix.Length));
        return Convert.ToHexStringLower(sha.ComputeHash(buffer));
    }

    /// <summary>Verification path: the same digest without the intermediate copy of the canonical bytes.</summary>
    public static string OfChunkStreaming(ReadOnlySpan<byte> header, ReadOnlySpan<byte> body, string subDomain)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.UTF8.GetBytes(Domain(subDomain)));
        sha.AppendData(header);
        sha.AppendData(body);
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}
