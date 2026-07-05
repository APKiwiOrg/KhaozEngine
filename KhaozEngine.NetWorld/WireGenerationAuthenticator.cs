using System;
using KhaozEngine.Netcode;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The engine's ALWAYS-ON connect-time gate on the wire-format generation (<see cref="MoveProtocol.WireProtocolVersion"/>),
/// installed automatically by <see cref="WorldServer"/> / <see cref="ShardedWorldServer"/> around whatever authenticator
/// the consumer supplies. Every engine client folds the wire generation into its Hello unconditionally (see
/// <see cref="ProtocolHandshake.BuildClientToken"/>), even when it opts out of a consumer game version; this gate peels
/// that OUTER layer and rejects a mismatch - or a peer that presents none (a pre-10.2.0 / 9.x client sending a raw or
/// consumer-only token) - cleanly as <see cref="DisconnectReason.IncompatibleVersion"/>, so a wire-skewed client is
/// turned away at connect instead of admitted and left to misparse a snapshot frame (e.g. reading the 10.0.0 12-byte
/// per-client header as 8 bytes and decoding an empty world). This replaces the pre-10.2.0 posture where the wire gate
/// was opt-in (a consumer had to fold <c>;wire{N}</c> into its <see cref="WorldClientConfig.ProtocolVersion"/>), so an
/// unconfigured pairing silently misparsed. The consumer's own <see cref="VersionCheckingAuthenticator"/> - if any -
/// sits INSIDE this gate and checks the game version on top; the inner (stripped) token is delegated unchanged, so
/// subject / display-name resolution is exactly as without it. Pass a <see cref="WireGenerationAuthenticator"/>
/// explicitly to override the expected generation (tests use this to simulate a wire-skewed peer); the servers then
/// respect it as-is rather than double-wrapping.
/// </summary>
public sealed class WireGenerationAuthenticator : IConnectionAuthenticator, IConnectionDisplayName
{
    private readonly IConnectionAuthenticator inner;
    private readonly string requiredLabel;

    /// <param name="expectedWireGeneration">The wire generation this server requires - normally
    /// <see cref="MoveProtocol.WireProtocolVersion"/>. A client presenting any other generation, or none, is rejected.</param>
    /// <param name="inner">The real auth gate to delegate to on a matching generation. Defaults to
    /// <see cref="AllowAllAuthenticator"/> (dev/local). Compose a <see cref="VersionCheckingAuthenticator"/> here to also
    /// gate a consumer game version on top of the wire generation.</param>
    public WireGenerationAuthenticator(int expectedWireGeneration, IConnectionAuthenticator? inner = null)
    {
        ExpectedWireGeneration = expectedWireGeneration;
        requiredLabel = ProtocolHandshake.WireGenerationLabel(expectedWireGeneration);
        this.inner = inner ?? new AllowAllAuthenticator();
    }

    /// <summary>The wire generation this gate admits (rejecting every other, and clients that present none).</summary>
    public int ExpectedWireGeneration { get; }

    /// <summary>Wraps <paramref name="inner"/> with the always-on wire-generation gate at this build's
    /// <see cref="MoveProtocol.WireProtocolVersion"/>, unless it already IS a <see cref="WireGenerationAuthenticator"/>
    /// (then it is respected as-is, so a test or consumer can install a non-default expected generation without being
    /// double-wrapped). <see cref="WorldServer"/> and <see cref="ShardedWorldServer"/> call this on their authenticator.</summary>
    public static IConnectionAuthenticator Install(IConnectionAuthenticator? inner) =>
        inner is WireGenerationAuthenticator existing
            ? existing
            : new WireGenerationAuthenticator(MoveProtocol.WireProtocolVersion, inner ?? new AllowAllAuthenticator());

    public bool TryAuthenticate(ReadOnlySpan<byte> token, out string subject, out string rejectReason)
    {
        ProtocolHandshake.TryUnwrapToken(token, out string label, out byte[] innerToken);
        if (!string.Equals(label, requiredLabel, StringComparison.Ordinal))
        {
            // A raw/legacy token unwraps to label "" (no wire layer); a peer on another generation unwraps to a
            // different label. Either way: clean IncompatibleVersion, carrying this server's required wire label.
            subject = string.Empty;
            rejectReason = ProtocolHandshake.IncompatibleReason(requiredLabel);
            return false;
        }
        return inner.TryAuthenticate(innerToken, out subject, out rejectReason);
    }

    public string ReadDisplayName(ReadOnlySpan<byte> token)
    {
        // Peel the wire layer, then delegate to the inner authenticator's own display-name resolution (which peels any
        // consumer-version layer in turn). Only called after TryAuthenticate accepted the same token.
        ProtocolHandshake.TryUnwrapToken(token, out _, out byte[] innerToken);
        return inner is IConnectionDisplayName named ? named.ReadDisplayName(innerToken) : string.Empty;
    }
}
