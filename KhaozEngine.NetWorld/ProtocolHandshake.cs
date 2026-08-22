using System;
using KhaozEngine.Netcode;

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
/// The layer bytes themselves live in <see cref="HandshakeToken"/> since 17.39.0, so the engine has one
/// implementation of them and a tile server can reach the codec without referencing this package.
/// </summary>
public static class ProtocolHandshake
{
    /// <summary>Upper bound on a protocol-version string's UTF-8 encoding, in bytes (a single length byte).</summary>
    public const int MaxVersionBytes = HandshakeToken.MaxLabelBytes;

    // Label prefix for the always-present ENGINE wire-generation layer (the OUTER handshake layer). Distinct from any
    // consumer version: BuildClientToken stamps WireGenerationLabel(MoveProtocol.WireProtocolVersion) on EVERY connect
    // token (even with no consumer version), and the server's always-on WireGenerationAuthenticator requires it. A peer
    // that predates this (a pre-10.2.0 / 9.x client presenting a raw or consumer-only token) unwraps to a non-matching
    // label and is rejected as IncompatibleVersion instead of admitted and left to misparse the wire.
    private const string WirePrefix = "ke-wire:";

    /// <summary>The connect-token label carrying the engine wire generation (the OUTER handshake layer that
    /// <see cref="BuildClientToken"/> always applies). The client stamps its build's generation
    /// (<see cref="MoveProtocol.WireProtocolVersion"/>); the server's <see cref="WireGenerationAuthenticator"/> requires
    /// its own generation's label. Independent of the opt-in consumer <see cref="WorldClientConfig.ProtocolVersion"/>.</summary>
    public static string WireGenerationLabel(int wireGeneration) =>
        WirePrefix + wireGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Builds the full connect token an engine client sends: the always-present engine wire-generation layer
    /// (<paramref name="wireGeneration"/>) wrapping the OPTIONAL consumer-version layer (<paramref name="consumerVersion"/>,
    /// null = none) wrapping the inner auth <paramref name="innerToken"/>. The wire layer is unconditional so a server can
    /// reject a wire-generation mismatch even when the consumer opts out of a game version; the consumer layer, when
    /// present, is checked on top by a <see cref="VersionCheckingAuthenticator"/>. Mirrors the server-side composition
    /// <c>WireGenerationAuthenticator(gen, VersionCheckingAuthenticator(consumerVersion, rule, inner))</c>.</summary>
    public static byte[] BuildClientToken(int wireGeneration, string? consumerVersion, byte[]? innerToken)
    {
        byte[]? consumerLayer = consumerVersion is null ? innerToken : WrapToken(consumerVersion, innerToken);
        return WrapToken(WireGenerationLabel(wireGeneration), consumerLayer);
    }

    /// <summary>Builds a version-wrapped connect token: <c>[magic][verLen:byte][version utf8][inner token]</c>.
    /// <paramref name="innerToken"/> is the auth token the inner <see cref="Netcode.IConnectionAuthenticator"/>
    /// expects (may be null/empty for an anonymous connection).</summary>
    public static byte[] WrapToken(string protocolVersion, byte[]? innerToken) =>
        HandshakeToken.Wrap(protocolVersion, innerToken);

    /// <summary>Splits a connect token produced by <see cref="WrapToken"/> into its version and inner token. An
    /// unwrapped token (no magic, or too short / a corrupt length) yields version <c>""</c> and the whole token as
    /// <paramref name="innerToken"/>, and returns <c>false</c> - so a legacy/non-opting client is handled as
    /// "version unknown" rather than rejected at decode. Never throws.</summary>
    public static bool TryUnwrapToken(ReadOnlySpan<byte> token, out string protocolVersion, out byte[] innerToken) =>
        HandshakeToken.TryUnwrap(token, out protocolVersion, out innerToken);

    /// <summary>The structured reject reason for a version mismatch, carrying the server's required version.</summary>
    public static string IncompatibleReason(string requiredVersion) =>
        HandshakeToken.IncompatibleVersionReason(requiredVersion);

    /// <summary>Recognizes a reason produced by <see cref="IncompatibleReason"/>, extracting the required version.
    /// Lets <see cref="WorldClient"/> distinguish a version-mismatch rejection from a plain token rejection.</summary>
    public static bool TryParseIncompatibleReason(string? reason, out string requiredVersion) =>
        HandshakeToken.TryParseIncompatibleVersion(reason, out requiredVersion);
}
