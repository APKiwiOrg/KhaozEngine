using System;
using System.Text;
using KhaozEngine.NetWorld;

namespace KhaozEngine.Netcode;

/// <summary>Refuses a peer on a protocol-version mismatch before anything else runs.</summary>
public sealed class VersionGateAuthenticator : IConnectionAuthenticator, IConnectionDisplayName,
    IConnectionPersistenceKey
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

    /// <inheritdoc/>
    public string ReadPersistenceKey(ReadOnlySpan<byte> token)
    {
        HandshakeToken.TryUnwrap(token, out _, out byte[] innerToken);
        return inner is IConnectionPersistenceKey keyed ? keyed.ReadPersistenceKey(innerToken) : string.Empty;
    }
}

/// <summary>Refuses a peer whose identity layer differs from the server's, so a client built against other content
/// (a different world, map or data set, whatever opaque identity the head computes) can never join and render its
/// own copy while the server simulates another. Distinct from the version gate on purpose: a patch that leaves the
/// content alone still interoperates.
/// <para>The refusal is the stable wire token <see cref="HandshakeToken.WorldMismatchReason"/>,
/// <c>ke:world-mismatch:&lt;server&gt;|&lt;client&gt;</c>, which carries both identities because a client matches
/// it. The operator log line carries the server's identity and whether the client sent one, never the client's
/// label: that is whatever bytes an unauthenticated peer put in the layer.</para></summary>
public sealed class WorldIdentityGateAuthenticator : IConnectionAuthenticator, IConnectionDisplayName,
    IConnectionPersistenceKey
{
    readonly string identity;
    readonly IConnectionAuthenticator inner;
    readonly Action<string>? log;

    /// <summary>Gates on <paramref name="worldHash"/> and delegates to <paramref name="inner"/> on a match.</summary>
    /// <param name="worldHash">The identity every client has to present exactly. Refused at construction when it is
    /// empty, carries a <c>|</c> or exceeds <see cref="HandshakeToken.MaxLabelBytes"/> UTF-8 bytes, the same rule a
    /// client's identity is held to: an empty identity reads the same as no layer at all, so it would admit a client
    /// that sent none, a pipe would split the refusal token in the wrong place, and an identity no layer can carry
    /// refuses every client.</param>
    /// <param name="inner">The gate inside this one, or null to admit everything that matches.</param>
    /// <param name="log">The operator's line on each refusal.</param>
    /// <exception cref="ArgumentNullException"><paramref name="worldHash"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="worldHash"/> is empty, contains <c>|</c>, or is over the
    /// label cap.</exception>
    public WorldIdentityGateAuthenticator(string worldHash, IConnectionAuthenticator? inner = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(worldHash);
        if (worldHash.Length == 0 || worldHash.Contains('|'))
            throw new ArgumentException("A server identity is non-empty and never contains '|'.", nameof(worldHash));
        if (Encoding.UTF8.GetByteCount(worldHash) > HandshakeToken.MaxLabelBytes)
            throw new ArgumentException(
                $"A server identity fits {HandshakeToken.MaxLabelBytes} UTF-8 bytes, the most a layer label carries.",
                nameof(worldHash));
        identity = worldHash;
        this.inner = inner ?? new AllowAllAuthenticator();
        this.log = log;
    }

    /// <inheritdoc/>
    public bool TryAuthenticate(ReadOnlySpan<byte> token, out string subject, out string rejectReason)
    {
        HandshakeToken.TryUnwrap(token, out string clientIdentity, out byte[] innerToken);
        if (!string.Equals(clientIdentity, identity, StringComparison.Ordinal))
        {
            subject = string.Empty;
            rejectReason = HandshakeToken.WorldMismatchReason(identity, clientIdentity);
            log?.Invoke($"[identity-gate] refused a client whose identity layer does not match this server's " +
                $"{identity}: the client sent {(clientIdentity.Length == 0 ? "none" : "a different one")}.");
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

    /// <inheritdoc/>
    public string ReadPersistenceKey(ReadOnlySpan<byte> token)
    {
        HandshakeToken.TryUnwrap(token, out _, out byte[] innerToken);
        return inner is IConnectionPersistenceKey keyed ? keyed.ReadPersistenceKey(innerToken) : string.Empty;
    }
}

/// <summary>Refuses a banned account. Runs OUTSIDE-IN last, because a ban keys on the VERIFIED subject and only
/// the token check produces one. The check is synchronous and called on the host thread, so it must be cheap (an
/// in-memory view over whatever store the head keeps). An empty subject is never ban checked, because an
/// authenticator that admits anonymously produces no account id to key a ban on.
/// <para>This is the AT-THE-DOOR ban path. It refuses a subject already banned, during authentication, with the
/// <see cref="HandshakeToken.BannedReason"/> wire token (<c>ke:banned</c>), before the peer joins at all, so a
/// client sees a refused connect rather than a kick. The other path is the JOIN check a <c>WorldServer</c> or
/// <c>ShardedWorldServer</c> runs over its <c>banStore:</c>, which kicks with a typed
/// <c>ServerNotice(ServerNoticeKind.Banned)</c>, and that a <c>TileWorldServer</c> runs at the join and every tick
/// over its <c>TileWorldServerConfig.BanStore</c>, which kicks with the <c>ke:banned</c> notice token. Both read the one <see cref="IBanStore"/> seam, so hand the SAME
/// store to this gate and to <c>banStore:</c> and the two can never disagree about who is banned.</para>
/// <para>The <c>Func&lt;string,bool&gt;</c> constructor predates the store one and is kept for a head whose ban
/// list is not an <see cref="IBanStore"/>. Both constructors refuse identically.</para></summary>
public sealed class BanGateAuthenticator : IConnectionAuthenticator, IConnectionDisplayName,
    IConnectionPersistenceKey
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

    /// <summary>Wraps <paramref name="inner"/> with a check of <paramref name="banStore"/> over the subject it
    /// verifies. The store is read live on every connect, so a ban recorded after construction refuses the next
    /// attempt.</summary>
    public BanGateAuthenticator(IConnectionAuthenticator inner, IBanStore banStore, Action<string>? log = null)
        : this(inner, (banStore ?? throw new ArgumentNullException(nameof(banStore))).IsBanned, log)
    {
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

    /// <inheritdoc/>
    public string ReadPersistenceKey(ReadOnlySpan<byte> token) =>
        inner is IConnectionPersistenceKey keyed ? keyed.ReadPersistenceKey(token) : string.Empty;
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
    /// composes <see cref="VersionGateAuthenticator"/> itself with its own rule and nests the rest by hand.
    /// <paramref name="worldHash"/> is held to <see cref="WorldIdentityGateAuthenticator"/>'s rule, so an empty,
    /// piped or over-long one throws <see cref="ArgumentException"/> here, at boot.</summary>
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
