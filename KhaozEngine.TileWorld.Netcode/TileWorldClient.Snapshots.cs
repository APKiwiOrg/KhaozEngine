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
        // Raised rather than folded into SnapCount, because the two say different things to a head: a snap is "you
        // mispredicted a step", a teleport is "you are somewhere else now". See the event's own doc.
        if (result.Teleported) Teleported?.Invoke();
    }

    /// <summary>
    /// Where a remote draws right now: its own sample, carried forward across the fraction of a tick that has
    /// passed since it was taken. False for an unknown net id and for the local player, who is drawn from
    /// prediction through <see cref="TilePresenter.LocalPose"/> instead.
    /// </summary>
    /// <param name="netId">The remote's net id.</param>
    /// <param name="pose">Where and which way to draw it.</param>
    /// <returns>True when this client is tracking <paramref name="netId"/> as a remote.</returns>
    public bool TryGetRemotePose(long netId, out TilePose pose)
    {
        pose = default;
        if (netId == LocalNetId || !remoteSamples.TryGetValue(netId, out RemoteSample sample)) return false;
        pose = Presenter.Pose(sample.Glide, (float)Math.Max(0d, (RenderTime - sample.At) / config.TickSeconds));
        return true;
    }

    // Rebuilds the per-remote draw states off whatever InterpolateAt just wrote into the world. Called every frame
    // from AdvancePresentation rather than on snapshot arrival, because the delayed timeline advances with the
    // FRAME and a remote resampled once per packet hops four times a second whatever the frame rate.
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

    // One remote, one frame. The whole method is about answering a question the wire deliberately does not: a
    // remote's ROUTE is owner-only, so its replicated state names the tile it is on and how far through a step it
    // is, and nothing at all about where the step is going.
    //
    // The answer is to draw a remote ONE STEP BEHIND, between the tile it left and the tile it is on now, rather
    // than to guess the tile it is heading for. A guess is wrong every time a walk turns a corner or ends, and it
    // is wrong in the worst way: the avatar is drawn walking onto ground the player never walked onto, and then
    // has to be snatched back. The cost of the honest answer is one step of extra latency on top of the
    // interpolation delay, which is the same trade the delay itself already makes.
    void SampleRemote(Entity e, ref NetId id)
    {
        long netId = id.Value;
        if (netId == LocalNetId) return;
        if (!World.TryGet(e, out TileMoveState now)) return;
        liveRemotes.Add(netId);

        if (!remoteSamples.TryGetValue(netId, out RemoteSample prev))
        {
            // First sight of this remote. It draws on its tile centre until it is seen to leave it, because there
            // is no earlier tile to have come from.
            remoteSamples[netId] = new RemoteSample(Standing(now), now.Tile, sampleTime);
            return;
        }

        if (now.Tile != prev.Tile)
        {
            // A step committed, and only now is the tile it went to a fact. The glide starts over from the tile
            // the remote just left, at whatever step progress the new state carries, which is zero on the commit
            // tick, so the drawn position is continuous with where the previous glide finished.
            remoteSamples[netId] = new RemoteSample(GlideFrom(prev.Tile, now), now.Tile, sampleTime);
            return;
        }

        // Same tile as last time. Step progress that ADVANCED carries the glide forward. Progress that fell back to
        // zero without the tile changing is the route ending or a re-path around a blocker, and re-stamping the
        // glide there would drag the remote back to the tile the last step started on. Left alone, the glide runs
        // out against its own clamp and parks on the tile the remote is standing on.
        if (now.StepTicks <= prev.Glide.StepTicks)
        {
            // A turn on the spot still has to reach the screen. TileMoveSimulator.BeginInteract sets Facing with NO
            // tile change and no step progress for a zero-step interact, which is the ordinary click on the thing
            // you are already standing next to, so a receiver that only resampled on movement would draw that
            // player facing their last step until they next walked. The glide's tile pair and its stamp are kept,
            // so the facing turns and the motion does not restart.
            if (now.Facing != prev.Glide.Facing)
            {
                TileMoveState turned = prev.Glide;
                turned.Facing = now.Facing;
                remoteSamples[netId] = new RemoteSample(turned, prev.Tile, prev.At);
            }
            return;
        }

        TileMoveState glide = prev.Glide;
        glide.StepTicks = now.StepTicks;
        glide.StepTotal = now.StepTotal;
        glide.Facing = now.Facing;
        remoteSamples[netId] = new RemoteSample(glide, now.Tile, sampleTime);
    }

    // A one-step route from the tile just left to the tile just reached, which is the shape TilePresenter glides
    // along. A pair that is not one step apart is not a step at all: a teleport, a plane change, or a remote that
    // left the area of interest and came back somewhere else. Those CUT, because sliding an avatar across the
    // distance between them would draw it walking over every tile in between.
    static TileMoveState GlideFrom(TileCoord from, in TileMoveState now)
    {
        // The step measurement is in LONG for the reason TileWorldServer.GoalInRange is: ReadMove bounds every
        // replicated field except a tile's X and Z, so two snapshots placing one remote int.MinValue apart would
        // make this subtraction int.MinValue and Math.Abs would throw out of SampleRemote, out of
        // RefreshRemoteSamples, and out of the CLIENT'S RENDER LOOP. That is exactly the failure the reader's own
        // doc promises a corrupt frame can never cause.
        long dx = (long)now.Tile.X - from.X, dz = (long)now.Tile.Z - from.Z;
        if (from.Plane != now.Tile.Plane || Math.Max(Math.Abs(dx), Math.Abs(dz)) != 1)
            return Standing(now);
        TileMoveState s = now;
        s.Tile = from;
        s.Route = new TileRoute(new[] { now.Tile }, 0);
        return s;
    }

    // A remote drawn on its tile centre. The replicated state already arrives with an idle route (the codec never
    // writes one), and this says so rather than relying on it.
    static TileMoveState Standing(in TileMoveState now)
    {
        TileMoveState s = now;
        s.Route = TileRoute.None;
        return s;
    }

    /// <summary>
    /// One remote's drawing state. <paramref name="Glide"/> is what <see cref="TilePresenter.Pose"/> is handed: a
    /// synthetic state standing on the tile the remote LEFT, carrying a one-step route to the tile it is on now.
    /// <paramref name="Tile"/> is the raw replicated tile the next frame compares against, which the glide itself
    /// cannot answer because it stands on the PREVIOUS one. The step counter the next frame compares against needs
    /// no field beside it: every site here copies the replicated one into the glide, so <c>Glide.StepTicks</c> IS
    /// that value. <paramref name="At"/> is the render-timeline instant the glide was stamped at, which is what
    /// the fraction of a tick since then is measured from.
    /// </summary>
    readonly record struct RemoteSample(TileMoveState Glide, TileCoord Tile, double At);
}
