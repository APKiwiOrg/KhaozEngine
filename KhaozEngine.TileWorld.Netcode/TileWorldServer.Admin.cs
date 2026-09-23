using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using KhaozEngine.Ecs;
using KhaozEngine.NetWorld;
using KhaozEngine.Sharding;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The admin half of <see cref="TileWorldServer"/>: the engine's generic live-admin surface,
/// <see cref="IAdminControllable"/>, which is what lets <c>ServerAdmin</c> and the <c>KhaozEngine.Server.Admin</c>
/// HTTPS endpoint drive a tile world exactly as they drive a <c>WorldServer</c>. The interface is named under
/// <c>KhaozEngine.NetWorld</c> but lives in <c>KhaozEngine.Netcode</c>, so this package still never references
/// <c>NetWorld</c>.
/// <para>ONE THREADING RULE, the interface's own. Every member here may be called from any thread (an HTTP handler,
/// a console), and none of them touches the world. Reads answer from a snapshot published at the end of every tick,
/// so <see cref="ListOnline"/> is at most one tick stale. Writes are queued and applied on the host thread at the top
/// of the next tick, ahead of <see cref="OnBeforeTick"/> and before the tick snapshots its player index, so a kick
/// that lands there is out of the world before anything iterates. A target is resolved when the command is applied
/// rather than when it was queued, because a slot is a seat the next connection recycles.</para>
/// <para>The host-thread members this sits beside are unchanged and are still the right call from game code on the
/// host thread: <see cref="Kick(int, string)"/> and <see cref="BroadcastNotice"/> act at once,
/// <see cref="SetPlayerState"/> writes a whole state.</para>
/// <para>POSITIONS ARE WORLD METRES through <see cref="TileWorldServerConfig.Presenter"/>, because the interface
/// speaks them. A listed position is the player's COMMITTED tile drawn on its centre, which is the rules' answer
/// about where a player is and a whole tile ahead of a body mid glide, and a teleport target is snapped back onto a
/// tile and plane by the same presenter, so a position copied off <see cref="ListOnline"/> teleports onto the tile
/// it was read from.</para>
/// </summary>
public sealed partial class TileWorldServer : IAdminControllable
{
    enum AdminKind : byte { Teleport, Kick, Broadcast }

    readonly record struct AdminCommand(AdminKind Kind, PlayerRef Target, Vector3 Position, string Text);

    readonly ConcurrentQueue<AdminCommand> adminCommands = new();
    // Swapped in whole, never written in place, so a reader on another thread sees last tick's array or this one's
    // and never one being filled. The scratch list is the rebuild buffer and is never handed out.
    volatile OnlinePlayer[] publishedOnline = Array.Empty<OnlinePlayer>();
    readonly List<OnlinePlayer> onlineScratch = new();
    TilePresenter? placeholderPresenter;

    /// <summary>Raised on the host thread as (slot, tile, reason) when a queued <see cref="Teleport"/> is refused and
    /// the player is left where they were. The tile is the one the position snapped to, default when it named no
    /// tile at all. The admin call returned long before this, so this event is where a head logs the refusal or
    /// reports it back to the operator who asked.</summary>
    public event Action<int, TileCoord, TileTeleportRefusal>? TeleportRefused;

    TilePresenter AdminPresenter =>
        config.Presenter ?? (placeholderPresenter ??= new TilePresenter(1f, TileWorldDocument.DefaultPlaneHeight));

    /// <summary>Everyone in world as of the end of the last tick: slot, account id, display name, net id and the
    /// committed tile's centre in world metres. A body LINGERING under
    /// <see cref="TileWorldServerConfig.CombatLogoutTicks"/> is listed, for the reason <see cref="PlayerCount"/>
    /// counts it. <c>Grounded</c> is always true and <c>VerticalVelocity</c> always zero, which is the one honest
    /// answer a tile world has for the two fields a float head uses to report a falling body. Empty until the first
    /// tick has run. Safe from any thread.</summary>
    public IReadOnlyList<OnlinePlayer> ListOnline() => publishedOnline;

    /// <summary>
    /// Queues a TILE move: <paramref name="position"/> is snapped onto a tile and plane through
    /// <see cref="TileWorldServerConfig.Presenter"/>, and on the next tick the player is placed there through
    /// <see cref="SetPlayerState"/> with <c>teleport: true</c>, the server-authoritative move the tile server already
    /// has, so the epoch advances and the client cuts and resyncs rather than gliding across the map. The route, any
    /// pending interaction and any combat lock are dropped with it, because each would walk the player straight back
    /// toward where they were. Facing and mode are kept.
    /// <para>REFUSED rather than placed when the tile is outside the loaded world or blocked whole: the player stays
    /// where they are and <see cref="TeleportRefused"/> is raised with the reason. Refused on the host thread, not
    /// here, because the collision map is the head's and is only safe to read there. An unknown target is ignored,
    /// as on the float heads.</para>
    /// </summary>
    /// <param name="target">The player, by slot or account id.</param>
    /// <param name="position">Where to put them, in world metres.</param>
    /// <exception cref="ArgumentException">A coordinate of <paramref name="position"/> is not finite, which names no
    /// place at all and is refused on the caller's thread.</exception>
    public void Teleport(PlayerRef target, Vector3 position)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
            throw new ArgumentException("A teleport target must be a finite position.", nameof(position));
        adminCommands.Enqueue(new AdminCommand(AdminKind.Teleport, target, position, string.Empty));
    }

    /// <summary>Queues <see cref="Kick(int, string)"/> for the next tick. <paramref name="reason"/> is a REASON
    /// TOKEN, the same contract every notice on this protocol has: it goes on the wire as it is and the client shows
    /// its own localized string for it. An empty reason, or one longer than
    /// <see cref="TileProtocol.MaxNoticeBytes"/> once encoded, goes out as <see cref="TileServerReason.Kicked"/>
    /// instead, because this call cannot fail: <c>ServerAdmin.BanAsync</c> makes it after the ban is already
    /// stored. An unknown target is ignored.</summary>
    /// <param name="target">The player, by slot or account id.</param>
    /// <param name="reason">A <see cref="TileServerReason"/> token, or a game's own.</param>
    public void Kick(PlayerRef target, string reason) =>
        adminCommands.Enqueue(new AdminCommand(AdminKind.Kick, target, default, reason ?? string.Empty));

    /// <summary>Queues <see cref="BroadcastNotice"/> for the next tick. <paramref name="text"/> is a REASON TOKEN,
    /// never a sentence: the server owns no string catalog, so the client renders its own localized line for the
    /// token, and a game that wants operator announcements defines a token per announcement.</summary>
    /// <param name="text">A <see cref="TileServerReason"/> token, or a game's own.</param>
    /// <exception cref="ArgumentException"><paramref name="text"/> is empty, or longer than
    /// <see cref="TileProtocol.MaxNoticeBytes"/> once encoded. Refused here, on the caller's thread, because there is
    /// no token to fall back to and a notice the wire refuses would otherwise throw out of the tick.</exception>
    public void Broadcast(string text)
    {
        if (!FitsNotice(text))
            throw new ArgumentException(
                $"A tile broadcast is a reason token between 1 and {TileProtocol.MaxNoticeBytes} UTF-8 bytes.",
                nameof(text));
        adminCommands.Enqueue(new AdminCommand(AdminKind.Broadcast, default, default, text));
    }

    static bool FitsNotice(string? token) =>
        !string.IsNullOrEmpty(token) && Encoding.UTF8.GetByteCount(token) <= TileProtocol.MaxNoticeBytes;

    // The top of RunOneTick. Everything queued since the last tick lands here, in arrival order, on the host thread.
    void ApplyAdminCommands()
    {
        while (adminCommands.TryDequeue(out AdminCommand command))
        {
            switch (command.Kind)
            {
                case AdminKind.Teleport:
                    ApplyTeleport(command.Target, command.Position);
                    break;
                case AdminKind.Kick:
                {
                    int slot = ResolveSlot(command.Target);
                    if (slot >= 0) Kick(slot, KickToken(command.Text));
                    break;
                }
                case AdminKind.Broadcast:
                    BroadcastNotice(command.Text);
                    break;
            }
        }
    }

    // What an admin kick puts on the wire. The fallback keeps the call infallible, see Kick(PlayerRef).
    static string KickToken(string reason) => FitsNotice(reason) ? reason : TileServerReason.Kicked;

    void ApplyTeleport(in PlayerRef target, Vector3 position)
    {
        int slot = ResolveSlot(target);
        if (slot < 0 || !netIdBySlot.TryGetValue(slot, out long netId)) return;
        if (!host.TryGetOwner(netId, out CellSim cell, out Entity e) || !cell.World.TryGet(e, out TileMoveState live))
            return;
        // Both refusals are the collision map's own answer told apart: Get reads an unloaded region as Blocked, and
        // SetPlayerState would refuse the unloaded region outright, as a throw out of this tick.
        if (!AdminPresenter.TryTileAt(position, config.PlaneCount, out TileCoord tile)
            || !simulator.Map.HasRegion(tile.Region))
        {
            TeleportRefused?.Invoke(slot, tile, TileTeleportRefusal.OutsideWorld);
            return;
        }
        if ((simulator.Map.Get(tile.X, tile.Z, tile.Plane) & TileCollisionFlags.Blocked) != 0)
        {
            TeleportRefused?.Invoke(slot, tile, TileTeleportRefusal.Blocked);
            return;
        }
        // The admin idiom SetPlayerState documents: copy the live state and move the tile. The route is replaced
        // outright (the raw component's copy is never the authoritative one anyway), and both targets go with it:
        // a pending interaction or a combat lock left on the state would walk the player back toward what they were
        // doing, which is not what an operator moving them meant. Dropping the lock here is SetPlayerState's
        // "ending that fight on purpose", so no cannot-reach notice follows it.
        TileMoveState placed = live;
        placed.Tile = tile;
        placed.Route = TileRoute.None;
        placed.InteractTarget = 0;
        placed.InteractDomain = default;
        placed.CombatTarget = 0;
        SetPlayerState(slot, placed, teleport: true);
        actions.Clear(slot);
    }

    int ResolveSlot(in PlayerRef target)
    {
        if (target.IsSlot) return netIdBySlot.ContainsKey(target.SlotValue) ? target.SlotValue : -1;
        if (string.IsNullOrEmpty(target.AccountValue)) return -1;
        foreach (KeyValuePair<int, string> seat in accountIdBySlot)
            if (string.Equals(seat.Value, target.AccountValue, StringComparison.Ordinal)) return seat.Key;
        return -1;
    }

    // The end of RunOneTick, after the serve, so the snapshot is the world the clients were just shown. Rebuilt into
    // the scratch list and published only when it differs from what a reader can already see, so a tick where nobody
    // joined, left or committed a new tile allocates nothing.
    void PublishOnline()
    {
        onlineScratch.Clear();
        foreach (KeyValuePair<int, long> seat in netIdBySlot)
        {
            int slot = seat.Key;
            string account = accountIdBySlot.TryGetValue(slot, out string? id) ? id : string.Empty;
            Vector3 position = Vector3.Zero;
            string name = string.Empty;
            // The raw component rather than TryGetPlayerState: only the tile is read, and it is authoritative on the
            // component, while assembling the route would cost an array per player per tick for nothing.
            if (host.TryGetOwner(seat.Value, out CellSim cell, out Entity e))
            {
                if (cell.World.TryGet(e, out TileMoveState state)) position = AdminPresenter.PoseAt(state.Tile).Position;
                if (cell.World.TryGet(e, out TileIdentity identity)) name = identity.DisplayName ?? string.Empty;
            }
            onlineScratch.Add(new OnlinePlayer(slot, account, name, position, Grounded: true, VerticalVelocity: 0f,
                NetId: seat.Value));
        }
        OnlinePlayer[] current = publishedOnline;
        if (onlineScratch.Count == current.Length)
        {
            bool same = true;
            for (int i = 0; i < current.Length && same; i++) same = onlineScratch[i].Equals(current[i]);
            if (same) return;
        }
        publishedOnline = onlineScratch.ToArray();
    }
}
