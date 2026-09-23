using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Netcode;

namespace KhaozEngine.NetWorld;

/// <summary>
/// Both sides of a <see cref="DisconnectReason.ContentVersionMismatch"/> refusal, read by <see cref="WorldClient"/>
/// from the catalog content door's reject token so a game can show its own localized "update your content" line
/// without parsing it.
/// <para>The token is <see cref="ContentRefusal.Mismatch"/>,
/// <c>ke:content-mismatch:&lt;serverVersion&gt;|&lt;serverHash&gt;|&lt;clientVersion&gt;|&lt;clientHash&gt;</c>, which
/// <see cref="ContentIdentityGateAuthenticator"/> writes. It is read with <see cref="ContentRefusal.TryParseMismatch"/>,
/// the one strict parser, so a token that parser refuses is no content refusal here either.</para>
/// </summary>
/// <param name="Server">The content version the server serves: its number and CLIENT manifest hash.</param>
/// <param name="Client">The version the client presented, or null when it presented no content layer or one that did
/// not parse. Null is the absence of a statement, never version 0.</param>
public readonly record struct ContentVersionMismatchDetail(ContentVersionIdentity Server, ContentVersionIdentity? Client)
{
    /// <summary>Recognizes a catalog content-version refusal token, extracting both sides. False for any other
    /// reason, including a <c>ke:content-mismatch:</c> body <see cref="ContentRefusal.TryParseMismatch"/> refuses.</summary>
    public static bool TryParse(string? reason, out ContentVersionMismatchDetail detail)
    {
        if (ContentRefusal.TryParseMismatch(reason, out ContentVersionIdentity server, out ContentVersionIdentity? client))
        {
            detail = new ContentVersionMismatchDetail(server, client);
            return true;
        }

        detail = default;
        return false;
    }
}
