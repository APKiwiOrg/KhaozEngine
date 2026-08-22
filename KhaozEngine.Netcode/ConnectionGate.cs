using System;

namespace KhaozEngine.Netcode;

/// <summary>Refuses a peer on a protocol-version mismatch before anything else runs.</summary>
public sealed class VersionGateAuthenticator : IConnectionAuthenticator, IConnectionDisplayName
{
    readonly string serverVersion;
    readonly Func<string, bool> isCompatible;
    readonly IConnectionAuthenticator inner;

    /// <summary>Gates on <paramref name="isCompatible"/> and delegates to <paramref name="inner"/> on a match.</summary>
    public VersionGateAuthenticator(string serverVersion, Func<string, bool> isCompatible,
        IConnectionAuthenticator? inner = null)
    {
        this.serverVersion = serverVersion ?? throw new ArgumentNullException(nameof(serverVersion));
        this.isCompatible = isCompatible ?? throw new ArgumentNullException(nameof(isCompatible));
        this.inner = inner ?? new AllowAllAuthenticator();
    }

    /// <inheritdoc/>
    public bool TryAuthenticate(ReadOnlySpan<byte> token, out string subject, out string rejectReason)
    {
        HandshakeToken.TryUnwrap(token, out string clientVersion, out byte[] innerToken);
        if (!isCompatible(clientVersion))
        {
            subject = string.Empty;
            rejectReason = HandshakeToken.IncompatibleVersionReason(serverVersion);
            return false;
        }
        return inner.TryAuthenticate(innerToken, out subject, out rejectReason);
    }

    /// <inheritdoc/>
    public string ReadDisplayName(ReadOnlySpan<byte> token)
    {
        HandshakeToken.TryUnwrap(token, out _, out byte[] innerToken);
        return inner is IConnectionDisplayName named ? named.ReadDisplayName(innerToken) : string.Empty;
    }
}

/// <summary>Refuses a peer built against a different WORLD, so it can never join and render its own map while the
/// server simulates another. Distinct from the version gate on purpose: a patch that leaves the world alone still
/// interoperates.</summary>
public sealed class WorldIdentityGateAuthenticator : IConnectionAuthenticator, IConnectionDisplayName
{
    readonly string worldHash;
    readonly IConnectionAuthenticator inner;
    readonly Action<string>? log;

    /// <summary>Gates on <paramref name="worldHash"/> and delegates to <paramref name="inner"/> on a match.</summary>
    public WorldIdentityGateAuthenticator(string worldHash, IConnectionAuthenticator? inner = null,
        Action<string>? log = null)
    {
        this.worldHash = worldHash ?? throw new ArgumentNullException(nameof(worldHash));
        this.inner = inner ?? new AllowAllAuthenticator();
        this.log = log;
    }

    /// <inheritdoc/>
    public bool TryAuthenticate(ReadOnlySpan<byte> token, out string subject, out string rejectReason)
    {
        HandshakeToken.TryUnwrap(token, out string clientWorld, out byte[] innerToken);
        if (!string.Equals(clientWorld, worldHash, StringComparison.Ordinal))
        {
            subject = string.Empty;
            rejectReason = HandshakeToken.WorldMismatchReason(worldHash, clientWorld);
            log?.Invoke($"[world-identity] refused a client on a mismatched world: server={worldHash} " +
                $"client={(clientWorld.Length == 0 ? "(none)" : clientWorld)}.");
            return false;
        }
        return inner.TryAuthenticate(innerToken, out subject, out rejectReason);
    }

    /// <inheritdoc/>
    public string ReadDisplayName(ReadOnlySpan<byte> token)
    {
        HandshakeToken.TryUnwrap(token, out _, out byte[] innerToken);
        return inner is IConnectionDisplayName named ? named.ReadDisplayName(innerToken) : string.Empty;
    }
}

/// <summary>Refuses a banned account. Runs OUTSIDE-IN last, because a ban keys on the VERIFIED subject and only
/// the token check produces one. The predicate is synchronous and called on the host thread, so it must be cheap
/// (an in-memory view over whatever store the head keeps). An empty subject is never ban checked, because an
/// authenticator that admits anonymously produces no account id to key a ban on.
/// <para>This is the AT-THE-DOOR ban path, and it is one of two. It refuses a subject the head ALREADY knows is
/// banned, during authentication, with the <see cref="HandshakeToken.BannedReason"/> wire token
/// (<c>ke:banned</c>), before the peer joins at all, so a client sees a refused connect rather than a kick.
/// <c>KhaozEngine.NetWorld.IBanStore</c> is the other path: a <c>WorldServer</c> consults it at JOIN and kicks
/// with a typed <c>ServerNotice(ServerNoticeKind.Banned)</c>, which is the route a ban applied MID-SESSION takes
/// and the one a game banned-player banner renders. The check here is a <c>Func&lt;string,bool&gt;</c> rather than
/// an <c>IBanStore</c> because <c>IBanStore</c> lives in <c>KhaozEngine.NetWorld</c>, which this package cannot
/// reference. A <c>WorldServer</c> game that wants both wires the SAME store behind both, passing it as
/// <c>banStore:</c> and handing its <c>IsBanned</c> in here, so the two can never disagree about who is banned.</para></summary>
public sealed class BanGateAuthenticator : IConnectionAuthenticator, IConnectionDisplayName
{
    readonly IConnectionAuthenticator inner;
    readonly Func<string, bool> isBanned;
    readonly Action<string>? log;

    /// <summary>Wraps <paramref name="inner"/> with a ban check over the subject it verifies.</summary>
    public BanGateAuthenticator(IConnectionAuthenticator inner, Func<string, bool> isBanned, Action<string>? log = null)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.isBanned = isBanned ?? throw new ArgumentNullException(nameof(isBanned));
        this.log = log;
    }

    /// <inheritdoc/>
    public bool TryAuthenticate(ReadOnlySpan<byte> token, out string subject, out string rejectReason)
    {
        if (!inner.TryAuthenticate(token, out subject, out rejectReason)) return false;
        if (!string.IsNullOrEmpty(subject) && isBanned(subject))
        {
            log?.Invoke($"[ban] refused a connection for banned account '{subject}'.");
            subject = string.Empty;
            rejectReason = HandshakeToken.BannedReason;
            return false;
        }
        return true;
    }

    /// <inheritdoc/>
    public string ReadDisplayName(ReadOnlySpan<byte> token) =>
        inner is IConnectionDisplayName named ? named.ReadDisplayName(token) : string.Empty;
}

/// <summary>
/// The connect-time door: version, then world, then token, then ban. Promoted from Ruinborne (engine-first: two
/// games need the identical gate) and reachable without <c>KhaozEngine.NetWorld</c>, which is what lets a tile
/// server use it.
/// <para>Order is load-bearing. The VERSION gate is outermost, so a skewed client gets the ordinary out-of-date
/// refusal and, having sent no world layer, never reaches the world check. The WORLD check sits just inside it.
/// The real token auth is next, reached only once version and world both match. The BAN check is last, because it
/// needs the subject the token produced.</para>
/// </summary>
public static class ConnectionGate
{
    /// <summary>Composes the four-layer door around <paramref name="tokenAuth"/>. The version rule is EXACT
    /// equality with <paramref name="protocolVersion"/>: a head that wants a range or a compatibility window
    /// composes <see cref="VersionGateAuthenticator"/> itself with its own rule and nests the rest by hand.</summary>
    public static IConnectionAuthenticator Wrap(IConnectionAuthenticator tokenAuth, string protocolVersion,
        string worldHash, Action<string>? log = null, Func<string, bool>? isBanned = null)
    {
        ArgumentNullException.ThrowIfNull(tokenAuth);
        ArgumentNullException.ThrowIfNull(protocolVersion);
        ArgumentNullException.ThrowIfNull(worldHash);
        log?.Invoke($"World identity: {worldHash}.");
        IConnectionAuthenticator auth = isBanned is null ? tokenAuth : new BanGateAuthenticator(tokenAuth, isBanned, log);
        IConnectionAuthenticator world = new WorldIdentityGateAuthenticator(worldHash, auth, log);
        return new VersionGateAuthenticator(protocolVersion, v => v == protocolVersion, world);
    }

    /// <summary>Builds the token a client presents to a <see cref="Wrap"/>ped door: the version layer wrapping the
    /// world layer wrapping the real auth token.</summary>
    public static byte[] BuildToken(string protocolVersion, string worldHash, byte[]? innerToken) =>
        HandshakeToken.Wrap(protocolVersion, HandshakeToken.Wrap(worldHash, innerToken));
}
