using System;
using KhaozEngine.Netcode;

namespace KhaozEngine.NetWorld;

/// <summary>
/// An <see cref="IConnectionAuthenticator"/> decorator that gates a connection on a protocol/build version
/// BEFORE delegating the real auth check, so a version-skewed client is rejected cleanly at connect instead of
/// being admitted and later crashing on a snapshot it cannot decode. The client carries its version by setting
/// <see cref="WorldClientConfig.ProtocolVersion"/> (which wraps the connect token via
/// <see cref="ProtocolHandshake.WrapToken"/>); this decorator unwraps it, runs the consumer-supplied
/// <c>isCompatible</c> rule, and on mismatch rejects with <see cref="ProtocolHandshake.IncompatibleReason"/> -
/// surfaced on the client as <see cref="DisconnectReason.IncompatibleVersion"/> with the required version in the
/// detail. A legacy/non-opting client presents an unwrapped token, decoded as version <c>""</c>, so the rule sees
/// an empty string and can reject unknown-version clients. On accept, the unwrapped inner token is delegated to
/// the wrapped inner authenticator unchanged, so subject/display-name resolution is exactly as without the decorator.
/// Compose it like any authenticator: <c>new WorldServer(..., authenticator: new VersionCheckingAuthenticator(...))</c>.
/// <para>Since 17.40.0 the gate itself lives in <see cref="VersionGateAuthenticator"/> and this type forwards to
/// one, so the engine has a single version-gate body rather than two that can drift. Name, surface and behaviour
/// are unchanged, and a consumer that does not reference this package composes the
/// <see cref="VersionGateAuthenticator"/> directly.</para>
/// </summary>
public sealed class VersionCheckingAuthenticator : IConnectionAuthenticator, IConnectionDisplayName
{
    private readonly VersionGateAuthenticator gate;

    /// <param name="serverVersion">The server's protocol version, sent to a rejected client as the required version.</param>
    /// <param name="isCompatible">Consumer rule: given the client's presented version (empty for a legacy/non-opting
    /// client), return true to admit. Typically <c>v =&gt; v == serverVersion</c>, or a range check.</param>
    /// <param name="inner">The real auth gate to delegate to on a compatible version. Defaults to
    /// <see cref="AllowAllAuthenticator"/> (dev/local).</param>
    public VersionCheckingAuthenticator(string serverVersion, Func<string, bool> isCompatible,
        IConnectionAuthenticator? inner = null) =>
        gate = new VersionGateAuthenticator(serverVersion, isCompatible, inner);

    public bool TryAuthenticate(ReadOnlySpan<byte> token, out string subject, out string rejectReason) =>
        gate.TryAuthenticate(token, out subject, out rejectReason);

    public string ReadDisplayName(ReadOnlySpan<byte> token) => gate.ReadDisplayName(token);
}
