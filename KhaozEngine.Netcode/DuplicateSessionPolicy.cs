namespace KhaozEngine.Netcode;

/// <summary>
/// What a <see cref="NetServer"/> does when a Hello authenticates to a subject that ALREADY holds a live slot: two
/// clients presenting one account's connect token. One account is one identity everywhere above this layer (a
/// persistence record is keyed by it, and one record cannot represent two live players), so the join gate answers
/// the question once for every head rather than leaving each of them to notice.
/// <para>A connection with an EMPTY subject (a tokenless/guest connect) is never deduped under any policy: it is
/// anonymous, not an account, and two guests are two people.</para>
/// </summary>
public enum DuplicateSessionPolicy
{
    /// <summary>The default: the NEW session wins. The older session is disconnected with
    /// <see cref="SessionRejectReason.SignedInElsewhere"/> and its Left is surfaced BEFORE the newcomer's Joined, so a
    /// host draining in order sees leave-then-join and never two live sessions for one account. This is what a
    /// reconnect over a half-dead link needs: the old connection may be a corpse the transport has not buried yet, and
    /// refusing the newcomer would lock the player out until it times out.</summary>
    KickOlder,

    /// <summary>The existing session keeps the seat and the NEW Hello is refused with
    /// <see cref="SessionRejectReason.AlreadySignedIn"/>. The safer answer for a server with no session-takeover story,
    /// at the cost of the reconnect case above: a player whose link died is locked out until the server notices.</summary>
    RefuseNewer,
}

/// <summary>
/// Engine-authored reject reasons carried in the <see cref="SessionOpcode.Reject"/> body (and on the disconnect that
/// follows it). They are stable wire tokens, not display text: a client matches the token and shows its own localized
/// string, the same way <c>ProtocolHandshake</c>'s version-skew envelope works. <c>WorldClient</c> maps both to a
/// distinct <c>DisconnectReason</c> so a game's reconnect screen can say WHY rather than showing a generic rejection.
/// </summary>
public static class SessionRejectReason
{
    /// <summary>Sent to the session being displaced under <see cref="DuplicateSessionPolicy.KickOlder"/>: this
    /// account just signed in somewhere else and that session took the seat.</summary>
    public const string SignedInElsewhere = "ke:signed-in-elsewhere";

    /// <summary>Sent to the Hello being refused under <see cref="DuplicateSessionPolicy.RefuseNewer"/>: this account
    /// already holds a live session on this server.</summary>
    public const string AlreadySignedIn = "ke:already-signed-in";
}
