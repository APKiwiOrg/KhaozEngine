using KhaozEngine.Diagnostics;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The join-time gate on the RESERVED account-id namespace. <see cref="ResumePositionCache.GuestAccountPrefix"/>
/// is what both server heads derive for a TOKENLESS connection (<c>guest:{slot}</c>), and since #647 it is also
/// what decides whether persistence files a record at all: an account id carrying it gets no load-on-join, no
/// save-on-leave and no periodic pass, and <see cref="ResumePositionCache"/> refuses it outright.
///
/// <para>Nothing used to stop a game minting that prefix as a REAL subject. A game that namespaces its own
/// account ids (a <c>guest:</c> tier bought with a login, say) could hand out a token that verifies fine, reaches
/// persistence as a genuine account id, and reads as tokenless there. That player played a whole session and lost
/// every byte of it on disconnect, silently: no log line, no event, no rejection. Silent data loss is the worst
/// shape a low-severity bug can have, so it is refused instead (#664).</para>
///
/// <para>The join gate is the only place that can still tell the two apart, because the tokenless key is DERIVED
/// after this point while a minted subject is PRESENTED to it. It is the prefix that decides, not the word, so
/// every other subject format keeps working, an account genuinely named <c>guest</c> included.</para>
/// </summary>
internal static class ReservedSubjectGuard
{
    private static readonly ILogger Log = Diagnostics.Log.Get("ReservedSubject");

    /// <summary>True when <paramref name="subject"/> is a verified subject inside the reserved namespace, in which
    /// case the caller must refuse the connection. Logs the reason and the fix, because this is a server
    /// configuration error rather than anything the connecting player did.</summary>
    internal static bool IsReserved(string subject, int slot)
    {
        if (!ResumePositionCache.IsGuestAccount(subject)) return false;
        Log.Error(
            $"Refused the join on slot {slot}: the verified subject '{subject}' is inside the reserved " +
            $"'{ResumePositionCache.GuestAccountPrefix}' namespace, which names a tokenless connection's seat and " +
            "is never persisted. Mint that account under a different prefix.");
        return true;
    }
}
