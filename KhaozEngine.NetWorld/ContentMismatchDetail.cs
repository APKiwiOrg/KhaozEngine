using KhaozEngine.Netcode;

namespace KhaozEngine.NetWorld;

/// <summary>
/// Both sides of a <see cref="DisconnectReason.ContentMismatch"/> refusal, read by <see cref="WorldClient"/> from
/// the server's reject token so a game can show its own localized "different content" line without parsing it.
/// <para>The token is <see cref="HandshakeToken.WorldMismatchReason"/>, <c>ke:world-mismatch:&lt;server&gt;|&lt;client&gt;</c>,
/// which <see cref="WorldIdentityGateAuthenticator"/> writes. The engine attaches no meaning to either value: each is
/// the opaque identity one side configured, <see cref="WorldClientConfig.ContentIdentity"/> on the client and the
/// gate's identity on the server.</para>
/// </summary>
/// <param name="ServerIdentity">The identity the server requires.</param>
/// <param name="ClientIdentity">What the server read at the client's identity layer. Empty when the client configured
/// no <see cref="WorldClientConfig.ContentIdentity"/> and its auth token carries no labelled layer. When the client
/// configured none but its auth token IS a labelled layer, this is that layer's label, because the gate peels
/// whatever layer sits in the identity position.</param>
public readonly record struct ContentMismatchDetail(string ServerIdentity, string ClientIdentity)
{
    /// <summary>Recognizes a content-identity refusal token, extracting both identities. False for any other reason,
    /// including a world-mismatch body with no pipe.</summary>
    public static bool TryParse(string? reason, out ContentMismatchDetail detail)
    {
        if (HandshakeToken.TryParseWorldMismatch(reason, out string server, out string client))
        {
            detail = new ContentMismatchDetail(server, client);
            return true;
        }

        detail = default;
        return false;
    }
}
