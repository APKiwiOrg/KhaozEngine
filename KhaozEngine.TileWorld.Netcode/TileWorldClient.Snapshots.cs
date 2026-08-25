using System;
using System.Collections.Generic;
using System.Numerics;
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
    // The captures RefreshRemoteBodies would otherwise close over, hoisted so the ECS callback can be cached
    // instead of rebuilt every frame. Live only for the duration of one refresh, which nothing re-enters.
    RefAction<NetId>? sampleRemotes;
    float sampleDt;

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
            localChase.SnapTo(LocalTarget);
            return;
        }

        ReconciliationResult result = Prediction.Reconcile((int)serverTick, basis, ackSeq);
        if (result.PositionError > CorrectionEpsilon) CorrectionCount++;
        if (result.HardSnapApplied) SnapCount++;
        // A CUT is not chased across. Both of these zero the prediction layer's own offsets and place the avatar
        // outright, so the chase has to land on the same frame or the body would slide across the ground between
        // the two places instead of appearing at the new one. Teleport and hard snap are BOTH taken, because they
        // are different questions and each can fire without the other: a teleport is an authoritative epoch
        // advance ("you are somewhere else now"), a hard snap is a step the two heads disagreed about, and a
        // reported seed teleport carries no snap at all. LocalTarget is the committed tile's centre and nothing
        // else (no correction offset is composed into it, see LocalPose), so snapping onto it draws the body
        // exactly where the rules just put it.
        if (result.HardSnapApplied || result.Teleported) localChase.SnapTo(LocalTarget);
        // Raised rather than folded into SnapCount, because the two say different things to a head: a snap is "you
        // mispredicted a step", a teleport is "you are somewhere else now". See the event's own doc.
        if (result.Teleported) Teleported?.Invoke();
    }

    /// <summary>
    /// Where a remote draws right now: its own <see cref="TileChase"/>, pursuing the tile the remote's replicated
    /// state is committed to. False for an unknown net id and for the local player, who is drawn through
    /// <see cref="LocalPose"/> instead.
    /// <para>The chase runs on the frame clock (see <see cref="AdvancePresentation"/>), so this is a plain read
    /// and calling it twice in a frame answers twice the same. The remote's own DELAY is unchanged and is not the
    /// chase's doing: a remote's state is read off the timeline
    /// <see cref="TileWorldClientConfig.InterpolationDelayTicks"/> holds behind live, so the divergence a viewer
    /// measures is the delay plus the chase's steady-state lag.</para>
    /// </summary>
    /// <param name="netId">The remote's net id.</param>
    /// <param name="pose">Where and which way to draw it.</param>
    /// <returns>True when this client is tracking <paramref name="netId"/> as a remote.</returns>
    public bool TryGetRemotePose(long netId, out TilePose pose)
    {
        pose = default;
        if (netId == LocalNetId || !remoteBodies.TryGetValue(netId, out RemoteBody? body)) return false;
        pose = Presenter.PoseAt(body.Chase.Drawn, body.State.Tile.Plane, body.State.Facing);
        return true;
    }

    // Rebuilds the per-remote draw states off whatever InterpolateAt just wrote into the world, and steps each
    // remote's chase by the frame's dt. Called every frame from AdvancePresentation rather than on snapshot
    // arrival, because the delayed timeline advances with the FRAME and a remote resampled once per packet hops at
    // the tick rate whatever the frame rate.
    void RefreshRemoteBodies(float dt)
    {
        sampleRemotes ??= SampleRemote;
        sampleDt = dt;
        liveRemotes.Clear();
        World.ForEach(sampleRemotes);
        // Every live remote was just written into the map, so equal counts means nothing went stale and the prune
        // can be skipped, which is the ordinary frame. A pruned remote takes its chase with it, so one that comes
        // back is a first sighting and CUTS onto its tile rather than sliding in from wherever it left.
        if (remoteBodies.Count == liveRemotes.Count) return;
        goneRemotes.Clear();
        foreach (long netId in remoteBodies.Keys)
            if (!liveRemotes.Contains(netId)) goneRemotes.Add(netId);
        for (int i = 0; i < goneRemotes.Count; i++) remoteBodies.Remove(goneRemotes[i]);
    }

    // One remote, one frame: decide whether the tile it now names is a STEP or a DISCONTINUITY, then advance its
    // chase onto that tile.
    //
    // The replicated state says where the body is outright, so there is nothing to reconstruct and nothing to
    // guess. It used to be otherwise: the everyone channel carried a tile and step progress and nothing about
    // where the step was GOING, so the only honest answer was to draw a remote ONE STEP BEHIND, at a whole step of
    // extra latency, with a second rule to tell a step from a teleport. Committing at the START of a step is what
    // made the destination a fact the server can simply send.
    //
    // The discontinuity test is the state type's own IsStepOrigin, which is the rule the wire decoder and
    // SetPlayerState are both held to: same plane, at most one Chebyshev step. So a teleport, a plane change and a
    // remote that left the interest set and came back somewhere else all CUT, and only a real step is pursued. The
    // epoch is taken as well, as a HIGH-WATER MARK for the reason ClientPrediction takes it that way: a server
    // teleport that happens to land one tile away is a cut, not a step.
    void SampleRemote(Entity e, ref NetId id)
    {
        long netId = id.Value;
        if (netId == LocalNetId) return;
        if (!World.TryGet(e, out TileMoveState now)) return;
        liveRemotes.Add(netId);

        Vector2 target = new(now.Tile.X, now.Tile.Z);
        if (!remoteBodies.TryGetValue(netId, out RemoteBody? body))
        {
            body = new RemoteBody(new TileChase(config.ChaseHalfLifeSeconds), now);
            remoteBodies[netId] = body;
            body.Chase.SnapTo(target);
        }
        else if (now.Epoch > body.Epoch || !TileMoveState.IsStepOrigin(body.State.Tile, now.Tile))
        {
            body.Chase.SnapTo(target);
        }
        body.State = now;
        if (now.Epoch > body.Epoch) body.Epoch = now.Epoch;
        body.Chase.Advance(target, sampleDt);
    }

    /// <summary>
    /// One remote's drawing state. <see cref="State"/> is the replicated state verbatim, which is where the tile,
    /// the plane and the facing come from. <see cref="Chase"/> is that remote's own pursuer, one per body, so two
    /// remotes never share a drawn position. <see cref="Epoch"/> is the teleport high-water mark, held here rather
    /// than read off <see cref="State"/> so a momentary dip to zero cannot read as a fresh advance on the way back
    /// up (see <c>ClientPrediction</c>, which holds it the same way and for the same reason).
    /// <para>A CLASS rather than a record struct, deliberately: the chase is stateful and is stepped in place
    /// every frame, and a struct would have it copied out of the dictionary, advanced, and thrown away.</para>
    /// </summary>
    sealed class RemoteBody(TileChase chase, TileMoveState state)
    {
        public readonly TileChase Chase = chase;
        public TileMoveState State = state;
        public uint Epoch = state.Epoch;
    }
}
