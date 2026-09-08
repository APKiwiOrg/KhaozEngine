using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace KhaozEngine.Netcode;

/// <summary>
/// Zero-dependency HMAC-SHA256 connect-token primitive: a stateless, self-verifying bearer token binding a
/// <c>subject</c> (the stable account/player identity) to an expiry, signed with a shared secret. The base v1 format
/// is <c>v1.&lt;subject&gt;.&lt;expUnix&gt;.&lt;base64url-HMACSHA256&gt;</c>. Later formats add base64url
/// display-name and persistence-key claims before the expiry. Every field before the MAC is signed. The
/// <c>subject</c> may not contain a '.' so every version splits cleanly. The matching
/// <see cref="IConnectionAuthenticator"/> is <see cref="HmacTokenAuthenticator"/>.
/// </summary>
public static class SignedToken
{
    private const string Version = "v1";
    // v2 adds a base64url display-name claim between subject and expiry:
    // v2.<subject>.<base64url-UTF8 displayName>.<expUnix>.<base64url-mac>. base64url contains no '.', so the five
    // fields still split cleanly; the signature covers v2.<subject>.<nameB64>.<expUnix>. A v1 token (no name) is
    // unchanged and still verifies. The display name is cosmetic and distinct from the verified subject/account id.
    private const string Version2 = "v2";
    // v3 adds a base64url persistence-key claim after the display name. The claim is a durable identity hint and
    // does not replace the subject for authentication or duplicate-session admission.
    private const string Version3 = "v3";

    /// <summary>
    /// Mints a signed token for <paramref name="subject"/> expiring at <paramref name="expiry"/>, signed with
    /// <paramref name="secret"/>. The subject must not contain '.' (the field separator).
    /// </summary>
    public static string Mint(string subject, DateTimeOffset expiry, byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(secret);
        if (subject.Contains('.'))
            throw new ArgumentException("subject must not contain '.'", nameof(subject));

        long expUnix = expiry.ToUnixTimeSeconds();
        string signed = string.Concat(Version, ".", subject, ".", expUnix.ToString(CultureInfo.InvariantCulture));
        string mac = ToBase64Url(Hmac(secret, signed));
        return string.Concat(signed, ".", mac);
    }

    /// <summary>
    /// Mints a v2 signed token carrying an optional human <paramref name="displayName"/> claim alongside
    /// <paramref name="subject"/> (a cosmetic name, NOT the account id). The name is base64url-encoded so it may
    /// contain any character (including '.'); an empty name produces an empty claim field. The subject still must
    /// not contain '.'. Verify with the <c>out displayName</c> <see cref="TryVerify(string,byte[],DateTimeOffset,out string,out string,out string)"/> overload.
    /// </summary>
    public static string Mint(string subject, string displayName, DateTimeOffset expiry, byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(secret);
        if (subject.Contains('.'))
            throw new ArgumentException("subject must not contain '.'", nameof(subject));

        long expUnix = expiry.ToUnixTimeSeconds();
        string nameField = displayName.Length == 0 ? string.Empty : ToBase64Url(Encoding.UTF8.GetBytes(displayName));
        string signed = string.Concat(Version2, ".", subject, ".", nameField, ".", expUnix.ToString(CultureInfo.InvariantCulture));
        string mac = ToBase64Url(Hmac(secret, signed));
        return string.Concat(signed, ".", mac);
    }

    /// <summary>
    /// Mints a v3 signed token carrying an optional durable <paramref name="persistenceKey"/> claim beside the
    /// verified subject and display name. The claim is base64url-encoded and covered by the token signature.
    /// </summary>
    public static string Mint(string subject, string displayName, string persistenceKey, DateTimeOffset expiry,
        byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(persistenceKey);
        ArgumentNullException.ThrowIfNull(secret);
        if (subject.Contains('.'))
            throw new ArgumentException("subject must not contain '.'", nameof(subject));

        long expUnix = expiry.ToUnixTimeSeconds();
        string nameField = displayName.Length == 0 ? string.Empty : ToBase64Url(Encoding.UTF8.GetBytes(displayName));
        string persistenceField = persistenceKey.Length == 0
            ? string.Empty
            : ToBase64Url(Encoding.UTF8.GetBytes(persistenceKey));
        string signed = string.Concat(Version3, ".", subject, ".", nameField, ".", persistenceField, ".",
            expUnix.ToString(CultureInfo.InvariantCulture));
        string mac = ToBase64Url(Hmac(secret, signed));
        return string.Concat(signed, ".", mac);
    }

    /// <summary>
    /// Verifies <paramref name="token"/> against <paramref name="secret"/> at instant <paramref name="now"/>.
    /// On success returns true with the embedded <paramref name="subject"/>; on failure returns false with an empty
    /// subject and a short <paramref name="reason"/> (<c>"malformed"</c>, <c>"bad signature"</c>, or
    /// <c>"expired"</c>). Signature is checked before expiry, with a fixed-time HMAC compare. Accepts v1, v2, and v3
    /// tokens. Any additional verified claims are dropped by this overload.
    /// </summary>
    public static bool TryVerify(string token, byte[] secret, DateTimeOffset now, out string subject, out string reason) =>
        TryVerify(token, secret, now, out subject, out _, out _, out reason);

    /// <summary>
    /// As <see cref="TryVerify(string,byte[],DateTimeOffset,out string,out string)"/>, also surfacing the verified
    /// <paramref name="displayName"/> claim (empty for a v1 token or a token with no name). Any persistence-key claim
    /// is dropped by this overload. The name is covered by the same signature as the subject.
    /// </summary>
    public static bool TryVerify(string token, byte[] secret, DateTimeOffset now, out string subject, out string displayName, out string reason)
        => TryVerify(token, secret, now, out subject, out displayName, out _, out reason);

    /// <summary>
    /// As <see cref="TryVerify(string,byte[],DateTimeOffset,out string,out string,out string)"/>, also surfacing the
    /// verified <paramref name="persistenceKey"/> claim. The claim is empty for v1 and v2 tokens.
    /// </summary>
    public static bool TryVerify(string token, byte[] secret, DateTimeOffset now, out string subject,
        out string displayName, out string persistenceKey, out string reason)
    {
        subject = string.Empty;
        displayName = string.Empty;
        persistenceKey = string.Empty;
        reason = string.Empty;
        ArgumentNullException.ThrowIfNull(secret);

        if (string.IsNullOrEmpty(token)) { reason = "malformed"; return false; }

        string[] parts = token.Split('.');
        // v1 has 4 fields, v2 has 5, and v3 has 6.
        int nameIndex, persistenceIndex, expIndex;
        if (parts.Length == 4 && parts[0] == Version) { nameIndex = -1; persistenceIndex = -1; expIndex = 2; }
        else if (parts.Length == 5 && parts[0] == Version2) { nameIndex = 2; persistenceIndex = -1; expIndex = 3; }
        else if (parts.Length == 6 && parts[0] == Version3) { nameIndex = 2; persistenceIndex = 3; expIndex = 4; }
        else { reason = "malformed"; return false; }

        if (!long.TryParse(parts[expIndex], NumberStyles.None, CultureInfo.InvariantCulture, out long expUnix))
        {
            reason = "malformed";
            return false;
        }

        // The signed message is everything before the final '.' (the mac is the last field).
        string signed = token.Substring(0, token.LastIndexOf('.'));
        byte[] expected = Hmac(secret, signed);
        if (!TryFromBase64Url(parts[^1], out byte[] provided)
            || !CryptographicOperations.FixedTimeEquals(provided, expected))
        {
            reason = "bad signature";
            return false;
        }

        if (now.ToUnixTimeSeconds() > expUnix)
        {
            reason = "expired";
            return false;
        }

        string verifiedDisplayName = string.Empty;
        if (nameIndex >= 0 && !TryDecodeClaim(parts[nameIndex], out verifiedDisplayName))
        {
            reason = "malformed";
            return false;
        }

        string verifiedPersistenceKey = string.Empty;
        if (persistenceIndex >= 0 && !TryDecodeClaim(parts[persistenceIndex], out verifiedPersistenceKey))
        {
            reason = "malformed";
            return false;
        }

        subject = parts[1];
        displayName = verifiedDisplayName;
        persistenceKey = verifiedPersistenceKey;
        return true;
    }

    /// <summary>
    /// Secret-free STRUCTURAL parse of a token: extracts the <paramref name="subject"/>, expiry
    /// (<paramref name="expUnix"/>, Unix seconds), and optional <paramref name="displayName"/> from a well-formed
    /// v1, v2, or v3 token WITHOUT the HMAC secret. This does NOT verify the signature and does NOT check
    /// expiry, so a true, a tampered, and an expired token are indistinguishable to it. It is NOT authentication: use
    /// it only as a cheap client-side shape pre-filter (e.g. sanity-checking a pasted or launch-supplied token before
    /// attempting a connect, where the secret lives only on the server). The authoritative check is always
    /// <see cref="TryVerify(string,byte[],DateTimeOffset,out string,out string)"/> on the server.
    /// <para>
    /// Accepts the structure <see cref="TryVerify(string,byte[],DateTimeOffset,out string,out string,out string,out string)"/>
    /// accepts before checking the signature: the matching version layout, numeric expiry, and decodable claim
    /// fields. It is derived from this type's own layout rather than a consumer copy. The signature is not inspected,
    /// and the v3 persistence-key claim is validated structurally but never surfaced.
    /// </para>
    /// </summary>
    /// <param name="token">The candidate token string.</param>
    /// <param name="subject">On success, the embedded subject (the account/player id); empty otherwise. NOT verified.</param>
    /// <param name="expUnix">On success, the embedded expiry in Unix seconds; 0 otherwise. NOT checked against a clock.</param>
    /// <param name="displayName">On success, the display-name claim: <c>null</c> for a v1 token (no name field at
    /// all), the empty string for a v2 or v3 token with an empty name, or the decoded name for a token that carries one.
    /// NOT verified (the signature is not checked, so the name is not trustworthy).</param>
    /// <returns>True if the token is structurally a well-formed v1, v2, or v3 token. False otherwise.</returns>
    public static bool TryParseUnverified(string token, out string subject, out long expUnix, out string? displayName)
    {
        subject = string.Empty;
        expUnix = 0;
        displayName = null;

        if (string.IsNullOrEmpty(token)) return false;

        string[] parts = token.Split('.');
        // These are the same three layouts TryVerify accepts.
        int nameIndex, persistenceIndex, expIndex;
        if (parts.Length == 4 && parts[0] == Version) { nameIndex = -1; persistenceIndex = -1; expIndex = 2; }
        else if (parts.Length == 5 && parts[0] == Version2) { nameIndex = 2; persistenceIndex = -1; expIndex = 3; }
        else if (parts.Length == 6 && parts[0] == Version3) { nameIndex = 2; persistenceIndex = 3; expIndex = 4; }
        else return false;

        if (!long.TryParse(parts[expIndex], NumberStyles.None, CultureInfo.InvariantCulture, out expUnix))
        {
            expUnix = 0;
            return false;
        }

        string? parsedDisplayName = null;
        if (nameIndex >= 0)
        {
            if (parts[nameIndex].Length == 0)
            {
                parsedDisplayName = string.Empty;
            }
            else if (TryFromBase64Url(parts[nameIndex], out byte[] nameBytes))
            {
                parsedDisplayName = Encoding.UTF8.GetString(nameBytes);
            }
            else
            {
                // A non-empty v2 name field that is not valid base64url is structurally malformed - TryVerify rejects
                // it as "malformed" too (after the signature passes). Reset the fields so a false return yields no
                // half-parsed values.
                expUnix = 0;
                return false;
            }
        }

        // Validate the v3 claim field against the verifier's shape, then discard it. This parse is deliberately not
        // an authentication surface and must not hand an unverified durable identity to a caller.
        if (persistenceIndex >= 0 && parts[persistenceIndex].Length > 0
            && !TryFromBase64Url(parts[persistenceIndex], out _))
        {
            expUnix = 0;
            return false;
        }

        subject = parts[1];
        displayName = parsedDisplayName;
        return true;
    }

    private static bool TryDecodeClaim(string field, out string value)
    {
        value = string.Empty;
        if (field.Length == 0) return true;
        if (!TryFromBase64Url(field, out byte[] bytes)) return false;
        value = Encoding.UTF8.GetString(bytes);
        return true;
    }

    private static byte[] Hmac(byte[] secret, string message)
    {
        using var hmac = new HMACSHA256(secret);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryFromBase64Url(string s, out byte[] bytes)
    {
        string b64 = s.Replace('-', '+').Replace('_', '/');
        switch (b64.Length % 4)
        {
            case 2: b64 += "=="; break;
            case 3: b64 += "="; break;
            case 1: bytes = Array.Empty<byte>(); return false; // not a valid base64url length
        }
        try { bytes = Convert.FromBase64String(b64); return true; }
        catch (FormatException) { bytes = Array.Empty<byte>(); return false; }
    }
}

/// <summary>
/// <see cref="IConnectionAuthenticator"/> over <see cref="SignedToken"/> verification. Accepts a connection iff its
/// connect token is a valid, unexpired <see cref="SignedToken"/> for the configured secret, binding the connection
/// to the token's verified subject. The clock is injected so it is deterministically testable (and so a host can
/// supply a monotonic/NTP-corrected time source).
/// </summary>
public sealed class HmacTokenAuthenticator : IConnectionAuthenticator, IConnectionDisplayName,
    IConnectionPersistenceKey
{
    private readonly byte[] secret;
    private readonly Func<DateTimeOffset> clock;

    public HmacTokenAuthenticator(byte[] secret, Func<DateTimeOffset> clock)
    {
        this.secret = secret ?? throw new ArgumentNullException(nameof(secret));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public bool TryAuthenticate(ReadOnlySpan<byte> token, out string subject, out string rejectReason)
    {
        string tokenStr = token.Length > 0 ? Encoding.UTF8.GetString(token) : string.Empty;
        return SignedToken.TryVerify(tokenStr, secret, clock(), out subject, out rejectReason);
    }

    /// <summary>The verified v2 display-name claim on the token (empty for a v1 token, no name, or a token that
    /// fails verification). Re-verifies the same token, so the name returned is always signature-checked.</summary>
    public string ReadDisplayName(ReadOnlySpan<byte> token)
    {
        string tokenStr = token.Length > 0 ? Encoding.UTF8.GetString(token) : string.Empty;
        return SignedToken.TryVerify(tokenStr, secret, clock(), out _, out string displayName, out _)
            ? displayName : string.Empty;
    }

    /// <summary>
    /// The verified v3 persistence-key claim on the token. Returns an empty string for v1, v2, an empty claim, or a
    /// token that fails verification.
    /// </summary>
    public string ReadPersistenceKey(ReadOnlySpan<byte> token)
    {
        string tokenStr = token.Length > 0 ? Encoding.UTF8.GetString(token) : string.Empty;
        return SignedToken.TryVerify(tokenStr, secret, clock(), out _, out _, out string persistenceKey, out _)
            ? persistenceKey : string.Empty;
    }
}
