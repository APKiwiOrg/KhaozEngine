using System;
using System.Text;

namespace KhaozEngine.Netcode;

/// <summary>
/// Seam deciding whether a connecting client may join, given the token it presented in its Hello, and (on accept)
/// the verified <c>subject</c> the connection is bound to (the stable account/player identity). The engine ships
/// <see cref="AllowAllAuthenticator"/> for dev/local and <see cref="HmacTokenAuthenticator"/> for signed tokens; a
/// real account/token check is otherwise the game's/infra's.
/// </summary>
public interface IConnectionAuthenticator
{
    /// <summary>
    /// Returns true to accept. On accept, <paramref name="subject"/> is the verified identity the server binds the
    /// connection to (empty for an anonymous/guest connection); on reject, it is empty and
    /// <paramref name="rejectReason"/> is sent to the client.
    /// </summary>
    bool TryAuthenticate(ReadOnlySpan<byte> token, out string subject, out string rejectReason);
}

/// <summary>
/// Optional companion to <see cref="IConnectionAuthenticator"/>: an authenticator that can also surface a verified
/// human display name carried by the connect token. After a successful
/// <see cref="IConnectionAuthenticator.TryAuthenticate"/>, <see cref="NetServer"/> probes the authenticator for this
/// interface and, when present, reads the display name from the SAME token and includes it on the Joined event
/// (<see cref="ServerSessionEvent.DisplayName"/>). The display name is cosmetic and independent of the verified
/// <c>subject</c>/account id; an authenticator that does not implement this yields an empty display name.
/// <see cref="HmacTokenAuthenticator"/> implements it (a v2 <see cref="SignedToken"/> claim);
/// <see cref="AllowAllAuthenticator"/> does not.
/// </summary>
public interface IConnectionDisplayName
{
    /// <summary>Reads the verified display name carried by an already-accepted <paramref name="token"/> (empty when
    /// the token carries none). Only called after <see cref="IConnectionAuthenticator.TryAuthenticate"/> accepted the
    /// same token, so the name has been verified alongside the subject.</summary>
    string ReadDisplayName(ReadOnlySpan<byte> token);
}

/// <summary>
/// Accepts every connection, binding it to the presented token decoded as a UTF-8 subject (empty when no token is
/// presented). Dev/local default; never use as the only gate on an exposed server.
/// </summary>
public sealed class AllowAllAuthenticator : IConnectionAuthenticator
{
    public bool TryAuthenticate(ReadOnlySpan<byte> token, out string subject, out string rejectReason)
    {
        subject = token.Length > 0 ? Encoding.UTF8.GetString(token) : string.Empty;
        rejectReason = string.Empty;
        return true;
    }
}
