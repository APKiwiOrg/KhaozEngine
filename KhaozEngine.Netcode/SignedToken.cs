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
    /// Verifies <paramref name="token"/> against <paramref name="secret"/> at instant <paramref name="now"/>.
    /// On success returns true with the embedded <paramref name="subject"/>; on failure returns false with an empty
    /// subject and a short <paramref name="reason"/> (<c>"malformed"</c>, <c>"bad signature"</c>, or
    /// <c>"expired"</c>). Signature is checked before expiry, with a fixed-time HMAC compare.
    /// </summary>
    public static bool TryVerify(string token, byte[] secret, DateTimeOffset now, out string subject, out string reason)
    {
        subject = string.Empty;
        reason = string.Empty;
        ArgumentNullException.ThrowIfNull(secret);

        if (string.IsNullOrEmpty(token)) { reason = "malformed"; return false; }

        string[] parts = token.Split('.');
        if (parts.Length != 4 || parts[0] != Version)
        {
            reason = "malformed";
            return false;
        }
        if (!long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long expUnix))
        {
            reason = "malformed";
            return false;
        }

        // The signed message is everything before the final '.' (v1.<subject>.<expUnix>).
        string signed = token.Substring(0, token.LastIndexOf('.'));
        byte[] expected = Hmac(secret, signed);
        if (!TryFromBase64Url(parts[3], out byte[] provided)
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
/// <see cref="IConnectionAuthenticator"/> over <see cref="SignedToken.TryVerify"/>: accepts a connection iff its
/// connect token is a valid, unexpired <see cref="SignedToken"/> for the configured secret, binding the connection
/// to the token's verified subject. The clock is injected so it is deterministically testable (and so a host can
/// supply a monotonic/NTP-corrected time source).
/// </summary>
public sealed class HmacTokenAuthenticator : IConnectionAuthenticator
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
}
