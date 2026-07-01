using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace KhaozEngine.Netcode;

/// <summary>
/// Zero-dependency HMAC-SHA256 connect-token primitive: a stateless, self-verifying bearer token binding a
/// <c>subject</c> (the stable account/player identity) to an expiry, signed with a shared secret. The wire format
/// is <c>v1.&lt;subject&gt;.&lt;expUnix&gt;.&lt;base64url-HMACSHA256&gt;</c> where the signature covers
/// <c>v1.&lt;subject&gt;.&lt;expUnix&gt;</c>; the <c>subject</c> may not contain a '.' so the four fields split
/// cleanly. No allocations beyond the token string; pure BCL crypto (no external dependency). The matching
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
    /// Verifies <paramref name="token"/> against <paramref name="secret"/> at instant <paramref name="now"/>.
    /// On success returns true with the embedded <paramref name="subject"/>; on failure returns false with an empty
    /// subject and a short <paramref name="reason"/> (<c>"malformed"</c>, <c>"bad signature"</c>, or
    /// <c>"expired"</c>). Signature is checked before expiry, with a fixed-time HMAC compare. Accepts both v1 and v2
    /// tokens (any display-name claim is verified but dropped; use the <c>out displayName</c> overload to read it).
    /// </summary>
    public static bool TryVerify(string token, byte[] secret, DateTimeOffset now, out string subject, out string reason) =>
        TryVerify(token, secret, now, out subject, out _, out reason);

    /// <summary>
    /// As <see cref="TryVerify(string,byte[],DateTimeOffset,out string,out string)"/>, also surfacing the verified
    /// <paramref name="displayName"/> claim (empty for a v1 token or a v2 token with no name). The name is covered by
    /// the same signature as the subject, so a valid token's name is trustworthy.
    /// </summary>
    public static bool TryVerify(string token, byte[] secret, DateTimeOffset now, out string subject, out string displayName, out string reason)
    {
        subject = string.Empty;
        displayName = string.Empty;
        reason = string.Empty;
        ArgumentNullException.ThrowIfNull(secret);

        if (string.IsNullOrEmpty(token)) { reason = "malformed"; return false; }

        string[] parts = token.Split('.');
        // v1.<subject>.<expUnix>.<mac> (4) or v2.<subject>.<nameB64>.<expUnix>.<mac> (5).
        int nameIndex, expIndex;
        if (parts.Length == 4 && parts[0] == Version) { nameIndex = -1; expIndex = 2; }
        else if (parts.Length == 5 && parts[0] == Version2) { nameIndex = 2; expIndex = 3; }
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

        subject = parts[1];
        if (nameIndex >= 0 && parts[nameIndex].Length > 0)
        {
            // The name was inside the signed message, so it is verified; decode it only after the signature passed.
            if (!TryFromBase64Url(parts[nameIndex], out byte[] nameBytes))
            {
                subject = string.Empty;
                reason = "malformed";
                return false;
            }
            displayName = Encoding.UTF8.GetString(nameBytes);
        }
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
/// <see cref="IConnectionAuthenticator"/> over <see cref="SignedToken.TryVerify(string, byte[], System.DateTimeOffset, out string, out string)"/>: accepts a connection iff its
/// connect token is a valid, unexpired <see cref="SignedToken"/> for the configured secret, binding the connection
/// to the token's verified subject. The clock is injected so it is deterministically testable (and so a host can
/// supply a monotonic/NTP-corrected time source).
/// </summary>
public sealed class HmacTokenAuthenticator : IConnectionAuthenticator, IConnectionDisplayName
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
}
