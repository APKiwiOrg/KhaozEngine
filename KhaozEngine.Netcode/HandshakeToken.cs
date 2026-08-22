using System;
using System.Text;

namespace KhaozEngine.Netcode;

/// <summary>
/// The connect-token LAYER codec plus the engine's refusal reason tokens. A connect token is a nest of labelled
/// layers, outermost first, each <c>[magic][labelLen:byte][label utf8][inner]</c>, so a gate peels one layer,
/// decides, and hands the rest to the gate inside it. An unlabelled token unwraps to label <c>""</c> with the
/// whole token as the inner, so a legacy peer that never opted in is handled as "unknown" rather than throwing.
/// <para>Reason tokens are STABLE WIRE TOKENS, not display text: a client matches the token and shows its own
/// localized string.</para>
/// <para>The layer bytes are the format <c>KhaozEngine.NetWorld.ProtocolHandshake</c> shipped, which now delegates
/// here so the engine has exactly one implementation of them.</para>
/// </summary>
public static class HandshakeToken
{
    // The leading 0x00 guarantees no collision with an unwrapped token: the shipped authenticators read a token as
    // a UTF-8 subject string, which is never NUL-prefixed.
    static readonly byte[] Magic = { 0x00, (byte)'K', (byte)'E', (byte)'V', (byte)'1' };

    const string IncompatiblePrefix = "ke:incompatible-version:";
    const string WorldMismatchPrefix = "ke:world-mismatch:";

    /// <summary>Upper bound on a layer label's UTF-8 encoding, in bytes (a single length byte).</summary>
    public const int MaxLabelBytes = 255;

    /// <summary>The refusal token for a banned account. Carries no detail: a ban reason is an operator concern,
    /// not something to hand the banned client.</summary>
    public const string BannedReason = "ke:banned";

    /// <summary>Wraps <paramref name="innerToken"/> in one labelled layer.</summary>
    public static byte[] Wrap(string label, byte[]? innerToken)
    {
        ArgumentNullException.ThrowIfNull(label);
        byte[] lbl = Encoding.UTF8.GetBytes(label);
        if (lbl.Length > MaxLabelBytes)
            throw new ArgumentException($"Label exceeds {MaxLabelBytes} UTF-8 bytes.", nameof(label));
        innerToken ??= Array.Empty<byte>();
        var buffer = new byte[Magic.Length + 1 + lbl.Length + innerToken.Length];
        int i = 0;
        Array.Copy(Magic, 0, buffer, i, Magic.Length); i += Magic.Length;
        buffer[i++] = (byte)lbl.Length;
        Array.Copy(lbl, 0, buffer, i, lbl.Length); i += lbl.Length;
        Array.Copy(innerToken, 0, buffer, i, innerToken.Length);
        return buffer;
    }

    /// <summary>Peels one layer. An unlabelled or corrupt token yields label <c>""</c> plus the whole token, and
    /// returns false. Never throws.</summary>
    public static bool TryUnwrap(ReadOnlySpan<byte> token, out string label, out byte[] innerToken)
    {
        if (token.Length >= Magic.Length + 1 && token.Slice(0, Magic.Length).SequenceEqual(Magic))
        {
            int len = token[Magic.Length];
            int innerStart = Magic.Length + 1 + len;
            if (innerStart <= token.Length)
            {
                label = Encoding.UTF8.GetString(token.Slice(Magic.Length + 1, len));
                innerToken = token.Slice(innerStart).ToArray();
                return true;
            }
        }
        label = string.Empty;
        innerToken = token.ToArray();
        return false;
    }

    /// <summary>The refusal token for a protocol-version mismatch, carrying the version the server requires.</summary>
    public static string IncompatibleVersionReason(string requiredVersion) =>
        IncompatiblePrefix + (requiredVersion ?? string.Empty);

    /// <summary>Recognizes <see cref="IncompatibleVersionReason"/>, extracting the required version.</summary>
    public static bool TryParseIncompatibleVersion(string? reason, out string requiredVersion)
    {
        if (reason is not null && reason.StartsWith(IncompatiblePrefix, StringComparison.Ordinal))
        {
            requiredVersion = reason.Substring(IncompatiblePrefix.Length);
            return true;
        }
        requiredVersion = string.Empty;
        return false;
    }

    /// <summary>The refusal token for a world mismatch, carrying BOTH hashes separated by a pipe (a hex hash never
    /// contains one), so the client can say which world it built against.</summary>
    public static string WorldMismatchReason(string serverHash, string clientHash) =>
        WorldMismatchPrefix + (serverHash ?? string.Empty) + "|" + (clientHash ?? string.Empty);

    /// <summary>Recognizes <see cref="WorldMismatchReason"/>, extracting both hashes.</summary>
    public static bool TryParseWorldMismatch(string? reason, out string serverHash, out string clientHash)
    {
        serverHash = string.Empty;
        clientHash = string.Empty;
        if (reason is null || !reason.StartsWith(WorldMismatchPrefix, StringComparison.Ordinal)) return false;
        string body = reason.Substring(WorldMismatchPrefix.Length);
        int pipe = body.IndexOf('|');
        if (pipe < 0) return false;
        serverHash = body.Substring(0, pipe);
        clientHash = body.Substring(pipe + 1);
        return true;
    }
}
