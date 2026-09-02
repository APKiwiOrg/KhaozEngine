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
/// allowed to do, and the operator controls over a live session. The other three partials are construction and the
/// player index (<c>TileWorldServer.cs</c>), the tick order and the serve (<c>TileWorldServer.Tick.cs</c>), and the
/// pending-action resolution (<c>TileWorldServer.Actions.cs</c>).
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
    // Slots whose player is lingering after a combat logout, and the tick each one is released on. Expired from
    // Tick rather than from RunOneTick, for the same reason the drain's close is: releasing a session mutates the
    // player index the tick body is iterating.
    readonly Dictionary<int, long> lingerUntilTick = new();
    readonly List<int> lingerScratch = new();
    // A SECOND scratch list rather than a share of the one above, because the two walks can nest: a game's own
    // PlayerLeaving handler is free to seat someone, and a reclaim running inside an expiry would otherwise rewrite
    // the list the expiry is still walking.
    readonly List<int> reclaimScratch = new();

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
    /// key collection, so it reflects a join or a leave immediately and must not be enumerated across one.
    /// <para>A slot held by a body LINGERING under <see cref="TileWorldServerConfig.CombatLogoutTicks"/> is in here,
    /// and has to be: the body is still being stepped and hit, so a periodic save that skipped it would file a state
    /// older than the fight that is still happening to it.</para></summary>
    public IReadOnlyCollection<int> JoinedSlots => netIdBySlot.Keys;

    /// <summary>True from the moment <see cref="BeginDrain"/> runs until the server is disposed. A drain is
    /// one way: there is no resume, because the announcement has already gone out to every client.</summary>
    public bool IsDraining => drainRemaining >= 0f;

    /// <summary>True once a drain's grace has elapsed AND the sessions it was counting down for have been closed,
    /// so the host may flush persistence and exit. Polled by the head's own loop rather than raised as an event,
    /// because what happens next (a save pass, a process exit) is the head's decision and its timing is the head's
    /// to own.
    /// <para>The close is part of the answer rather than a consequence of it. A spent grace on its own is true the
    /// instant <see cref="BeginDrain"/> returns with a grace of zero, with every session still open and no
    /// <see cref="PlayerLeaving"/> raised for any of them, so a head that exited on it would file nothing newer than
    /// its last periodic pass. The close runs on the next <see cref="Tick"/>, which is what this waits for.</para>
    /// </summary>
    public bool IsDrainComplete => IsDrainGraceSpent && drainClosed;

    // The grace half of IsDrainComplete, and the trigger the tick uses. Separate from the property so the close can
    // depend on it without the property depending on the close, which would be circular.
    bool IsDrainGraceSpent => IsDraining && drainRemaining <= 0f;

    /// <summary>
    /// Pumps the transport and turns session events into joins, leaves and buffered commands. Call once per host
    /// frame, BEFORE <see cref="Tick"/>: a command that arrived this frame is then routed by the very next tick
    /// rather than waiting a whole one, which is the difference between a click landing in 250 ms and in 500 ms.
    /// <para>Emptying the event inbox on every poll is a contract rather than a courtesy. <c>NetServer</c> caps
    /// its undrained inbox and drops the OLDEST event once a host stops keeping up, so a poll that left events
    /// behind would lose joins first.</para>
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
        // A connection that arrives after the drain already closed everything is told and dropped rather than
        // seated. The head has flushed by this point, so seating one would raise PlayerJoined for an account
        // nothing will file again, and leaving it seated would keep a client connected to a server that considers
        // itself gone. This is the ONE join that is refused: one arriving inside the grace is admitted, see below.
        if (drainClosed)
        {
            SendNotice(slot, drainReason);
            net.Disconnect(slot);
            return;
        }
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

    // Everything an inbound frame is allowed to do, and every way it is refused. The order is deliberate: an
    // unknown slot costs a dictionary probe, the rate budget costs a subtraction, and only a frame that passed both
    // is decoded. Decoding is the most expensive of the three and the only one an attacker chooses the size of.
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
    void OnLeave(int slot, bool force = false)
    {
        // A player who was in combat is NOT removed at once: the body lingers in world, still stepped, still served
        // and still attackable, until the window lapses. That is what stops a losing fight being escaped by pulling
        // the plug. FORCED for an operator kick, for a drain and for a seat being recycled by a new connection,
        // because none of those is the leaving player's decision.
        if (!force && config.CombatLogoutTicks > 0 && !lingerUntilTick.ContainsKey(slot)
            && netIdBySlot.TryGetValue(slot, out long lingering) && IsInCombat(lingering))
        {
            lingerUntilTick[slot] = TickCount + config.CombatLogoutTicks;
            return;
        }
        lingerUntilTick.Remove(slot);
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
        // Both directions of the seat index go at the same moment. A reverse entry left behind would answer a slot
        // for a player who left, and the next occupant of that seat would then have two ids pointing at it.
        if (netIdBySlot.TryGetValue(slot, out long leavingNetId)) slotByNetId.Remove(leavingNetId);
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

    // In combat means holding a lock, or a combat event having touched them inside the window, which is section
    // 13.3's own phrasing and is deliberately not "damage landed": the player this rule exists to stop escaping is
    // the one being attacked who has not clicked back, and a fight in which every swing misses is exactly that
    // player's fight. TileCombatState.LastCombatTick is the fact, written for both parties on every resolved swing.
    //
    // The damage record is NOT read here. It answers a different question (who hurt me, for a retaliation) and a
    // miss must not move it, so reading it for this would be reading the wrong fact for the wrong reason.
    bool IsInCombat(long netId)
    {
        if (TryGetActorState(netId, out TileMoveState state) && state.CombatTarget != 0) return true;
        return TryGetCombatState(netId, out TileCombatState combat) && combat.LastCombatTick != 0
            && TickCount - combat.LastCombatTick <= config.CombatLogoutTicks;
    }

    // ONE ACCOUNT, ONE LIVE BODY, and the linger is the only thing in the tree that can hold a seat the session
    // layer has already let go of. NetServer releases the slot the moment the link drops (RemovePeer clears
    // slotBySubject too), so during the window the account is invisible to the duplicate-session gate and a
    // reconnect is handed the LOWEST free slot, which can be BELOW the one the lingering body sits on. SpawnPlayer's
    // own seat-recycle guard cannot see that, because it guards the new SLOT and this is the same ACCOUNT on a
    // different one.
    //
    // Ended rather than refused, and that is the ruling this reads out of 13.3. The window holds the SEAT so a
    // losing fight cannot be escaped by pulling the plug, and a player who comes straight back has escaped nothing:
    // they are seated where they left (the rejoin hint is their own stored tile) and the fight is still there.
    // Refusing the rejoin until the window lapsed would lock a player out of their own fight, which is the opposite
    // of what the ruling is for, and it would still have to answer this same question for the tick the window ends
    // on. Ending the body cannot duplicate, because the leave runs BEFORE the new seat is built: PlayerLeaving files
    // the pre-drop state and clears persistence's in-flight guard for the account, and the join that follows loads
    // it. That is exactly the ordering NetServer.EndOlderSession already imposes on a duplicate session.
    //
    // An UNRELATED player still cannot end it, which is the linger's whole point.
    void ReleaseLingerFor(string accountId)
    {
        if (lingerUntilTick.Count == 0) return;
        reclaimScratch.Clear();
        // accountIdBySlot still holds the lingering seat's account: the deferral returns from OnLeave ahead of every
        // Remove below it, so the linger needs no second record of who it belongs to.
        foreach (KeyValuePair<int, long> entry in lingerUntilTick)
            if (accountIdBySlot.TryGetValue(entry.Key, out string? held) && held == accountId)
                reclaimScratch.Add(entry.Key);
        for (int i = 0; i < reclaimScratch.Count; i++) OnLeave(reclaimScratch[i], force: true);
    }

    // Released from Tick, once each, through the ordinary leave path, so PlayerLeaving is raised and a persistence
    // layer files the final state exactly as it would for a player who logged out cleanly.
    void ExpireLingeringSessions()
    {
        if (lingerUntilTick.Count == 0) return;
        lingerScratch.Clear();
        foreach (KeyValuePair<int, long> entry in lingerUntilTick)
            if (TickCount >= entry.Value) lingerScratch.Add(entry.Key);
        for (int i = 0; i < lingerScratch.Count; i++)
        {
            lingerUntilTick.Remove(lingerScratch[i]);
            OnLeave(lingerScratch[i], force: true);
        }
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
        // The player index alone, which is the same table JoinedSlots and TryGetPlayerState answer from. The
        // account table is written and removed with it on every path, so a second probe cannot change the answer
        // and would only read as though there were a state where the two disagree.
        return netIdBySlot.ContainsKey(slot);
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
    /// synchronously rather than on the poll that observes the transport catching up.
    /// <para>Immediate even for a player mid fight: <see cref="TileWorldServerConfig.CombatLogoutTicks"/> is
    /// deliberately bypassed here, and by the drain that closes through this, because an operator close is not the
    /// leaving player's decision and a body left standing for it would outlive the ban or the shutdown that caused
    /// it.</para></summary>
    /// <param name="slot">The connection slot to close. An unknown slot is a no-op.</param>
    /// <param name="reasonToken">A <see cref="TileServerReason"/> token, or a game's own.</param>
    public void Kick(int slot, string reasonToken)
    {
        SendNotice(slot, reasonToken);
        net.Disconnect(slot);
        OnLeave(slot, force: true);
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
    /// <see cref="PlayerLeaving"/> it needs to file their final state. That close is the point
    /// <see cref="IsDrainComplete"/> turns true, and after it a new connection is told the reason and dropped
    /// rather than seated.</para>
    /// </summary>
    /// <param name="reasonToken">A <see cref="TileServerReason"/> token, normally
    /// <see cref="TileServerReason.Draining"/>. Kept, and sent again to anyone who joins inside the grace.</param>
    /// <param name="graceSeconds">Seconds counted down before the sessions are closed. Zero or negative closes them
    /// on the very next <see cref="Tick"/>, which is also when <see cref="IsDrainComplete"/> can first turn
    /// true.</param>
    public void BeginDrain(string reasonToken, float graceSeconds)
    {
        if (IsDraining) return;
        drainReason = reasonToken;
        drainRemaining = Math.Max(0f, graceSeconds);
        BroadcastNotice(reasonToken);
    }
}
