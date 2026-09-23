using KhaozEngine.Catalog.Netcode;
using KhaozEngine.Netcode;

namespace KhaozEngine.NetWorld;

/// <summary>
/// What one connect refusal means to a <see cref="WorldClient"/>: the typed <see cref="DisconnectReason"/>, the
/// <see cref="WorldClient.DisconnectReasonDetail"/> string, whether the attempt may be retried, and the parsed detail
/// of the refusals that carry one. The ONE place the client reads a refusal token, each through the parser its
/// producer owns, so a new engine refusal is a new row here rather than a new branch in the session loop.
/// </summary>
internal readonly record struct ConnectRefusal(
    DisconnectReason Reason,
    string Detail,
    ConnectRefusal.RetryRule Retry,
    ContentMismatchDetail WorldMismatch = default,
    ContentVersionMismatchDetail ContentVersionMismatch = default,
    int MinimumClientBuild = 0)
{
    /// <summary>Whether a refused attempt goes back on the reconnect backoff.</summary>
    internal enum RetryRule
    {
        /// <summary>Terminal: the same build keeps getting the same answer.</summary>
        Never,

        /// <summary>Retried on the backoff like a dropped transport.</summary>
        Backoff,

        /// <summary>Retried only when <see cref="WorldClientConfig.RetryOnReject"/> is set.</summary>
        WhenRetryOnReject,
    }

    /// <summary>True when the attempt this refusal ended may be retried.</summary>
    internal bool AllowsReconnect(bool retryOnReject) => Retry switch
    {
        RetryRule.Never => false,
        RetryRule.Backoff => true,
        _ => retryOnReject,
    };

    /// <summary>Classifies <paramref name="reason"/>, the server's reject token.</summary>
    internal static ConnectRefusal Read(string? reason)
    {
        // Version handshake rejected us: terminal (retrying the same build will keep failing).
        if (ProtocolHandshake.TryParseIncompatibleReason(reason, out string requiredVersion))
            return new(DisconnectReason.IncompatibleVersion, requiredVersion, RetryRule.Never);

        // Built against other content than the server: terminal, the same build keeps failing.
        if (ContentMismatchDetail.TryParse(reason, out ContentMismatchDetail world))
            return new(DisconnectReason.ContentMismatch, world.ServerIdentity, RetryRule.Never, WorldMismatch: world);

        // The catalog content door's two refusals. Both are terminal for the same reason as the two above: a
        // retry presents the same build and the same content. The detail stays the WHOLE token, which is what a
        // game parsing it under RejectedToken already reads with ContentRefusal.
        if (ContentVersionMismatchDetail.TryParse(reason, out ContentVersionMismatchDetail content))
            return new(DisconnectReason.ContentVersionMismatch, reason!, RetryRule.Never,
                ContentVersionMismatch: content);
        if (ContentRefusal.TryParseClientTooOld(reason, out int minimumClientBuild))
            return new(DisconnectReason.ContentClientTooOld, reason!, RetryRule.Never,
                MinimumClientBuild: minimumClientBuild);

        // The KICK half of the duplicate-session gate: another client took this account's seat. Terminal, and the
        // one reason here that has to be: retrying would displace the session that just displaced this one, and the
        // two clients would trade the seat forever. The game shows its own localized line and offers a manual
        // sign-in.
        if (reason == SessionRejectReason.SignedInElsewhere)
            return new(DisconnectReason.SignedInElsewhere, string.Empty, RetryRule.Never);

        // The REFUSAL half (a RefuseNewer server). Retried, unlike the kick: a refusal displaces nobody, so there is
        // no seat to trade and no ping-pong to start. What is usually holding the seat is this player's OWN
        // half-dead connection, which the server drops once its transport timeout expires (LiteNetLib leaves
        // DisconnectTimeout at 5 s), and the default backoff spends its first three attempts inside that window, so
        // answering terminally dumped a player to manual sign-in for a one-second blip. From attempt four (7.5 s in)
        // the backoff has outlasted the window and the seat is free. A game that would rather stop asking sets
        // ReconnectBackoff.MaxAttempts, or AutoReconnect false.
        if (reason == SessionRejectReason.AlreadySignedIn)
            return new(DisconnectReason.AlreadySignedIn, string.Empty, RetryRule.Backoff);

        return new(DisconnectReason.RejectedToken, reason ?? string.Empty, RetryRule.WhenRetryOnReject);
    }
}
