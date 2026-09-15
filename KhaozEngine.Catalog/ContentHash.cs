using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace KhaozEngine.Catalog;

/// <summary>
/// Every digest in the pack, domain separated under <c>kec/</c> with the scheme version folded into the
/// prefix. SHA-256 throughout, rendered LOWER HEX as 64 characters wherever it appears as text and carried
/// as raw 32 bytes wherever it appears as a field. No two digests here share a sub-domain, so a head
/// comparing one can never accidentally agree with a head comparing another.
/// <para>
/// The chunk digests take a UTF-8 domain prefix followed by the canonical bytes RAW, because a chunk's
/// input is already canonical bytes and re-rendering them as text would double the digest cost for
/// nothing. The manifest digests take a canonical TEXT, in which every number is invariant formatted and
/// every string is length prefixed, because a manifest's input is authored values.
/// </para>
/// </summary>
public static class ContentHash
{
    /// <summary>
    /// Folded into every digest here. Bumping it re-digests every published version, which is precisely
    /// why it exists.
    /// </summary>
    public const int SchemeVersion = ContentPackFormat.HashSchemeVersion;

    /// <summary>The chunk sub-domain, over a chunk's canonical uncompressed bytes.</summary>
    public const string ChunkDomain = "kec/chunk/";

    /// <summary>The remap rule chunk sub-domain, over a rule chunk's canonical uncompressed bytes.</summary>
    public const string RuleDomain = "kec/rules/";

    /// <summary>The text chunk sub-domain, over a text chunk's canonical uncompressed bytes.</summary>
    public const string TextDomain = "kec/text/";

    /// <summary>The server manifest sub-domain, over the canonical manifest text with the server side's chunks.</summary>
    public const string ServerManifestDomain = "kec/manifest/server/";

    /// <summary>The client manifest sub-domain, over the same text with the server-only chunks omitted.</summary>
    public const string ClientManifestDomain = "kec/manifest/client/";

    /// <summary>The content address of one chunk, over its canonical uncompressed bytes.</summary>
    public static string OfChunk(ReadOnlySpan<byte> canonical) => OverBytes(ChunkDomain, canonical);

    /// <summary>The content address of the remap rule chunk, over its canonical uncompressed bytes.</summary>
    public static string OfRuleChunk(ReadOnlySpan<byte> canonical) => OverBytes(RuleDomain, canonical);

    /// <summary>The content address of one per-language text chunk, over its canonical uncompressed bytes.</summary>
    public static string OfTextChunk(ReadOnlySpan<byte> canonical) => OverBytes(TextDomain, canonical);

    /// <summary>The server manifest hash, the identity half of a content version, over the canonical manifest text.</summary>
    public static string OfServerManifest(string canonicalText) => OverText(ServerManifestDomain, canonicalText);

    /// <summary>
    /// The client manifest hash, over the same canonical text built from the client-visible chunks only, so
    /// it is never equal to the server manifest's hash for the same version.
    /// </summary>
    public static string OfClientManifest(string canonicalText) => OverText(ClientManifestDomain, canonicalText);

    /// <summary>
    /// The digest of a standalone file under the sub-domain its own magic names, so a caller verifying a
    /// downloaded file hashes it the way the publisher did rather than guessing. Returns null for a run of
    /// bytes too short to carry a magic and for a magic this reader does not know, both refused before any
    /// length is read.
    /// <para>
    /// A <c>KECM</c> manifest is deliberately not one of them: a manifest hash is taken over the canonical
    /// manifest TEXT after decode, never over the file bytes, so there is no answer to give here.
    /// </para>
    /// </summary>
    public static string? OfBytesForKind(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < ContentPackFormat.MagicBytes) return null;

        ReadOnlySpan<byte> magic = bytes[..ContentPackFormat.MagicBytes];
        if (magic.SequenceEqual(ContentPackFormat.ChunkMagic)) return OfChunk(bytes);
        if (magic.SequenceEqual(ContentPackFormat.RuleChunkMagic)) return OfRuleChunk(bytes);
        if (magic.SequenceEqual(ContentPackFormat.TextChunkMagic)) return OfTextChunk(bytes);
        return null;
    }

    /// <summary>The UTF-8 domain prefix a digest opens with, the sub-domain then the scheme version then a newline.</summary>
    public static string Domain(string subDomain) =>
        subDomain + SchemeVersion.ToString(CultureInfo.InvariantCulture) + "\n";

    /// <summary>
    /// Appends a string to a canonical text, LENGTH PREFIXED as <c>"{len}:{value} "</c> with a bare
    /// <c>"- "</c> for null, so a delimiter inside an authored key cannot make two different manifests
    /// digest the same, and a value that is missing is not the same content as one that is empty.
    /// </summary>
    public static void AppendText(StringBuilder builder, string? value)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (value is null) { builder.Append("- "); return; }
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
        builder.Append(' ');
    }

    /// <summary>
    /// Appends a number to a canonical text through <see cref="CultureInfo.InvariantCulture"/>, because
    /// <c>StringBuilder.Append(int)</c> formats with the CURRENT culture and a negative number would then
    /// digest differently under a culture with its own minus sign.
    /// </summary>
    public static void AppendNumber(StringBuilder builder, long value)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Append(value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The verification path: the same digest as <see cref="OfChunk"/> and its siblings taken over a header
    /// and a body in place, with no intermediate copy of the canonical bytes.
    /// </summary>
    public static string OfChunkStreaming(ReadOnlySpan<byte> header, ReadOnlySpan<byte> body, string subDomain)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.UTF8.GetBytes(Domain(subDomain)));
        sha.AppendData(header);
        sha.AppendData(body);
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    static string OverBytes(string subDomain, ReadOnlySpan<byte> canonical)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.UTF8.GetBytes(Domain(subDomain)));
        sha.AppendData(canonical);
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    static string OverText(string subDomain, string canonicalText)
    {
        ArgumentNullException.ThrowIfNull(canonicalText);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Domain(subDomain) + canonicalText)));
    }
}
