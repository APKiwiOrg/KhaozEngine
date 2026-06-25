using System;

namespace KhaozEngine.Netcode;

/// <summary>
/// Seam deciding whether a connecting client may join, given the token it presented in its Hello. The engine
/// ships <see cref="AllowAllAuthenticator"/> for dev/local; a real account/token check is the game's/infra's.
/// </summary>
public interface IConnectionAuthenticator
{
    /// <summary>Returns true to accept; on false, <paramref name="rejectReason"/> is sent to the client.</summary>
    bool TryAuthenticate(ReadOnlySpan<byte> token, out string rejectReason);
}

/// <summary>Accepts every connection. Dev/local default; never use as the only gate on an exposed server.</summary>
public sealed class AllowAllAuthenticator : IConnectionAuthenticator
{
    public bool TryAuthenticate(ReadOnlySpan<byte> token, out string rejectReason)
    {
        rejectReason = string.Empty;
        return true;
    }
}
