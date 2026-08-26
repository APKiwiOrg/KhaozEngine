using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The RECEIVE half of <see cref="TileWorldClient"/>: the session events, the frame demux, and everything a
/// snapshot turns into. The other partial owns the command tick and the send path, and the split is along that
/// seam rather than at a line count, because the two halves share only the fields between them.
/// </summary>
public sealed partial class TileWorldClient
{
    // Below this a reconciliation moved the local player by less than a thousandth of a tile, which is float noise
    // in the replay rather than a disagreement. Counting those would report a correction on every snapshot of a
    // perfectly predicted walk and make CorrectionCount useless as the health number it is meant to be.
    const float CorrectionEpsilon = 1e-4f;

    readonly HashSet<long> liveRemotes = new();
    // The captures RefreshRemoteSamples would otherwise close over, hoisted so the ECS callback can be cached
    // instead of rebuilt every frame. Live only for the duration of one refresh, which nothing re-enters.
    RefAction<NetId>? sampleRemotes;
    double sampleTime;

    // The freshest committed tile this client holds per remote, off the newest APPLIED snapshot rather than off
    // the delayed render timeline the bodies ride. See TryGetLatestRemoteTile for what that buys and what it
    // costs. Its own scratch, rather than RefreshRemoteSamples', because the two passes run at different moments
    // (one per snapshot, one per frame) and sharing the set would make an interleaving that never happens today
    // into a bug the day it does.
    readonly Dictionary<long, LatestTile> latestTiles = new();
    readonly HashSet<long> liveLatest = new();
    readonly List<long> goneLatest = new();
    RefAction<NetId>? captureLatest;
    double latestAt;

    /// <summary>
    /// Pumps the transport and applies whatever arrived. Call it once per frame, BEFORE drawing and before
    /// <see cref="AdvancePresentation"/>, so a frame draws the newest snapshot rather than the previous one.
    /// <para>The inbox is drained to EMPTY on every call rather than a few events at a time, and not because
    /// anything trims it: <c>NetClient</c>'s inbox is a plain unbounded queue, and its own poll drains the whole
    /// transport queue into it on every call, so the transport's cap cannot bite here either. The reasons are that
    /// session events are strictly ORDERED, so a snapshot left behind is a frame of reconciliation basis that is
    /// already stale by the time it is read, and that a queue nothing evicts grows without bound the moment a head
    /// stops keeping up with it.</para>
    /// </summary>
    public void Poll()
    {
        net.Poll();
        while (net.TryDequeueEvent(out ClientSessionEvent ev))
        {
            switch (ev.Kind)
            {
                case ClientSessionEventKind.Joined:
                    IsJoined = true;
                    break;
                case ClientSessionEventKind.Rejected:
                    // Terminal. The reason is kept as well as raised, so a head that wires its handler after the
                    // first poll (a loading screen built once the transport is up) can still read why.
                    RefusedReason = ev.RejectReason;
                    IsJoined = false;
                    RefusedAtDoor?.Invoke(ev.RejectReason);
                    break;
                case ClientSessionEventKind.Disconnected:
                    IsJoined = false;
                    Disconnected?.Invoke();
                    break;
                case ClientSessionEventKind.Data:
                    OnServerFrame(ev.Data);
                    break;
            }
        }
    }

    // Demux by TAG, never by length, which is the wire's own rule (see TileProtocol). A frame whose decoder refuses
    // it is dropped silently: every byte here came off a socket, and a client that threw on a malformed frame would
    // hand anything on the path between it and the server a way to kill its render loop.
    void OnServerFrame(byte[] data)
    {
        switch (TileProtocol.ServerFrameTag(data))
        {
            case TileProtocol.ServerFrameSnapshot:
                if (TileProtocol.TryDecodeSnapshotFrame(data, out long localNetId, out int ackSeq,
                        out long serverTick, out byte[] snapshot))
                    OnSnapshot(localNetId, ackSeq, serverTick, snapshot);
                else DroppedSnapshotCount++;
                return;
            case TileProtocol.ServerFrameGameMessage:
                if (TileProtocol.TryDecodeGameMessage(data, TileProtocol.ServerFrameGameMessage,
                        out ushort kind, out ReadOnlySpan<byte> payload))
                    OnGameMessage?.Invoke(kind, payload);
                return;
            case TileProtocol.ServerFrameNotice:
                if (TileProtocol.TryDecodeNotice(data, out string reason)) OnNotice(reason);
                return;
        }
    }

    // Every notice raises NoticeReceived, including the ones that also have a typed event of their own. A head that
    // only wants the typed one subscribes to that, and a head logging the wire sees the whole stream in one place.
    void OnNotice(string reason)
    {
        NoticeReceived?.Invoke(reason);
        if (reason == TileServerReason.CannotReach) CannotReach?.Invoke();
    }

    // One snapshot: the remote timeline gets a sample, and the local player gets a reconciliation basis.
    void OnSnapshot(long localNetId, int ackSeq, long serverTick, byte[] snapshot)
    {
        LocalNetId = localNetId;
        ServerTick = serverTick;
        // A snapshot the registry cannot decode is refused WHOLE. TryApply returns false for a component frame that
        // lies about its own length, and the half of the frame that did apply before the throw is not a world state
        // anybody chose: reconciling onto a basis rebuilt from it would rebase the player onto a plausible-looking
        // answer to a question nobody asked. Counted and dropped, which is safe only because the next
        // snapshot is a FULL one: this server serves SnapshotWriter.WriteFiltered every tick, so the next serve
        // overwrites every component of every entity in interest. The day the serve moves to AoiDeltaReplicator,
        // which Replication already offers, a dropped frame leaves a hole no later delta fills.
        if (!View.TryApply(World, snapshot, out _))
        {
            DroppedSnapshotCount++;
            return;
        }

        // THE HONEST READ's capture, and this is the ONE instant it can be taken. Apply has just written the newest
        // server state for every entity in the snapshot into World, and the next AdvancePresentation overwrites all
        // of it with the delayed timeline's answer, so a pass taken anywhere else reads the delayed value back.
        CaptureLatestTiles();

        // Remotes ride a DELAYED timeline, so the sample is buffered here at its arrival time and read back later,
        // at a render time two ticks behind (see AdvancePresentation). The local player is excluded from the buffer
        // on purpose: it is predicted rather than interpolated, and buffering it would clobber the replicated value
        // this method is about to use as a reconciliation basis with a stale one.
        View.RecordInterpolationSample(presentationClock, excludeNetId: localNetId);

        if (!View.TryGetEntity(localNetId, out Entity local) || !World.TryGet(local, out TileMoveState basis)) return;
        // The route arrives as its OWN owner-only component and is put back onto the state through the one helper
        // that does it, rather than through a second copy of the rule here. A basis with no route stands the player
        // still, so every reconciliation would cancel a walk the player never cancelled, and the replay of the
        // pending commands would have nothing to walk along.
        World.TryGet(local, out TileRouteState route);
        basis = TileProtocol.AssembleMoveState(basis, route);

        if (!seeded)
        {
            // The first snapshot PLACES the player rather than correcting one. Reset also zeroes the command
            // sequence, which is why nothing is predicted or sent before this point: a command sent earlier would
            // burn a number this rewinds, and the server refuses the re-used one as stale.
            seeded = true;
            Prediction.Reset(basis);
            return;
        }

        ReconciliationResult result = Prediction.Reconcile((int)serverTick, basis, ackSeq);
        if (result.PositionError > CorrectionEpsilon) CorrectionCount++;
        if (result.HardSnapApplied) SnapCount++;
        // A CUT places the local body outright, and it is the PREDICTION LAYER that does it: a teleport (an
        // authoritative epoch advance) and a hard snap both zero that layer's correction offsets, so
        // RenderedState, which is the only thing LocalPose draws, is on the corrected position on the very frame
        // the snapshot lands. There is nothing to reset here, which is the shape's own dividend: one place decides
        // what a discontinuity does to the local body rather than two agreeing about it.
        //
        // Raised rather than folded into SnapCount, because the two say different things to a head: a snap is "you
        // mispredicted a step", a teleport is "you are somewhere else now". See the event's own doc.
        if (result.Teleported) Teleported?.Invoke();
    }

    /// <summary>
    /// Where a remote's BODY draws right now: its own sample, glided from <see cref="TileMoveState.StepFrom"/>
    /// into <see cref="TileMoveState.Tile"/> and carried forward across the fraction of a tick that has passed
    /// since the sample was taken. False for an unknown net id and for the local player, who is drawn through
    /// <see cref="LocalPose"/> instead.
    /// <para>A remote lags its own committed tile by the same step the local body does, and by
    /// <see cref="TileWorldClientConfig.InterpolationDelayTicks"/> on top of it: it is read off a timeline held
    /// that far behind live, which is what buys room for a lost snapshot. Size a design that reads other players'
    /// tiles against the SUM, not against the step alone.</para>
    /// </summary>
    /// <param name="netId">The remote's net id.</param>
    /// <param name="pose">Where and which way to draw it.</param>
    /// <returns>True when this client is tracking <paramref name="netId"/> as a remote.</returns>
    public bool TryGetRemotePose(long netId, out TilePose pose)
    {
        pose = default;
        if (netId == LocalNetId || !remoteSamples.TryGetValue(netId, out RemoteSample sample)) return false;
        pose = Presenter.Pose(sample.State, (float)Math.Max(0d, (RenderTime - sample.At) / config.TickSeconds));
        return true;
    }

    /// <summary>
    /// The tile a remote is COMMITTED to, straight off its replicated state, with nothing glided and nothing
    /// carried forward. False for an unknown net id and for the local player, whose committed tile is
    /// <c>Prediction.PredictedState.Tile</c>.
    /// <para>THE DELAYED TIMELINE IS WHAT THIS READS, and it is the half of the pair to be careful with.
    /// <see cref="TryGetRemotePose"/> is the remote's BODY and this is the tile that body is walking into, both
    /// resampled at the same delayed render time, so the two agree with each other and neither agrees with the
    /// server: both are additionally <see cref="TileWorldClientConfig.InterpolationDelayTicks"/> behind what the
    /// server has committed. That AGREEMENT is the whole reason to pick this read, and it makes it the right one
    /// for an overlay drawn ON the body (a marker under a walking remote, a debug view of the drawn world). For a
    /// RULE, ask <see cref="TryGetLatestRemoteTile(long, out TileCoord)"/> instead, which answers off the newest
    /// applied snapshot and is not held behind the delay. Map either one with
    /// <see cref="TilePresenter.PoseAt(TileCoord, TileDirection)"/>.</para>
    /// <para>The remote's ROUTE is deliberately not available: it is owner-only on the wire, so no client can
    /// highlight another player's path. A route highlight is a LOCAL-player overlay only.</para>
    /// </summary>
    /// <param name="netId">The remote's net id.</param>
    /// <param name="tile">The tile the remote's replicated state is committed to.</param>
    /// <returns>True when this client is tracking <paramref name="netId"/> as a remote.</returns>
    public bool TryGetRemoteTile(long netId, out TileCoord tile)
    {
        tile = default;
        if (netId == LocalNetId || !remoteSamples.TryGetValue(netId, out RemoteSample sample)) return false;
        tile = sample.State.Tile;
        return true;
    }

    /// <summary>
    /// The tile a remote is committed to on the FRESHEST server state this client holds, which is a different
    /// question from <see cref="TryGetRemoteTile"/> and the one a rule should be asking. This one is read off the
    /// newest APPLIED snapshot. That one is read off the delayed render timeline the bodies ride, so it is
    /// additionally <see cref="TileWorldClientConfig.InterpolationDelayTicks"/> behind. False for an unknown net id
    /// and for the local player, exactly as <see cref="TryGetRemoteTile"/> is.
    /// <para>WHICH ONE TO USE. Draw an overlay that has to agree with the BODY it sits under (a nameplate anchor, a
    /// marker that tracks a walking remote) off <see cref="TryGetRemoteTile"/>, because the body and that read come
    /// off one timeline and cannot disagree. Ask anything the RULES will answer (is that monster adjacent, what did
    /// I just click, is my target still in reach) off THIS one, because the delayed read is the truth from a moment
    /// that has already passed and a rule built on it is wrong by construction. Measured in this package's
    /// loopback at a 1/6 s tick with the default two tick delay, the delayed read runs up to 2 ticks behind the
    /// server's own committed tile where this one runs behind by the transport latency plus at most one snapshot
    /// interval.</para>
    /// <para>The remote's ROUTE is deliberately not available on either read: it is owner-only on the wire, so no
    /// client can highlight another player's path. A route highlight is a LOCAL-player overlay only.</para>
    /// <para>A remote is known to this read from the snapshot that first carried it, which is up to one call before
    /// <see cref="RemoteNetIds"/> lists it (that collection is rebuilt in <see cref="AdvancePresentation"/>). So
    /// iterate <see cref="RemoteNetIds"/> and accept that this may answer for an id it does not yet hold, rather
    /// than treating the two as one set.</para>
    /// </summary>
    /// <param name="netId">The remote's net id.</param>
    /// <param name="tile">The tile the newest applied server state has the remote committed to.</param>
    /// <returns>True when this client holds a server state for <paramref name="netId"/> as a remote.</returns>
    public bool TryGetLatestRemoteTile(long netId, out TileCoord tile)
        => TryGetLatestRemoteTile(netId, out tile, out _);

    /// <summary>
    /// <see cref="TryGetLatestRemoteTile(long, out TileCoord)"/> plus how OLD the answer is, so an overlay can fade
    /// or hide a marker it can no longer stand behind rather than drawing a confident ring on a stale tile.
    /// <para><paramref name="ticksOld"/> is wall clock on this client's own presentation clock since the snapshot
    /// that produced the answer was applied, divided by <see cref="TileWorldClientConfig.TickSeconds"/>. In a
    /// healthy session it sits under one tick and never settles, because it climbs between snapshots and drops on
    /// each one. It climbs without bound while snapshots are not arriving, which is the case worth drawing
    /// differently.</para>
    /// <para>It does NOT include the one-way latency the snapshot spent in flight, which no client can see without
    /// an RTT estimate this package does not keep. So it is a LOWER bound on the true age, and a threshold built on
    /// it wants headroom for the link. It also only advances as fast as <see cref="AdvancePresentation"/> is
    /// called: a head that stopped driving its render clock reads zero here, and is reading a frozen delayed
    /// timeline through the other read at the same moment.</para>
    /// </summary>
    /// <param name="netId">The remote's net id.</param>
    /// <param name="tile">The tile the newest applied server state has the remote committed to.</param>
    /// <param name="ticksOld">Command ticks of client wall clock since that state was applied, zero when false.</param>
    /// <returns>True when this client holds a server state for <paramref name="netId"/> as a remote.</returns>
    public bool TryGetLatestRemoteTile(long netId, out TileCoord tile, out float ticksOld)
    {
        tile = default;
        ticksOld = 0f;
        if (netId == LocalNetId || !latestTiles.TryGetValue(netId, out LatestTile latest)) return false;
        tile = latest.Tile;
        // Max rather than a raw subtract for the same reason AdvancePresentation sanitizes its dt: the clock only
        // moves forward, so a negative here would mean it stopped being a clock, and an age below zero is not an
        // answer any caller can use.
        ticksOld = (float)Math.Max(0d, (presentationClock - latest.At) / config.TickSeconds);
        return true;
    }

    // Every remote in the snapshot Apply has just finished writing, stamped with the client clock. Full-state
    // snapshots are what make one pass do both jobs: World now carries exactly what the server sent, so anything
    // this pass does not see has left the viewer's area of interest and is pruned here rather than a frame later.
    //
    // The stamp is REFRESHED on every snapshot even when the tile did not change, which is the opposite of what
    // SampleRemote does with its own, and the difference is the two questions. This one is "when did the server
    // last tell me this", which is freshness. That one is "when did this state first appear", which is where the
    // glide measures its carry-forward from, and re-stamping it would freeze a remote on its last snapshot.
    void CaptureLatestTiles()
    {
        captureLatest ??= CaptureLatest;
        latestAt = presentationClock;
        liveLatest.Clear();
        World.ForEach(captureLatest);
        // Equal counts means nothing left, which is the ordinary snapshot, so the prune can be skipped.
        if (latestTiles.Count == liveLatest.Count) return;
        goneLatest.Clear();
        foreach (long netId in latestTiles.Keys)
            if (!liveLatest.Contains(netId)) goneLatest.Add(netId);
        for (int i = 0; i < goneLatest.Count; i++) latestTiles.Remove(goneLatest[i]);
    }

    void CaptureLatest(Entity e, ref NetId id)
    {
        long netId = id.Value;
        if (netId == LocalNetId) return;
        if (!World.TryGet(e, out TileMoveState now)) return;
        liveLatest.Add(netId);
        latestTiles[netId] = new LatestTile(now.Tile, latestAt);
    }

    // Rebuilds the per-remote draw states off whatever InterpolateAt just wrote into the world. Called every frame
    // from AdvancePresentation rather than on snapshot arrival, because the delayed timeline advances with the
    // FRAME and a remote resampled once per packet hops at the tick rate whatever the frame rate.
    void RefreshRemoteSamples(double renderTime)
    {
        sampleRemotes ??= SampleRemote;
        sampleTime = renderTime;
        liveRemotes.Clear();
        World.ForEach(sampleRemotes);
        // Every live remote was just written into the map, so equal counts means nothing went stale and the prune
        // can be skipped, which is the ordinary frame.
        if (remoteSamples.Count == liveRemotes.Count) return;
        goneRemotes.Clear();
        foreach (long netId in remoteSamples.Keys)
            if (!liveRemotes.Contains(netId)) goneRemotes.Add(netId);
        for (int i = 0; i < goneRemotes.Count; i++) remoteSamples.Remove(goneRemotes[i]);
    }

    // One remote, one frame. The replicated state says where the body is outright: the tile the remote is
    // committed to, the tile it is walking out of, and how far through. So there is nothing to reconstruct here,
    // and nothing to guess. All this does is STAMP a changed sample with the render-timeline instant it was first
    // seen at, which is what the presenter measures its sub-tick carry-forward from.
    //
    // It used to be the other way round. The everyone channel carried a tile and step progress and nothing about
    // where the step was GOING, so the only honest answer was to draw a remote ONE STEP BEHIND, between the tile it
    // was last seen on and the tile it is on now, at a whole step of extra latency, with a second rule to tell a
    // step from a teleport. Committing at the START of a step is what made the pair of tiles a fact the server can
    // simply send, and that whole reconstruction went with it.
    void SampleRemote(Entity e, ref NetId id)
    {
        long netId = id.Value;
        if (netId == LocalNetId) return;
        if (!World.TryGet(e, out TileMoveState now)) return;
        liveRemotes.Add(netId);

        // An unchanged sample KEEPS its stamp, which is the whole reason the previous one is compared rather than
        // overwritten every frame: re-stamping would restart the carry-forward on every frame and freeze the
        // remote on the instant of its last snapshot. Equality is the simulation's own, so a turn on the spot
        // counts as a change (BeginInteract writes Facing with no tile change and no progress, which is the
        // ordinary click on the thing you are already standing next to) while a re-render of the same tick does
        // not.
        if (remoteSamples.TryGetValue(netId, out RemoteSample prev) && prev.State.Equals(now)) return;
        remoteSamples[netId] = new RemoteSample(now, sampleTime);
    }

    /// <summary>
    /// One remote's drawing state. <paramref name="State"/> is the replicated state verbatim, which is what
    /// <see cref="TilePresenter.Pose"/> is handed: the tile the remote is committed to and the one its body is
    /// still walking out of. <paramref name="At"/> is the render-timeline instant that state was first seen at,
    /// which is what the fraction of a tick since then is measured from.
    /// </summary>
    readonly record struct RemoteSample(TileMoveState State, double At);

    /// <summary>
    /// One remote's freshest committed tile. <paramref name="Tile"/> is straight off the newest applied snapshot,
    /// and <paramref name="At"/> is the client-clock instant that snapshot was applied, which is what
    /// <see cref="TryGetLatestRemoteTile(long, out TileCoord, out float)"/> measures the answer's age from. Only
    /// the tile is kept: everything else on the state is presentation, and the delayed timeline is where
    /// presentation is read.
    /// </summary>
    readonly record struct LatestTile(TileCoord Tile, double At);
}
