using System;
using System.Globalization;
using KhaozEngine.Catalog;
using KhaozEngine.Netcode;

namespace KhaozEngine.Catalog.Netcode;

/// <summary>
/// Refuses a peer holding a different CONTENT version, spec 8.5, modelled on
/// <c>WorldIdentityGateAuthenticator</c> in every particular: unwrap ONE layer, compare ORDINAL, refuse with
/// a stable wire token carrying BOTH sides, otherwise delegate inward.
/// <para>
/// Layer ORDER in the nest, outermost first: protocol version, world, CONTENT, the game's token auth, the
/// ban check (contracts 7.5). Content sits inside world and outside auth because a disagreement about
/// content is a cheaper and more specific refusal than a failed credential, and the ban check stays
/// innermost because it needs the subject the token produced.
/// </para>
/// <para>
/// <b>A client that is behind is REFUSED, not admitted read-only while it fetches.</b> The refusal carries
/// the server's version number AND its client-manifest hash, which is everything the fetch loop of spec 8.7
/// needs: fetch that manifest, fetch the chunks it names that the cache lacks, verify each, reconnect. It
/// carries no URL, deliberately, because a URL in a refusal token is a redirect an unauthenticated party
/// controls (spec 13.4): the client is configured with its pack base address the way it is configured with
/// its server address.
/// </para>
/// </summary>
public sealed class ContentIdentityGateAuthenticator : IConnectionAuthenticator, IConnectionDisplayName,
    IConnectionPersistenceKey
{
    readonly IConnectionAuthenticator inner;
    readonly Action<string>? log;

    /// <summary>Gates on <paramref name="serverIdentity"/> and delegates to <paramref name="inner"/> on a match.</summary>
    /// <param name="serverIdentity">
    /// The version the server is serving, as the CLIENT sees it: the version number and the CLIENT manifest
    /// hash, never the server manifest hash, which no client ever holds.
    /// </param>
    /// <param name="inner">The gate inside this one, or null to admit everything that matches.</param>
    /// <param name="minimumClientBuild">
    /// The version's <c>MinimumClientBuild</c> (contracts 7.4), or 0 for no minimum. A client that STATES a
    /// lower build is refused with <see cref="ContentRefusal.ClientTooOld"/> ahead of the identity check, so
    /// it can tell the player to update rather than showing a mismatch a refetch will not clear. A client
    /// that states no build is making no statement and is judged on its content identity alone: the client
    /// half of this check is the fetch loop's, against the manifest it fetched (spec 8.7 step 3), and a
    /// door check that refused every client whose head sends no build ordinal would be a lockout no update
    /// can clear.
    /// </param>
    /// <param name="log">The operator's line, which carries both sides of a refusal.</param>
    /// <exception cref="ArgumentException"><paramref name="serverIdentity"/> carries no content address.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minimumClientBuild"/> is negative.</exception>
    public ContentIdentityGateAuthenticator(
        ContentVersionIdentity serverIdentity,
        IConnectionAuthenticator? inner = null,
        int minimumClientBuild = 0,
        Action<string>? log = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumClientBuild);
        if (!ContentIdentityLayer.IsContentAddress(serverIdentity.ManifestHash))
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"'{serverIdentity.ManifestHash}' is not a manifest hash, which is 64 lower hex characters. A door that gates on anything else refuses every client it cannot compare with."),
                nameof(serverIdentity));
        }

        ServerIdentity = serverIdentity;
        MinimumClientBuild = minimumClientBuild;
        this.inner = inner ?? new AllowAllAuthenticator();
        this.log = log;
    }

    /// <summary>The content version the server serves, which every client has to present exactly.</summary>
    public ContentVersionIdentity ServerIdentity { get; }

    /// <summary>The build ordinal a client that states one has to reach, or 0 for no minimum.</summary>
    public int MinimumClientBuild { get; }

    /// <inheritdoc/>
    public bool TryAuthenticate(ReadOnlySpan<byte> token, out string subject, out string rejectReason)
    {
        bool read = ContentIdentityLayer.TryUnwrap(
            token, out ContentVersionIdentity client, out int? clientBuild, out byte[] innerToken);

        if (!read)
        {
            // No layer, or one that is not a version number and a content address. Both are the same
            // statement (none), and echoing an unparsed value back would be how a colon reaches the
            // client's own parser.
            return Refuse(ContentRefusal.Mismatch(ServerIdentity, null), "(none)", out subject, out rejectReason);
        }

        if (MinimumClientBuild > 0 && clientBuild is int build && build < MinimumClientBuild)
        {
            subject = string.Empty;
            rejectReason = ContentRefusal.ClientTooOld(MinimumClientBuild);
            log?.Invoke(FormattableString.Invariant(
                $"[content-identity] refused a client on build {build}, below the version's minimum {MinimumClientBuild}."));
            return false;
        }

        if (client.Number != ServerIdentity.Number
            || !string.Equals(client.ManifestHash, ServerIdentity.ManifestHash, StringComparison.Ordinal))
        {
            return Refuse(
                ContentRefusal.Mismatch(ServerIdentity, client),
                FormattableString.Invariant($"{client.Number}|{client.ManifestHash}"),
                out subject,
                out rejectReason);
        }

        return inner.TryAuthenticate(innerToken, out subject, out rejectReason);
    }

    /// <inheritdoc/>
    public string ReadDisplayName(ReadOnlySpan<byte> token)
    {
        ContentIdentityLayer.TryUnwrap(token, out _, out _, out byte[] innerToken);
        return inner is IConnectionDisplayName named ? named.ReadDisplayName(innerToken) : string.Empty;
    }

    /// <inheritdoc/>
    public string ReadPersistenceKey(ReadOnlySpan<byte> token)
    {
        ContentIdentityLayer.TryUnwrap(token, out _, out _, out byte[] innerToken);
        return inner is IConnectionPersistenceKey keyed ? keyed.ReadPersistenceKey(innerToken) : string.Empty;
    }

    bool Refuse(string reason, string clientSide, out string subject, out string rejectReason)
    {
        subject = string.Empty;
        rejectReason = reason;
        log?.Invoke(FormattableString.Invariant(
            $"[content-identity] refused a client on a mismatched content version: server={ServerIdentity.Number}|{ServerIdentity.ManifestHash} client={clientSide}."));
        return false;
    }
}
