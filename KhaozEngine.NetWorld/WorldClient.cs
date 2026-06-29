using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

/// <summary>Tunables for <see cref="WorldClient"/>.</summary>
public sealed class WorldClientConfig
{
    /// <summary>Fixed client prediction tick, seconds. Must match the server tick for clean reconciliation.</summary>
    public float TickSeconds { get; init; } = 1f / 30f;
    /// <summary>Override prediction settings; defaults to <see cref="PredictionSettings.Default"/> at <see cref="TickSeconds"/>.</summary>
    public PredictionSettings? Prediction { get; init; }

    /// <summary>Smooth remote players between the discrete (~tick-rate) replicated snapshots by interpolating their
    /// positions at render time (one snapshot of interpolation delay), instead of teleporting one snapshot-step per
    /// ingest. Default <c>true</c>: every remote glides, at the cost of ~one tick of remote render latency. Set
    /// <c>false</c> to restore the raw-latest-position behaviour (no delay, but steppy remotes).</summary>
    public bool InterpolateRemotes { get; init; } = true;

    /// <summary>Mid-session disconnect detector: declare the session lost after this many seconds with no server
    /// snapshot (only advances when <see cref="WorldClient.Poll(float)"/> is called with dt &gt; 0). Default 3s.</summary>
    public float DisconnectTimeoutSeconds { get; init; } = 3f;
}

/// <summary>
/// Client glue over the shipped netcode: wraps a <see cref="NetClient"/> session, a
/// <see cref="ClientReplicationView"/> for remote entities, and <see cref="ClientPrediction{TState,TCommand}"/>
/// for the local avatar. Per frame the sample <see cref="Poll"/>s (ingests AoI snapshots; reconciles the local
/// player against the authoritative basis), <see cref="SendInput"/>s once per tick (predicts + transmits), and
/// reads <see cref="Snapshot"/> to render a capsule per entity (local predicted, remotes replicated). Render-free.
/// </summary>
public sealed class WorldClient
{
    private readonly NetClient net;
    private readonly World world = new();
    private readonly ReplicationRegistry registry = MoveProtocol.CreateRegistry();
    private readonly ClientReplicationView view;
    private readonly ClientPrediction<PlayerMoveState, MoveCommand> prediction;
    private int authoritativeTick;
    private WorldConnectionState state = WorldConnectionState.Connecting;
    private DisconnectReason disconnectReason = DisconnectReason.None;
    private string disconnectReasonDetail = string.Empty;
    private readonly float disconnectTimeout;
    private float secondsSinceServerFrame;
    private bool sawShutdownNotice;

    // Remote interpolation: render replicated remotes ~one snapshot in the past, lerping between the last two
    // snapshots so they glide instead of teleporting one snapshot-step per ingest. The drive is a presentation
    // clock (mirrors ClientPrediction.AdvancePresentation, but for remotes) that resets on each snapshot and ramps
    // alpha 0..1 across one estimated inter-snapshot interval, feeding ClientReplicationView.Interpolate.
    private const float IntervalSmoothing = 0.1f;     // EMA weight on each freshly measured inter-snapshot interval
    private readonly bool interpolateRemotes;
    private readonly float tickSeconds;
    private float snapshotInterval;                   // estimated seconds between snapshots (seeded to the tick)
    private float secondsSinceSnapshot;               // presentation clock since the last applied snapshot
    private bool sawFirstSnapshot;                    // gate the interval EMA until there is a real interval to measure

    public WorldClient(INetTransport transport, Func<float, float, float> groundHeight, MoveTuning tuning,
        WorldClientConfig? config = null, byte[]? token = null, Func<float, float, Vector3>? groundNormal = null,
        WorldBounds? bounds = null, IPhysicsWorld? physics = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));
        config ??= new WorldClientConfig();
        net = new NetClient(transport, token);
        view = new ClientReplicationView(registry);
        // Predict against the SAME physics world the server is authoritative over (mirrors WorldServer),
        // so a solid-prop consumer predicts straight rather than rubber-banding. Defaults null = terrain-only.
        var simulator = new PlayerMoveSimulator(groundHeight, tuning, groundNormal, bounds, physics);
        PredictionSettings settings = config.Prediction ?? (PredictionSettings.Default with { TickSeconds = config.TickSeconds });
        prediction = new ClientPrediction<PlayerMoveState, MoveCommand>(simulator, settings);
        interpolateRemotes = config.InterpolateRemotes;
        tickSeconds = config.TickSeconds;
        snapshotInterval = config.TickSeconds;        // server sends at the sim tick today; refined by measurement
        disconnectTimeout = config.DisconnectTimeoutSeconds;
    }

    /// <summary>Net id of the local player, or -1 until the first snapshot identifies it.</summary>
    public int LocalNetId { get; private set; } = -1;

    /// <summary>True once the session handshake has joined (equivalent to
    /// <see cref="ConnectionState"/> == <see cref="WorldConnectionState.Connected"/>).</summary>
    public bool Joined => state == WorldConnectionState.Connected;

    /// <summary>The live connection state. Observe transitions via <see cref="ConnectionStateChanged"/>.</summary>
    public WorldConnectionState ConnectionState => state;

    /// <summary>Raised on every <see cref="ConnectionState"/> transition (new state passed).</summary>
    public event Action<WorldConnectionState>? ConnectionStateChanged;

    /// <summary>Why the session was lost (or could not be established); <see cref="DisconnectReason.None"/> while healthy.</summary>
    public DisconnectReason DisconnectReason => disconnectReason;

    /// <summary>Extra detail for the reason (the authenticator's reject string for
    /// <see cref="DisconnectReason.RejectedToken"/>); empty otherwise.</summary>
    public string DisconnectReasonDetail => disconnectReasonDetail;

    private void SetState(WorldConnectionState next)
    {
        if (state == next) return;
        state = next;
        ConnectionStateChanged?.Invoke(next);
    }

    /// <summary>Raised when the server pushes a <see cref="ServerNotice"/> (e.g. a maintenance/restart warning).</summary>
    public event Action<ServerNotice>? NoticeReceived;

    /// <summary>The most recent <see cref="ServerNotice"/> received, or null if none. Lets a consumer that attaches
    /// late, or polls instead of subscribing, still read the latest notice.</summary>
    public ServerNotice? LastNotice { get; private set; }

    /// <summary>The local player's full predicted/reconciled render state (position + vertical velocity + grounded).
    /// Exact movement the client already knows for its own avatar - use it to fill the local entity's
    /// <c>KhaozEngine.Game.CharacterSample</c> exact-movement fields (so a replicated-animator bridge reads true air
    /// state instead of finite-differencing position). Defaults (grounded false, zero velocity) until the first
    /// snapshot seeds prediction.</summary>
    public PlayerMoveState LocalRenderState => prediction.RenderedState;

    /// <summary>The local player's predicted grounded flag (shorthand for <see cref="LocalRenderState"/>.Grounded).</summary>
    public bool LocalGrounded => prediction.RenderedState.Grounded;

    /// <summary>The local player's predicted vertical velocity, m/s positive up (shorthand for
    /// <see cref="LocalRenderState"/>.VerticalVelocity).</summary>
    public float LocalVerticalVelocity => prediction.RenderedState.VerticalVelocity;

    /// <summary>Pumps the session: ingests AoI snapshots, applies remote replication, reconciles the local avatar.
    /// Pass <paramref name="dt"/> (seconds elapsed since last call) to drive the snapshot-starvation detector - after
    /// <see cref="WorldClientConfig.DisconnectTimeoutSeconds"/> with no server frame the state transitions to
    /// <see cref="WorldConnectionState.Disconnected"/> with <see cref="DisconnectReason.Timeout"/>. The detector only
    /// advances when dt &gt; 0; callers that pass 0 (the default) disable it for that call.</summary>
    public void Poll(float dt = 0f)
    {
        net.Poll();
        bool gotFrame = false;
        while (net.TryDequeueEvent(out ClientSessionEvent ev))
        {
            switch (ev.Kind)
            {
                case ClientSessionEventKind.Joined:
                    disconnectReason = DisconnectReason.None;
                    disconnectReasonDetail = string.Empty;
                    secondsSinceServerFrame = 0f;
                    SetState(WorldConnectionState.Connected);
                    break;
                case ClientSessionEventKind.Data:
                    gotFrame = true;
                    OnServerFrame(ev.Data);
                    break;
                case ClientSessionEventKind.Rejected:
                    disconnectReason = DisconnectReason.RejectedToken;
                    disconnectReasonDetail = ev.RejectReason;
                    SetState(WorldConnectionState.Disconnected);
                    break;
                case ClientSessionEventKind.Disconnected:
                    if (state != WorldConnectionState.Disconnected)
                    {
                        disconnectReason = sawShutdownNotice ? DisconnectReason.ServerShutdown : DisconnectReason.Unreachable;
                        SetState(WorldConnectionState.Disconnected);
                    }
                    break;
            }
        }
        if (gotFrame) secondsSinceServerFrame = 0f;

        if (state == WorldConnectionState.Connected && dt > 0f)
        {
            secondsSinceServerFrame += dt;
            if (secondsSinceServerFrame >= disconnectTimeout)
            {
                disconnectReason = DisconnectReason.Timeout;
                SetState(WorldConnectionState.Disconnected);
            }
        }
    }

    /// <summary>Predicts one command forward and transmits it. Returns the assigned seq.</summary>
    public int SendInput(in MoveCommand cmd)
    {
        int seq = prediction.Predict(cmd);
        net.Send(MoveProtocol.EncodeMove(seq, cmd), NetChannelReliability.ReliableOrdered);
        return seq;
    }

    /// <summary>Advances render-time smoothing (call once per render frame): the local avatar's inter-tick
    /// prediction smoothing, plus - when <see cref="WorldClientConfig.InterpolateRemotes"/> is set - remote
    /// interpolation. Remotes ramp from the previous snapshot to the current one across one estimated inter-snapshot
    /// interval (alpha 0..1, clamped), so between the discrete ~tick-rate snapshots they glide rather than teleport.
    /// A late snapshot holds the remote at the current value (alpha pinned at 1, no extrapolation) until the next
    /// arrives. Idempotent within a frame: it rewrites the same lerped state, so multiple <see cref="Snapshot"/>
    /// reads per frame are consistent.</summary>
    public void AdvancePresentation(float dt)
    {
        prediction.AdvancePresentation(dt);                   // local avatar (unaffected by remote interpolation)
        if (!interpolateRemotes) return;
        secondsSinceSnapshot += dt;
        float alpha = snapshotInterval > 0f ? Math.Clamp(secondsSinceSnapshot / snapshotInterval, 0f, 1f) : 1f;
        view.Interpolate(world, alpha);                       // remotes only; the local entry renders from prediction
    }

    /// <summary>The current renderable set: local player predicted, remotes from the replicated position (smoothly
    /// interpolated between the last two snapshots when <see cref="WorldClientConfig.InterpolateRemotes"/> is set -
    /// the default - else the raw latest). Each entry also carries the exact grounded flag + vertical velocity
    /// (local: predicted; remote: replicated <see cref="MovementState"/>) so an animator bridge reads true air state
    /// instead of finite-differencing the terrain-following position.</summary>
    public IReadOnlyList<EntityRenderState> Snapshot()
    {
        var list = new List<EntityRenderState>();
        foreach (KeyValuePair<int, Entity> kv in view.Entities)
        {
            if (!world.IsAlive(kv.Value)) continue;
            bool isLocal = kv.Key == LocalNetId;
            Vector3 pos;
            bool grounded;
            float verticalVelocity;
            if (isLocal)
            {
                // The local avatar's exact air state is the predicted/reconciled state.
                PlayerMoveState rs = prediction.RenderedState;
                pos = rs.Position;
                grounded = rs.Grounded;
                verticalVelocity = rs.VerticalVelocity;
            }
            else
            {
                // Remotes: surface the replicated vertical state (MovementState rides alongside the position) so a
                // consumer animates jump/fall from the authoritative flag instead of finite-differencing the
                // terrain-following position (which reads "airborne" the faster a remote moves over a slope). Default
                // grounded when a remote has no MovementState yet, so it never spuriously starts airborne.
                world.TryGet(kv.Value, out ReplicatedPosition rp);
                pos = rp.Value;
                grounded = world.TryGet(kv.Value, out MovementState ms) ? ms.Grounded : true;
                verticalVelocity = ms.VerticalVelocity;
            }
            string? name = world.TryGet(kv.Value, out PlayerIdentity identity) ? identity.DisplayName : null;
            list.Add(new EntityRenderState(new NetId(kv.Key), pos, isLocal, name, grounded, verticalVelocity));
        }
        return list;
    }

    private void OnServerFrame(byte[] data)
    {
        if (!MoveProtocol.TryDecodeServerFrame(data, out MoveProtocol.ServerFrameKind kind, out byte[] payload)) return;
        switch (kind)
        {
            case MoveProtocol.ServerFrameKind.Snapshot:
                OnSnapshot(payload);
                break;
            case MoveProtocol.ServerFrameKind.Notice:
                ServerNotice notice = MoveProtocol.TryDecodeNotice(payload);
                if (notice.Kind == ServerNoticeKind.Shutdown) sawShutdownNotice = true;
                LastNotice = notice;
                NoticeReceived?.Invoke(notice);
                break;
        }
    }

    private void OnSnapshot(byte[] data)
    {
        if (!MoveProtocol.TryDecodeSnapshotFrame(data, out int localNetId, out int ackSeq, out byte[] snapshot)) return;
        bool first = LocalNetId < 0;
        LocalNetId = localNetId;
        view.Apply(world, snapshot);

        if (interpolateRemotes)
        {
            // Refine the inter-snapshot interval from the render time actually elapsed since the previous snapshot
            // (clamped to a sane band so jitter, a queued double-apply, or a dropped snapshot can't desync the ramp),
            // then restart the interpolation clock. Skip the very first snapshot: there is no prior interval yet.
            if (sawFirstSnapshot)
            {
                float measured = Math.Clamp(secondsSinceSnapshot, 0.5f * tickSeconds, 4f * tickSeconds);
                snapshotInterval += IntervalSmoothing * (measured - snapshotInterval);
            }
            sawFirstSnapshot = true;
            secondsSinceSnapshot = 0f;
        }

        // Reconcile reads the freshly applied 'current' here, BEFORE any AdvancePresentation interpolates the world,
        // so the local prediction basis is the true authoritative value, never an interpolated one.
        if (view.TryGetEntity(localNetId, out Entity local) && world.TryGet(local, out ReplicatedPosition p))
        {
            // Build the full authoritative basis from BOTH replicated components - position and the vertical axis
            // (MovementState) - so prediction replay reproduces the jump/fall, not just the XZ plane.
            world.TryGet(local, out MovementState ms);           // default (grounded, 0) until first replicated
            PlayerMoveState basis = PlayerMoveState.From(p.Value, ms);
            if (first) prediction.Reset(basis);                  // seed prediction at the authoritative spawn
            prediction.Reconcile(authoritativeTick++, basis, ackSeq);
        }
    }
}
