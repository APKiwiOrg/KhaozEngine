using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Sharding;
using KhaozEngine.WorldStore;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>Handles one opaque game message off the wire, as (slot, kind, payload). The engine never looks inside
/// the payload, so a game defines both the kind numbers and the encoding.</summary>
/// <param name="slot">The connection slot the message arrived on.</param>
/// <param name="kind">The game's own message kind.</param>
/// <param name="payload">The message body. A SLICE of the inbound buffer, valid only for the duration of the call:
/// copy it if the handler keeps it past its return.</param>
public delegate void TileGameMessageHandler(int slot, ushort kind, ReadOnlySpan<byte> payload);

/// <summary>
/// The session half of <see cref="TileWorldServer"/>: connections arriving and leaving, what an inbound frame is
/// allowed to do, and the operator controls over a live session. See the other two partials for construction and
/// the player index (<c>TileWorldServer.cs</c>) and for the tick order and the serve
/// (<c>TileWorldServer.Tick.cs</c>).
/// <para>The split is the natural seam: everything here runs on the POLL, driven by the transport, and everything
/// in the tick half runs on the CLOCK. Nothing here steps the world, and nothing there touches a connection.</para>
/// <para>This is also where the server becomes the PERSISTENCE HOST. <see cref="IPersistenceHost{TState}"/> is a
/// join, a leave, a state accessor and a spawn seed, which is exactly the set of things a session lifecycle owns,
/// so <see cref="TileWorldPersistence"/> drives the server through the members already declared here and needs no
/// adapter of its own.</para>
/// </summary>
public sealed partial class TileWorldServer : IPersistenceHost<TileMoveState>
{
    PositionHintProvider? hintProvider;

    // The drain countdown, in seconds of wall clock, and the token it was started with. Negative means no drain
    // has been asked for, which is what keeps IsDraining answerable from the one field: a grace of zero is a
    // legitimate drain that completes on the next Tick, and a zero-initialised field could not tell the two apart.
    float drainRemaining = -1f;
    string drainReason = string.Empty;
    bool drainClosed;
    // The slots to close when a drain completes. Snapshotted, because closing one removes it from the player index
    // and a dictionary cannot be enumerated while it changes.
    readonly List<int> drainScratch = new();

    /// <summary>Raised as (slot, accountId) once a connection has a player entity, which is the point a game may
    /// start reading and writing that player. Raised from <see cref="SpawnPlayer"/>, so it fires for a headless
    /// spawn as well as for a real join.</summary>
    public event Action<int, string>? PlayerJoined;

    /// <summary>Raised as (slot, accountId, final state) just BEFORE a player is despawned, which is the last
    /// moment their authoritative state exists. A persistence layer saves here, so anything a game wants stored
    /// has to be readable at this point.</summary>
    public event Action<int, string, TileMoveState>? PlayerLeaving;

    /// <summary>Raised for an opaque game message off the wire. The engine routes the envelope and knows nothing
    /// about the body, so this is where a game's own protocol takes over.</summary>
    public event TileGameMessageHandler? OnGameMessage;

    /// <summary>Raised as (slot, target) when a pending interaction is refused because the player could not reach
    /// the thing they clicked. The matching <see cref="TileServerReason.CannotReach"/> notice goes to that client
    /// on the same tick, so this event is for the SERVER's own reaction (a log line, a game-side cleanup) rather
    /// than for telling the player.</summary>
    public event Action<int, long>? OnCannotReach;

    /// <summary>Inbound frames refused: malformed, over the rate budget, or for a slot with no player. A healthy
    /// server climbs this slowly or not at all, so it is a flood and version-skew signal rather than a
    /// statistic.</summary>
    public long DroppedCommandCount { get; private set; }

    /// <summary>The slots with a live player, which is what a persistence pass iterates. The player index's own
    /// key collection, so it reflects a join or a leave immediately and must not be enumerated across one.</summary>
    public IReadOnlyCollection<int> JoinedSlots => netIdBySlot.Keys;

    /// <summary>True from the moment <see cref="BeginDrain"/> runs until the server is disposed. A drain is
    /// one way: there is no resume, because the announcement has already gone out to every client.</summary>
    public bool IsDraining => drainRemaining >= 0f;

    /// <summary>True once a drain's grace has elapsed, so the host may flush persistence and exit. Polled by the
    /// head's own loop rather than raised as an event, because what happens next (a save pass, a process exit) is
    /// the head's decision and its timing is the head's to own.</summary>
    public bool IsDrainComplete => IsDraining && drainRemaining <= 0f;

    /// <summary>
    /// Pumps the transport and turns session events into joins, leaves and buffered commands. Call once per host
    /// frame, BEFORE <see cref="Tick"/>: a command that arrived this frame is then routed by the very next tick
    /// rather than waiting a whole one, which is the difference between a click landing in 250 ms and in 500 ms.
    /// <para>Draining to empty is a contract rather than a courtesy. <c>NetServer</c> caps its undrained inbox and
    /// drops the OLDEST event when a host stops draining, so a poll that left events behind would lose joins.</para>
    /// </summary>
    public void Poll()
    {
        net.Poll();
        // One budget top-up per poll rather than per tick, because this is the cadence inbound frames actually
        // arrive on: a host polling faster than it ticks would otherwise throttle a client that sent nothing
        // unusual. The budget itself is per tick (see SpawnPlayer), so the two only differ on a head whose frame
        // rate is above its tick rate, which is every real one.
        foreach (RateLimiter limiter in rateBySlot.Values) limiter.Refill();
        while (net.TryDequeueEvent(out ServerSessionEvent ev))
        {
            switch (ev.Kind)
            {
                case ServerSessionEventKind.Joined:
                    OnJoin(ev.Slot, ev.Subject, ev.DisplayName);
                    break;
                case ServerSessionEventKind.Left:
                    OnLeave(ev.Slot);
                    break;
                case ServerSessionEventKind.Data:
                    HandleData(ev.Slot, ev.Data);
                    break;
            }
        }
    }

    // A verified subject is the account id persistence keys a record on, so it is passed through untouched. A
    // TOKENLESS connection has none, and is given a SEAT-derived guest id under the shared guest prefix, which is
    // the one the persistence core refuses to file a record under: a seat is inherited by the next connection, so
    // a record stored against one would load onto a stranger.
    void OnJoin(int slot, string subject, string displayName)
    {
        string accountId = string.IsNullOrEmpty(subject)
            ? $"{PositionHintCache.GuestAccountPrefix}{slot}"
            : subject;
        SpawnPlayer(slot, accountId, displayName);
        // A connection that arrives DURING a drain is admitted and told, rather than refused: the grace is what a
        // player needs to finish what they are doing, and a rejoin inside it (a reconnect after a drop) is exactly
        // the case that needs the announcement it missed. The broadcast in BeginDrain went out before this session
        // existed, so without this the one client that cannot see the shutdown coming is the one that just
        // reconnected into it.
        if (IsDraining) SendNotice(slot, drainReason);
    }

    // Everything an inbound frame is allowed to do, and the three ways it is refused. The order is deliberate: an
    // unknown slot costs a dictionary probe, the rate budget costs a subtraction, and only a frame that passed both
    // is decoded. A decoder is the most expensive of the three and the only one an attacker chooses the size of.
    void HandleData(int slot, byte[] data)
    {
        // A frame for a slot with no player is not necessarily hostile: a command sent one frame before a
        // disconnect arrives after it. It is still counted, because a sustained stream of them is a client that
        // never noticed it was dropped.
        if (!netIdBySlot.ContainsKey(slot)) { DroppedCommandCount++; return; }
        if (rateBySlot.TryGetValue(slot, out RateLimiter? limiter) && !limiter.TryConsume())
        {
            DroppedCommandCount++;
            return;
        }
        switch (TileProtocol.ClientFrameTag(data))
        {
            case TileProtocol.ClientFrameCommand:
                // Decoded against the server's OWN plane count, so a goal naming a plane this world does not have
                // is refused on the wire rather than inside a tick.
                if (TileProtocol.TryDecodeCommand(data, config.PlaneCount, out int seq, out TileCommand cmd))
                    commands.Store(slot, seq, cmd);
                else DroppedCommandCount++;
                return;
            case TileProtocol.ClientFrameGameMessage:
                if (TileProtocol.TryDecodeGameMessage(data, TileProtocol.ClientFrameGameMessage,
                        out ushort kind, out ReadOnlySpan<byte> payload))
                    OnGameMessage?.Invoke(slot, kind, payload);
                else DroppedCommandCount++;
                return;
            default:
                DroppedCommandCount++;
                return;
        }
    }

    // Idempotent by design, and that is load-bearing rather than tidy: Kick calls this synchronously and the
    // transport then surfaces a Left event for the same slot on a later poll, which calls it a second time. The
    // TryGetValue guard is what stops PlayerLeaving double-firing (and a persistence layer double-saving), and
    // every Remove below is already a no-op on a missing key.
    void OnLeave(int slot)
    {
        if (netIdBySlot.TryGetValue(slot, out long netId))
        {
            // Read before the despawn, because this IS the last moment the state exists. Both halves are required:
            // a slot with no account id is one nothing can be filed under.
            if (accountIdBySlot.TryGetValue(slot, out string? account)
                && TryGetPlayerState(slot, out TileMoveState final))
                PlayerLeaving?.Invoke(slot, account, final);
            if (host.TryGetOwner(netId, out CellSim cell, out Entity e) && cell.World.IsAlive(e))
            {
                // Eager: out of the ownership index before the despawn, so a handoff pass on the same frame cannot
                // find a dead entity through it.
                cell.UnregisterOwned(netId);
                cell.World.Despawn(e);
            }
        }
        host.UnbindClient(slot);
        netIdBySlot.Remove(slot);
        accountIdBySlot.Remove(slot);
        lastAckBySlot.Remove(slot);
        rateBySlot.Remove(slot);
        // A slot is a SEAT the next connection recycles, so both per-slot queues are forgotten rather than merely
        // cleared. A stale command high-water mark would reject every sequence number the next occupant sends,
        // whose own numbering restarts at zero, and freeze them until it crawled past the dead mark minutes later.
        // A surviving pending action would fire against a player who never clicked anything.
        commands.Forget(slot);
        actions.Forget(slot);
    }

    /// <summary>
    /// Installs the hint a join consults BEFORE the player entity exists, so a rejoining player is BUILT on the
    /// tile they left rather than at the spawn and then moved onto their record. Null clears it, which returns
    /// every join to the configured spawn.
    /// <para>This is the server half of the reconnect contract, and it is what keeps a quiet rejoin quiet. A head
    /// that spawned at the configured spawn and applied the stored tile afterwards has already served one snapshot
    /// saying the player is somewhere they are not, and the client reads that as a teleport.
    /// <see cref="TileWorldPersistence"/> installs one at construction, so a persistence-backed head needs no
    /// wiring.</para>
    /// </summary>
    /// <param name="provider">The hint source, or null to clear it.</param>
    public void SetPositionHintProvider(PositionHintProvider? provider) => hintProvider = provider;

    /// <summary>
    /// Where this slot WOULD have been built with no hint at all: the configured spawn, facing south. Deliberately
    /// hint-free, which is the whole point of it. Seeding a join from a hint means a QUARANTINED record can no
    /// longer just decline to place the player, because they are already standing on a position nothing validated,
    /// so this is the tile policy moves them back to.
    /// </summary>
    /// <param name="slot">The player's connection slot.</param>
    /// <param name="spawn">The configured spawn state. Always written, whether or not the slot is known.</param>
    /// <returns>False for a slot with no player, which is a slot nothing needs resetting.</returns>
    public bool TryGetConfiguredSpawn(int slot, out TileMoveState spawn)
    {
        spawn = TileMoveState.At(config.Spawn, TileDirection.S);
        return netIdBySlot.ContainsKey(slot) || accountIdBySlot.ContainsKey(slot);
    }

    // The end of a drain, run once from Tick the first time the grace is spent. Closing the sessions is what a
    // grace is FOR: the players were told, they had their time, and now every one of them leaves through the
    // ordinary path, so PlayerLeaving is raised for each and a persistence layer files the final state it would
    // otherwise never see. A head that exits without this saves nothing newer than its last periodic pass.
    void CloseDrainedSessions()
    {
        drainScratch.Clear();
        drainScratch.AddRange(netIdBySlot.Keys);
        for (int i = 0; i < drainScratch.Count; i++) Kick(drainScratch[i], drainReason);
    }

    /// <summary>Closes one session with a reason token and despawns its player. The notice goes out BEFORE the
    /// disconnect, so the client learns why rather than seeing an unexplained drop, and the player is released
    /// synchronously rather than on the poll that observes the transport catching up.</summary>
    /// <param name="slot">The connection slot to close. An unknown slot is a no-op.</param>
    /// <param name="reasonToken">A <see cref="TileServerReason"/> token, or a game's own.</param>
    public void Kick(int slot, string reasonToken)
    {
        SendNotice(slot, reasonToken);
        net.Disconnect(slot);
        OnLeave(slot);
    }

    /// <summary>Sends an opaque game message to one client, reliably and in order with that client's snapshots.
    /// The engine never looks inside <paramref name="payload"/>.</summary>
    /// <param name="slot">The connection slot to send to. An unknown slot is a no-op.</param>
    /// <param name="kind">The game's own message kind.</param>
    /// <param name="payload">The message body, at most <see cref="TileProtocol.MaxGameMessageBytes"/>.</param>
    public void SendGameMessageTo(int slot, ushort kind, ReadOnlySpan<byte> payload) =>
        net.SendTo(slot, TileProtocol.EncodeGameMessage(TileProtocol.ServerFrameGameMessage, kind, payload),
            NetChannelReliability.ReliableOrdered);

    /// <summary>Sends a reason token to every joined client, for the server-wide announcements a game does not
    /// need a message kind of its own for.</summary>
    /// <param name="reasonToken">A <see cref="TileServerReason"/> token, or a game's own.</param>
    public void BroadcastNotice(string reasonToken) =>
        net.Broadcast(TileProtocol.EncodeNotice(reasonToken), NetChannelReliability.ReliableOrdered);

    void SendNotice(int slot, string reasonToken) =>
        net.SendTo(slot, TileProtocol.EncodeNotice(reasonToken), NetChannelReliability.ReliableOrdered);

    /// <summary>
    /// Tells every client the server is going away, then counts <paramref name="graceSeconds"/> down on
    /// <see cref="Tick"/> so a head can flush persistence and exit once <see cref="IsDrainComplete"/>. The world
    /// keeps ticking throughout: a drain is a deadline, not a freeze, so a player mid walk finishes it.
    /// <para>The countdown is WALL CLOCK rather than tick count, so it runs down on frames that stepped nothing.
    /// A head asked to shut down has a real-time deadline whatever the simulation is doing, and a server that had
    /// fallen behind would otherwise take longer to drain the further behind it was.</para>
    /// <para>Deliberately a small local state machine rather than <c>NetWorld.DrainController</c>: that type sits
    /// in a package this one must never reference, and duplicating three lines of countdown is cheaper than the
    /// dependency. When the controller moves down to <c>KhaozEngine.Simulation</c>, this is the call site that
    /// adopts it.</para>
    /// <para>Idempotent: a second call while a drain is running is ignored rather than restarting the clock, so an
    /// operator who runs the command twice does not hand everyone a second grace period.</para>
    /// <para>When the grace is spent, the next <see cref="Tick"/> closes every remaining session through
    /// <see cref="Kick"/>, so each player leaves by the ordinary path and a persistence layer gets the
    /// <see cref="PlayerLeaving"/> it needs to file their final state.</para>
    /// </summary>
    /// <param name="reasonToken">A <see cref="TileServerReason"/> token, normally
    /// <see cref="TileServerReason.Draining"/>. Kept, and sent again to anyone who joins inside the grace.</param>
    /// <param name="graceSeconds">Seconds before <see cref="IsDrainComplete"/> turns true. Negative is treated as
    /// zero, which completes on the next tick.</param>
    public void BeginDrain(string reasonToken, float graceSeconds)
    {
        if (IsDraining) return;
        drainReason = reasonToken;
        drainRemaining = Math.Max(0f, graceSeconds);
        BroadcastNotice(reasonToken);
    }
}
