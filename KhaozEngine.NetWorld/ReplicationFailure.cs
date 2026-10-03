using System;

namespace KhaozEngine.NetWorld;

/// <summary>
/// A typed format 2 replication failure: the <see cref="DisconnectReason"/> the session ends with and its detail. The
/// server sends the matching stable token as its disconnect or reject reason, and <see cref="ConnectRefusal.Read"/>
/// maps each token back to its reason. Policy refusal, recovery failure and capacity failure are terminal. A restart
/// is retried on the consumer's reconnect backoff.
/// </summary>
/// <param name="Reason">One of the four replication disconnect reasons.</param>
/// <param name="Detail">Diagnostic detail for logs. Never parsed.</param>
internal readonly record struct ReplicationFailure(DisconnectReason Reason, string Detail)
{
    /// <summary>The client refused an offer outside its configured limits.</summary>
    internal const string PolicyRefusedToken = "ke:replication-policy-refused";

    /// <summary>Negotiation or repair did not complete before its deadline.</summary>
    internal const string RecoveryFailedToken = "ke:replication-recovery-failed";

    /// <summary>A complete projection cannot fit the negotiated limits, so repair cannot fix it.</summary>
    internal const string CapacityExceededToken = "ke:replication-capacity-exceeded";

    /// <summary>The server ended the stream and admits a fresh connection after a backoff.</summary>
    internal const string RestartToken = "ke:replication-restart";

    /// <summary>True for the three failures a reconnect cannot fix.</summary>
    internal bool IsTerminal => Reason != DisconnectReason.ReplicationRestart;

    /// <summary>The stable wire token for <see cref="Reason"/>.</summary>
    internal string Token => TokenFor(Reason);

    /// <summary>The stable wire token for one of the four replication reasons.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> is not a replication reason.</exception>
    internal static string TokenFor(DisconnectReason reason) => reason switch
    {
        DisconnectReason.ReplicationPolicyRefused => PolicyRefusedToken,
        DisconnectReason.ReplicationRecoveryFailed => RecoveryFailedToken,
        DisconnectReason.ReplicationCapacityExceeded => CapacityExceededToken,
        DisconnectReason.ReplicationRestart => RestartToken,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Not a replication disconnect reason."),
    };
}
