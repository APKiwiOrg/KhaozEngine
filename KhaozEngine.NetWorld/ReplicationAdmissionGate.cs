using System;
using KhaozEngine.Netcode;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The outermost connect gate of a server that runs format 2 streams. It wraps the result of
/// <see cref="WireGenerationAuthenticator.Install"/>. While open it forwards every call unchanged, verified claims
/// included. While closed, during a writer restart or after epoch exhaustion, it rejects every join with
/// <see cref="ReplicationFailure.RestartToken"/> without calling the inner authenticator, so a client takes its
/// reconnect backoff path.
/// </summary>
internal sealed class ReplicationAdmissionGate : IConnectionAuthenticator, IConnectionDisplayName,
    IConnectionPersistenceKey
{
    private readonly IConnectionAuthenticator inner;

    /// <param name="inner">The installed wire-generation gate and everything inside it.</param>
    internal ReplicationAdmissionGate(IConnectionAuthenticator inner) =>
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>True while joins reach the inner authenticator. Starts open.</summary>
    internal bool IsOpen { get; private set; } = true;

    /// <summary>Rejects every later join with the restart token.</summary>
    internal void Close() => IsOpen = false;

    /// <summary>Forwards later joins to the inner authenticator again.</summary>
    internal void Open() => IsOpen = true;

    public bool TryAuthenticate(ReadOnlySpan<byte> token, out string subject, out string rejectReason)
    {
        if (!IsOpen)
        {
            subject = string.Empty;
            rejectReason = ReplicationFailure.RestartToken;
            return false;
        }
        return inner.TryAuthenticate(token, out subject, out rejectReason);
    }

    public string ReadDisplayName(ReadOnlySpan<byte> token) =>
        inner is IConnectionDisplayName named ? named.ReadDisplayName(token) : string.Empty;

    public string ReadPersistenceKey(ReadOnlySpan<byte> token) =>
        inner is IConnectionPersistenceKey keyed ? keyed.ReadPersistenceKey(token) : string.Empty;
}
