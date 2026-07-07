using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace KhaozEngine.Identity;

/// <summary>A stateless HMAC-SHA256 session token: subject (+ optional display name) and an expiry, signed with a
/// server secret. Verified offline with a fixed-time comparison. The consumer mints one after the exchange step and
/// verifies it on every subsequent request.</summary>
public static class SessionToken
{
    private const string Version = "v1";

    public static string Mint(string subject, string? displayName, DateTimeOffset expiry, byte[] secret)
    {
        if (string.IsNullOrEmpty(subject)) throw new ArgumentException("subject required", nameof(subject));
        long exp = expiry.ToUnixTimeSeconds();
        string sub = B64(Encoding.UTF8.GetBytes(subject));
        string name = B64(Encoding.UTF8.GetBytes(displayName ?? string.Empty));
        string payload = $"{subject}.{displayName ?? string.Empty}.{exp.ToString(CultureInfo.InvariantCulture)}";
        string mac = B64(Hmac(payload, secret));
        return $"{Version}.{sub}.{name}.{exp.ToString(CultureInfo.InvariantCulture)}.{mac}";
    }

    public static bool TryVerify(string token, byte[] secret, DateTimeOffset now,
        out string subject, out string? displayName, out string reason)
    {
        subject = string.Empty; displayName = null; reason = string.Empty;
        if (string.IsNullOrEmpty(token)) { reason = "empty token"; return false; }
        string[] parts = token.Split('.');
        if (parts.Length != 5 || parts[0] != Version) { reason = "malformed token"; return false; }
        if (!long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out long exp))
        { reason = "malformed expiry"; return false; }
        string sub, name;
        try { sub = Encoding.UTF8.GetString(UnB64(parts[1])); name = Encoding.UTF8.GetString(UnB64(parts[2])); }
        catch (FormatException) { reason = "malformed payload"; return false; }
        string payload = $"{sub}.{name}.{parts[3]}";
        byte[] expected = Hmac(payload, secret);
        byte[] got;
        try { got = UnB64(parts[4]); } catch (FormatException) { reason = "malformed mac"; return false; }
        if (got.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(got, expected))
        { reason = "signature mismatch"; return false; }
        if (now.ToUnixTimeSeconds() >= exp) { reason = "token expired"; return false; }
        subject = sub; displayName = name.Length == 0 ? null : name;
        return true;
    }

    private static byte[] Hmac(string data, byte[] secret)
    {
        using HMACSHA256 h = new(secret);
        return h.ComputeHash(Encoding.UTF8.GetBytes(data));
    }
    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] UnB64(string s)
    {
        string t = s.Replace('-', '+').Replace('_', '/');
        t = (t.Length % 4) switch { 2 => t + "==", 3 => t + "=", _ => t };
        return Convert.FromBase64String(t);
    }
}
