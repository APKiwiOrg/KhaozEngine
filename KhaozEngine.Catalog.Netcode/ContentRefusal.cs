using System;
using System.Globalization;
using KhaozEngine.Catalog;

namespace KhaozEngine.Catalog.Netcode;

/// <summary>
/// The two stable wire tokens the content door refuses with, contracts 7.5, and their parsers:
/// <code>
/// ke:content-mismatch:&lt;serverVersion&gt;|&lt;serverHash&gt;|&lt;clientVersion&gt;|&lt;clientHash&gt;
/// ke:content-client-too-old:&lt;minimumClientBuild&gt;
/// </code>
/// <para>
/// The PIPE separates the fields inside the payload and the COLON separates the token's own fields, matching
/// the world gate's <c>ke:world-mismatch:&lt;serverHash&gt;|&lt;clientHash&gt;</c>. A token is not display
/// text: a client matches it and shows its own localized string, which is why nothing here is ever reworded
/// and why the mismatch carries BOTH sides. The client fetches by the SERVER hash it was just handed, so the
/// token is also the whole input to the fetch loop of spec 8.7.
/// </para>
/// <para>
/// <b>No value in either token ever contains a colon.</b> A version number is decimal and a hash is a content
/// address, both checked here, so a client that presents a hostile layer gets EMPTY client fields rather
/// than a payload that re-splits under its own parser.
/// </para>
/// <para>
/// <b>The PARSE side is as strict as the write side, on both hashes.</b> A refusal is the whole input to the
/// fetch loop, so a server hash that is not a content address is a path fragment reaching a client's fetch,
/// and an empty one is a fetch with nothing to name. Neither is a refusal a client can act on, so
/// <see cref="TryParseMismatch"/> answers false for both, which is the same answer it gives a token it does
/// not recognise at all.
/// </para>
/// </summary>
public static class ContentRefusal
{
    /// <summary>The refusal prefix for a content identity mismatch, carrying both sides.</summary>
    public const string MismatchPrefix = "ke:content-mismatch:";

    /// <summary>The refusal prefix for a client below the version's minimum client build.</summary>
    public const string ClientTooOldPrefix = "ke:content-client-too-old:";

    /// <summary>
    /// The mismatch token. A null <paramref name="client"/> writes EMPTY client fields, which is the answer
    /// for a client that presented no layer at all or one that did not parse.
    /// </summary>
    /// <param name="server">The version the server is serving.</param>
    /// <param name="client">The version the client claims, or null when it claimed none.</param>
    /// <exception cref="ArgumentException">Either hash is not a content address, or carries a colon or a pipe.</exception>
    public static string Mismatch(ContentVersionIdentity server, ContentVersionIdentity? client)
    {
        RequireNoSeparator(server.ManifestHash, nameof(server));
        RequireContentAddress(server.ManifestHash, nameof(server));
        string clientVersion = string.Empty;
        string clientHash = string.Empty;
        if (client is not null)
        {
            RequireNoSeparator(client.Value.ManifestHash, nameof(client));
            RequireContentAddress(client.Value.ManifestHash, nameof(client));
            clientVersion = client.Value.Number.ToString(CultureInfo.InvariantCulture);
            clientHash = client.Value.ManifestHash ?? string.Empty;
        }

        return MismatchPrefix
            + server.Number.ToString(CultureInfo.InvariantCulture)
            + ContentIdentityLayer.FieldSeparator
            + (server.ManifestHash ?? string.Empty)
            + ContentIdentityLayer.FieldSeparator
            + clientVersion
            + ContentIdentityLayer.FieldSeparator
            + clientHash;
    }

    /// <summary>
    /// The too-old token, carrying the build the publisher named, so the client tells the player to update
    /// rather than showing a generic mismatch it cannot act on.
    /// </summary>
    /// <param name="minimumClientBuild">The version's <c>MinimumClientBuild</c>, contracts 7.4.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minimumClientBuild"/> is negative.</exception>
    public static string ClientTooOld(int minimumClientBuild)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumClientBuild);
        return ClientTooOldPrefix + minimumClientBuild.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Recognizes <see cref="Mismatch"/>, which is how a client learns the version and hash to fetch. The
    /// client side is null when the refusal carried empty client fields.
    /// </summary>
    public static bool TryParseMismatch(
        string? reason,
        out ContentVersionIdentity server,
        out ContentVersionIdentity? client)
    {
        server = default;
        client = null;
        if (reason is null || !reason.StartsWith(MismatchPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string[] fields = reason[MismatchPrefix.Length..].Split(ContentIdentityLayer.FieldSeparator);
        if (fields.Length != 4
            || !TryParseOrdinal(fields[0], out int serverVersion)
            || !ContentIdentityLayer.IsContentAddress(fields[1]))
        {
            // The SERVER hash is what the client fetches by, so a field that is not a content address is a
            // path fragment reaching a fetch, and an EMPTY one is a fetch with nothing to name. Both read as
            // no refusal at all, which is the same answer the layer parser gives a hostile layer value.
            return false;
        }

        server = new ContentVersionIdentity(serverVersion, fields[1]);
        if (fields[2].Length == 0 && fields[3].Length == 0)
        {
            return true;
        }

        if (!TryParseOrdinal(fields[2], out int clientVersion)
            || !ContentIdentityLayer.IsContentAddress(fields[3]))
        {
            server = default;
            return false;
        }

        client = new ContentVersionIdentity(clientVersion, fields[3]);
        return true;
    }

    /// <summary>Recognizes <see cref="ClientTooOld"/>, extracting the build the player has to reach.</summary>
    public static bool TryParseClientTooOld(string? reason, out int minimumClientBuild)
    {
        minimumClientBuild = 0;
        return reason is not null
            && reason.StartsWith(ClientTooOldPrefix, StringComparison.Ordinal)
            && TryParseOrdinal(reason[ClientTooOldPrefix.Length..], out minimumClientBuild);
    }

    static bool TryParseOrdinal(string value, out int number) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);

    static void RequireContentAddress(string? hash, string parameterName)
    {
        if (!ContentIdentityLayer.IsContentAddress(hash))
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"'{hash}' is not a manifest hash, which is 64 lower hex characters. A refusal a client cannot fetch by is not a refusal."),
                parameterName);
        }
    }

    static void RequireNoSeparator(string? hash, string parameterName)
    {
        if (hash is not null
            && (hash.IndexOf(':') >= 0 || hash.IndexOf(ContentIdentityLayer.FieldSeparator) >= 0))
        {
            throw new ArgumentException(
                "A manifest hash carrying a colon or a pipe would re-split the refusal token under a client's own parser.",
                parameterName);
        }
    }
}
