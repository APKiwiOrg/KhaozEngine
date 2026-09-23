using System.Collections.Generic;
using KhaozEngine.WorldStore;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The post-door ban half of <see cref="TileWorldServer"/>. The DOOR is the connect gate the constructor wraps around
/// the authenticator (see <c>TileWorldServer.cs</c>), which refuses an account already banned with
/// <see cref="TileServerReason.Banned"/> before any player exists. This is everything after it, reading the same
/// <see cref="TileWorldServerConfig.BanStore"/> and <see cref="TileWorldServerConfig.IsBanned"/>:
/// <list type="bullet">
/// <item>At JOIN, the check <c>WorldServer</c> makes over its <c>banStore:</c>, so a ban that landed between the
/// door and the join is told and dropped rather than seated. Nothing is spawned, so no interest snapshot ever carries
/// the account and <see cref="PlayerJoined"/> is never raised for it.</item>
/// <item>Once per tick over every live session, so a ban recorded in the store while the player is in world ends the
/// session on the next tick however it was recorded: a console writing the store directly and
/// <c>ServerAdmin.BanAsync</c> alike, with no <see cref="Kick(int, string)"/> for the game to pair with it.</item>
/// <item>On an admin kick, which carries <see cref="TileServerReason.Banned"/> in place of its own reason when the
/// account is banned. <c>ServerAdmin.BanAsync</c> stores the ban and then kicks, so its kick reads as the ban it
/// is.</item>
/// </list>
/// Every path sends the one token, the same string the door refuses with, which is the tile protocol's typed ban
/// notice: <c>WorldServer</c> sends <c>ServerNoticeKind.Banned</c>, a tile server sends the token.
/// <para>A TOKENLESS seat (<see cref="PositionHintCache.GuestAccountPrefix"/>) is never checked, for the reason the door
/// never checks an empty subject and <c>ServerAdmin</c> refuses to ban one: the id names a seat the next connection
/// inherits, so a ban on it would land on whoever sits there next.</para>
/// </summary>
public sealed partial class TileWorldServer
{
    readonly List<int> bannedScratch = new();

    bool ChecksBans => config.IsBanned is not null || config.BanStore is not null;

    // Either source refusing refuses, which is how the door composes them too. Both are called on the host thread and
    // nothing is caught: a throw is the game's bug, the rule TileWorldServerConfig.CanRun states.
    bool IsAccountBanned(string accountId)
    {
        if (PositionHintCache.IsGuestAccount(accountId)) return false;
        return (config.IsBanned is { } predicate && predicate(accountId))
            || (config.BanStore is { } store && store.IsBanned(accountId));
    }

    // From OnJoin, ahead of the spawn. True when the connection was refused and must not be seated.
    bool RefuseBannedJoin(int slot, string accountId)
    {
        if (!ChecksBans || !IsAccountBanned(accountId)) return false;
        SendNotice(slot, TileServerReason.Banned);
        net.Disconnect(slot);
        return true;
    }

    // The top of RunOneTick, after the admin commands and before the tick snapshots its player index. A LINGERING body
    // is in the account table too, and is ended with the rest, because a ban is not the kind of leave a combat
    // logout window holds a body for.
    void KickBannedSessions()
    {
        if (!ChecksBans) return;
        bannedScratch.Clear();
        foreach (KeyValuePair<int, string> seat in accountIdBySlot)
            if (IsAccountBanned(seat.Value)) bannedScratch.Add(seat.Key);
        for (int i = 0; i < bannedScratch.Count; i++) Kick(bannedScratch[i], TileServerReason.Banned);
    }
}
