using System;
using System.Text;

namespace KhaozEngine.NetWorld;

/// <summary>
/// Wire helpers for the opt-in connect-time version handshake. A version-aware <see cref="WorldClient"/>
/// (one with <see cref="WorldClientConfig.ProtocolVersion"/> set) prepends its protocol version to the connect
/// token via <see cref="WrapToken"/>; a <see cref="VersionCheckingAuthenticator"/> on the server unwraps it,
/// applies the consumer-supplied compatibility rule, and on mismatch rejects with the structured reason
/// <see cref="IncompatibleReason"/> so the client can surface a distinct
/// <see cref="DisconnectReason.IncompatibleVersion"/> (rather than a generic token rejection). All purely
/// additive: an unwrapped token (a legacy client, or a client that did not opt in) decodes back as
/// version <c>""</c> with the original token bytes, so existing setups are byte-identical on the wire.
/// </summary>
public static class ProtocolHandshake
{
    // Marks a version-wrapped connect token. The leading 0x00 guarantees no collision with an unwrapped token:
    // the shipped authenticators read the token as a UTF-8 subject string (SignedToken / AllowAll), which is
    // never NUL-prefixed, so TryUnwrapToken can tell a wrapped token from a raw one with no false positives.
    private static readonly byte[] Magic = { 0x00, (byte)'K', (byte)'E', (byte)'V', (byte)'1' };

    /// <summary>Upper bound on a protocol-version string's UTF-8 encoding, in bytes (a single length byte).</summary>
    public const int MaxVersionBytes = 255;

    // Reject-reason envelope: a known prefix WorldClient recognizes to map a version-mismatch rejection to
    // DisconnectReason.IncompatibleVersion, carrying the server's required version as the detail.
    private const string IncompatiblePrefix = "ke:incompatible-version:";

    /// <summary>Builds a version-wrapped connect token: <c>[magic][verLen:byte][version utf8][inner token]</c>.
    /// <paramref name="innerToken"/> is the auth token the inner <see cref="Netcode.IConnectionAuthenticator"/>
    /// expects (may be null/empty for an anonymous connection).</summary>
    public static byte[] WrapToken(string protocolVersion, byte[]? innerToken)
    {
        if (protocolVersion is null) throw new ArgumentNullException(nameof(protocolVersion));
        byte[] ver = Encoding.UTF8.GetBytes(protocolVersion);
        if (ver.Length > MaxVersionBytes)
            throw new ArgumentException($"Protocol version exceeds {MaxVersionBytes} UTF-8 bytes.", nameof(protocolVersion));
        innerToken ??= Array.Empty<byte>();

        var buffer = new byte[Magic.Length + 1 + ver.Length + innerToken.Length];
        int i = 0;
        Array.Copy(Magic, 0, buffer, i, Magic.Length); i += Magic.Length;
        buffer[i++] = (byte)ver.Length;
        Array.Copy(ver, 0, buffer, i, ver.Length); i += ver.Length;
        Array.Copy(innerToken, 0, buffer, i, innerToken.Length);
        return buffer;
    }

    /// <summary>Splits a connect token produced by <see cref="WrapToken"/> into its version and inner token. An
    /// unwrapped token (no magic, or too short / a corrupt length) yields version <c>""</c> and the whole token as
    /// <paramref name="innerToken"/>, and returns <c>false</c> - so a legacy/non-opting client is handled as
    /// "version unknown" rather than rejected at decode. Never throws.</summary>
    public static bool TryUnwrapToken(ReadOnlySpan<byte> token, out string protocolVersion, out byte[] innerToken)
    {
        if (token.Length >= Magic.Length + 1 && token.Slice(0, Magic.Length).SequenceEqual(Magic))
        {
            int verLen = token[Magic.Length];
            int innerStart = Magic.Length + 1 + verLen;
            if (innerStart <= token.Length)
            {
                protocolVersion = Encoding.UTF8.GetString(token.Slice(Magic.Length + 1, verLen));
                innerToken = token.Slice(innerStart).ToArray();
                return true;
            }
        }
        protocolVersion = string.Empty;
        innerToken = token.ToArray();
        return false;
    }

    /// <summary>The structured reject reason for a version mismatch, carrying the server's required version.</summary>
    public static string IncompatibleReason(string requiredVersion) => IncompatiblePrefix + (requiredVersion ?? string.Empty);

    /// <summary>Recognizes a reason produced by <see cref="IncompatibleReason"/>, extracting the required version.
    /// Lets <see cref="WorldClient"/> distinguish a version-mismatch rejection from a plain token rejection.</summary>
    public static bool TryParseIncompatibleReason(string? reason, out string requiredVersion)
    {
        if (reason is not null && reason.StartsWith(IncompatiblePrefix, StringComparison.Ordinal))
        {
            requiredVersion = reason.Substring(IncompatiblePrefix.Length);
            return true;
        }
        requiredVersion = string.Empty;
        return false;
    }
}
