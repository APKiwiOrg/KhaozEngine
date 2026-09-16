using System;
using System.Globalization;
using KhaozEngine.Catalog;
using KhaozEngine.Netcode;

namespace KhaozEngine.Catalog.Netcode;

/// <summary>
/// The content layer of the connect-token nest, contracts 7.5: the value is
/// <c>&lt;versionNumber&gt;|&lt;clientManifestHash&gt;</c>, the decimal version number and the 64 character
/// lower hex client manifest hash joined by a pipe.
/// <para>
/// BOTH, because the number alone cannot detect a client that rebuilt a pack wrongly and the hash alone
/// cannot tell an operator which side is behind. The pipe is the separator INSIDE the payload and the colon
/// separates a refusal token's own fields, which is why nothing here ever emits a colon.
/// </para>
/// <para>
/// <b>An optional THIRD field carries the client's build ordinal</b>, the number
/// <c>MinimumClientBuild</c> is compared against (contracts 7.4). It is optional because the two-field
/// value is the pinned shape and a client that states no build is making no statement rather than a false
/// one: the gate then judges it on its content identity alone, and the client half of the same check is the
/// fetch loop's, against the manifest it just fetched (spec 8.7 step 3).
/// </para>
/// <para>
/// Parsing is STRICT and that is what keeps a refusal token parseable. A version number is plain invariant
/// decimal digits and a hash is exactly what a content address is, so a hostile layer value carrying a
/// colon, a pipe or anything else cannot be echoed back into the refusal the client parses. Anything that
/// fails the shape reads as no layer at all.
/// </para>
/// </summary>
public static class ContentIdentityLayer
{
    /// <summary>The separator between the fields of the layer value, which a hash and a decimal never contain.</summary>
    public const char FieldSeparator = '|';

    /// <summary>
    /// The layer value a client presents: the version number and the client manifest hash, and the build
    /// ordinal when the client states one.
    /// </summary>
    /// <param name="identity">The content version the client holds.</param>
    /// <param name="clientBuild">The client's build ordinal, or null to state none.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="identity"/> carries no content address, or <paramref name="clientBuild"/> is negative.
    /// </exception>
    public static string Format(ContentVersionIdentity identity, int? clientBuild = null)
    {
        RequireContentAddress(identity.ManifestHash, nameof(identity));
        if (identity.Number < 0)
        {
            throw new ArgumentException("A content version number is never negative.", nameof(identity));
        }

        if (clientBuild is < 0)
        {
            throw new ArgumentException("A build ordinal is never negative.", nameof(clientBuild));
        }

        string value = identity.Number.ToString(CultureInfo.InvariantCulture)
            + FieldSeparator
            + identity.ManifestHash;
        return clientBuild is null
            ? value
            : value + FieldSeparator + clientBuild.Value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads a layer value. False for anything that is not a decimal number and a content address, which is
    /// the same answer as a client that presented no layer at all: an unparseable statement is not a
    /// statement, and echoing one back into a refusal token is how a colon reaches a client's parser.
    /// </summary>
    /// <param name="value">The layer's label, as unwrapped.</param>
    /// <param name="identity">The version number and manifest hash the client claims, or default.</param>
    /// <param name="clientBuild">The client's build ordinal, or null when it stated none.</param>
    public static bool TryParse(string? value, out ContentVersionIdentity identity, out int? clientBuild)
    {
        identity = default;
        clientBuild = null;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        int first = value.IndexOf(FieldSeparator);
        if (first <= 0)
        {
            return false;
        }

        if (!TryParseOrdinal(value[..first], out int versionNumber))
        {
            return false;
        }

        string rest = value[(first + 1)..];
        int second = rest.IndexOf(FieldSeparator);
        string hash = second < 0 ? rest : rest[..second];
        if (!IsContentAddress(hash))
        {
            return false;
        }

        if (second >= 0)
        {
            string build = rest[(second + 1)..];
            if (build.IndexOf(FieldSeparator) >= 0 || !TryParseOrdinal(build, out int buildNumber))
            {
                return false;
            }

            clientBuild = buildNumber;
        }

        identity = new ContentVersionIdentity(versionNumber, hash);
        return true;
    }

    /// <summary>Wraps <paramref name="innerToken"/> in the content layer, which is what a client presents.</summary>
    /// <param name="identity">The content version the client holds.</param>
    /// <param name="innerToken">The token the gates inside this one read.</param>
    /// <param name="clientBuild">The client's build ordinal, or null to state none.</param>
    /// <exception cref="ArgumentException"><paramref name="identity"/> carries no content address.</exception>
    public static byte[] Wrap(ContentVersionIdentity identity, byte[]? innerToken = null, int? clientBuild = null)
        => HandshakeToken.Wrap(Format(identity, clientBuild), innerToken);

    /// <summary>
    /// Peels the content layer. False for a token that carries no layer, or one whose layer is not a version
    /// number and a content address, and the inner token is still handed back so a caller that refuses can
    /// see what was underneath.
    /// </summary>
    /// <param name="token">The token as it arrived at this layer.</param>
    /// <param name="identity">The version number and manifest hash the client claims, or default.</param>
    /// <param name="clientBuild">The client's build ordinal, or null when it stated none.</param>
    /// <param name="innerToken">What the gates inside this one read.</param>
    public static bool TryUnwrap(
        ReadOnlySpan<byte> token,
        out ContentVersionIdentity identity,
        out int? clientBuild,
        out byte[] innerToken)
    {
        HandshakeToken.TryUnwrap(token, out string label, out innerToken);
        return TryParse(label, out identity, out clientBuild);
    }

    /// <summary>
    /// Whether a string is a content address: exactly 64 lower hex characters. It is
    /// <see cref="FileSystemPackStore.IsContentAddress"/>, which is the tree's ONE definition of the rule, so
    /// the door and the store can never disagree about what a manifest hash looks like.
    /// </summary>
    internal static bool IsContentAddress(string? hash) => FileSystemPackStore.IsContentAddress(hash);

    /// <summary>
    /// The tree's one reading of a wire ordinal: PLAIN decimal digits, nothing else, which is what both this
    /// layer and <see cref="ContentRefusal"/> mean by a version number or a build ordinal.
    /// <para>
    /// The digit sweep is not redundant with the parse. <see cref="NumberStyles.None"/> still reads a
    /// trailing NUL as the end of the string, so a 47 with a NUL after it parses as 47, and a value that is
    /// not what it was written as travels on as though it were.
    /// </para>
    /// </summary>
    internal static bool TryParseOrdinal(string value, out int number)
    {
        number = 0;
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] is < '0' or > '9')
            {
                return false;
            }
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    static void RequireContentAddress(string? hash, string parameterName)
    {
        if (!IsContentAddress(hash))
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"'{hash}' is not a manifest hash, which is 64 lower hex characters. The door compares content addresses and nothing else."),
                parameterName);
        }
    }
}
