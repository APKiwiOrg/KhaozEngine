using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Diagnostics;
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

    /// <summary>Auto-reconnect on a mid-session drop (honored only when the factory ctor is used). Default true:
    /// the client rebuilds the transport, resumes the same token, and re-syncs without a manual rebuild.</summary>
    public bool AutoReconnect { get; init; } = true;

    /// <summary>Keep retrying even after a token rejection. Default false: a rejected token is terminal (it will not
    /// fix itself), surfaced as <see cref="DisconnectReason.RejectedToken"/>.</summary>
    public bool RetryOnReject { get; init; } = false;

    /// <summary>Backoff schedule for auto-reconnect.</summary>
    public ReconnectBackoff Reconnect { get; init; } = ReconnectBackoff.Default;

    /// <summary>Opt-in connect-time version handshake. When set, the client prepends this protocol/build version to
    /// its connect token (see <see cref="ProtocolHandshake.WrapToken"/>); a server running a
    /// <see cref="VersionCheckingAuthenticator"/> compares it and, on mismatch, rejects cleanly - surfaced here as
    /// <see cref="DisconnectReason.IncompatibleVersion"/> (the required version in
    /// <see cref="WorldClient.DisconnectReasonDetail"/>) instead of the client proceeding to receive snapshots it
    /// cannot decode. Null (default) = no version sent: byte-identical to the pre-handshake wire, and a
    /// version-checking server treats it as a legacy/unknown-version client.</summary>
    public string? ProtocolVersion { get; init; }
}

/// <summary>
/// Client glue over the shipped netcode: wraps a <see cref="NetClient"/> session, a
/// <see cref="ClientReplicationView"/> for remote entities, and <see cref="ClientPrediction{TState,TCommand}"/>
/// for the local avatar. Per frame the sample <see cref="Poll"/>s (ingests AoI snapshots; reconciles the local
/// player against the authoritative basis), <see cref="SendInput"/>s once per tick (predicts + transmits), and
/// reads <see cref="Snapshot"/> to render a capsule per entity (local predicted, remotes replicated). Render-free.
/// </summary>
public sealed class WorldClient : IDisposable
{
    private NetClient net;
    private World world = new();
    private readonly ReplicationRegistry registry;
    private ClientReplicationView view;
    private readonly ClientPrediction<PlayerMoveState, MoveCommand> prediction;
    private int authoritativeTick;
    // True once prediction has been seeded by the first snapshot of the whole session. Distinguishes the genuine
    // initial connect (seed from seq 0) from the first snapshot of a RECONNECT (re-seed but keep the command seq
    // counter monotonic - see ClientPrediction.Reseed). Never reset by StartAttempt, so every post-reconnect first
    // snapshot takes the reconnect path.
    private bool seededPredictionOnce;
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

    // Reconnect fields
    private readonly Func<INetTransport>? connectFactory;   // null = single-shot instance path
    private INetTransport? ownedTransport;                  // disposed by us when factory-built
    private readonly byte[]? token;
    private readonly bool autoReconnect;
    private readonly bool retryOnReject;
    private readonly ReconnectBackoff backoff;
    private int attempt;                 // 0 while initial-connecting or connected; 1.. while reconnecting
    private bool awaitingBackoff;        // true while waiting out the inter-attempt delay
    private float retryWaitRemaining;    // backoff countdown
    private float attemptDeadlineRemaining;  // current live attempt's join deadline

    // --- NetStats: a diagnostics-only snapshot of connection health, surfaced via NetStats. Rates are computed
    // over a rolling ~1s window driven by AdvancePresentation(dt) (the canonical per-frame call); snapshot count and
    // correction magnitude are captured at ingest in Poll/OnSnapshot. Nothing here affects simulation.
    private const float StatsWindowSeconds = 1f;
    private float statsElapsed;                        // seconds accumulated in the current window
    private int snapshotsSinceWindow;                  // AoI snapshots applied since the window opened
    private bool statsBaselineSet;                     // whether the byte-counter baseline has been captured
    private long bytesInBaseline;
    private long bytesOutBaseline;
    private float snapshotsPerSec;                     // last completed window's rates (reported by NetStats)
    private float bytesInPerSec;
    private float bytesOutPerSec;
    private float lastCorrection;                      // magnitude of the most recent reconciliation correction (m)
    private readonly float[] correctionRing = new float[64];
    private float correctionSum;
    private int correctionCount;
    private int correctionHead;

    /// <summary>Single-shot client over a caller-owned transport: no auto-reconnect (a drop is terminal, observable
    /// via <see cref="ConnectionState"/> + <see cref="DisconnectReason"/>). The caller owns disposing the transport.</summary>
    public WorldClient(INetTransport transport, Func<float, float, float> groundHeight, MoveTuning tuning,
        WorldClientConfig? config = null, byte[]? token = null, Func<float, float, Vector3>? groundNormal = null,
        WorldBounds? bounds = null, IPhysicsWorld? physics = null, ReplicationRegistry? registry = null)
        : this(connectFactory: null, transport, groundHeight, tuning, config, token, groundNormal, bounds, physics, registry)
    {
        ArgumentNullException.ThrowIfNull(transport);
    }

    /// <summary>Reconnect-capable client: <paramref name="connect"/> is invoked once for the initial connection and
    /// again per reconnect attempt (rebuilding the transport + session), resuming the same <paramref name="token"/>.
    /// Auto-reconnect is on by default (see <see cref="WorldClientConfig.AutoReconnect"/>). This client owns and
    /// disposes the transports it builds; dispose the client to close the current one.</summary>
    public WorldClient(Func<INetTransport> connect, Func<float, float, float> groundHeight, MoveTuning tuning,
        WorldClientConfig? config = null, byte[]? token = null, Func<float, float, Vector3>? groundNormal = null,
        WorldBounds? bounds = null, IPhysicsWorld? physics = null, ReplicationRegistry? registry = null)
        : this(connect ?? throw new ArgumentNullException(nameof(connect)), connect(), groundHeight, tuning, config,
               token, groundNormal, bounds, physics, registry)
    {
    }

    private WorldClient(Func<INetTransport>? connectFactory, INetTransport transport,
        Func<float, float, float> groundHeight, MoveTuning tuning, WorldClientConfig? config, byte[]? token,
        Func<float, float, Vector3>? groundNormal, WorldBounds? bounds, IPhysicsWorld? physics,
        ReplicationRegistry? registry)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));
        config ??= new WorldClientConfig();
        this.connectFactory = connectFactory;
        // Consumer-injectable registry: build it with MoveProtocol.CreateRegistry(configure) — the SAME registry the
        // server uses — so the client decodes the game's extension components. Default = movement-only.
        this.registry = registry ?? MoveProtocol.CreateRegistry();
        // Opt-in version handshake: wrap the token with the protocol version so a version-checking server can gate
        // the connect. Store the wrapped form so each reconnect attempt resends it. Null = unwrapped (legacy wire).
        this.token = config.ProtocolVersion is null ? token : ProtocolHandshake.WrapToken(config.ProtocolVersion, token);
        ownedTransport = connectFactory is not null ? transport : null;   // we dispose only what we built
        net = new NetClient(transport, this.token);
        view = new ClientReplicationView(this.registry);
        // Predict against the SAME physics world the server is authoritative over (mirrors WorldServer),
        // so a solid-prop consumer predicts straight rather than rubber-banding. Defaults null = terrain-only.
        var simulator = new PlayerMoveSimulator(groundHeight, tuning, groundNormal, bounds, physics);
        PredictionSettings settings = config.Prediction ?? (PredictionSettings.Default with { TickSeconds = config.TickSeconds });
        prediction = new ClientPrediction<PlayerMoveState, MoveCommand>(simulator, settings);
        interpolateRemotes = config.InterpolateRemotes;
        tickSeconds = config.TickSeconds;
        snapshotInterval = config.TickSeconds;        // server sends at the sim tick today; refined by measurement
        disconnectTimeout = config.DisconnectTimeoutSeconds;
        autoReconnect = config.AutoReconnect;
        retryOnReject = config.RetryOnReject;
        backoff = config.Reconnect;
        attemptDeadlineRemaining = disconnectTimeout;   // also bound the initial connect
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

    /// <summary>Number of the in-flight reconnect attempt (0 while connected or on the initial connect). Render
    /// "reconnecting (attempt N)..." from this and <see cref="SecondsUntilNextRetry"/>.</summary>
    public int ReconnectAttempt => attempt;

    /// <summary>Seconds until the next reconnect attempt fires while waiting out backoff; 0 otherwise.</summary>
    public float SecondsUntilNextRetry => awaitingBackoff ? MathF.Max(0f, retryWaitRemaining) : 0f;

    private void SetState(WorldConnectionState next)
    {
        if (state == next) return;
        state = next;
        ConnectionStateChanged?.Invoke(next);
    }

    /// <summary>Raised when the server pushes a <see cref="ServerNotice"/> (e.g. a maintenance/restart warning).</summary>
    public event Action<ServerNotice>? NoticeReceived;

    /// <summary>Raised when an incoming snapshot could not be decoded (e.g. an unregistered component type id from a
    /// newer server protocol). The client disconnects with <see cref="DisconnectReason.IncompatibleVersion"/>; the
    /// argument is the decode error. A consumer can subscribe to log it or show "client out of date, please update"
    /// without polling <see cref="DisconnectReason"/>.</summary>
    public event Action<string>? SnapshotDecodeFailed;

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

    /// <summary>The local player's predicted horizontal (planar ground-plane) speed in m/s, taken from the latest
    /// prediction tick (the commanded, collision-clamped move). Use it to drive a speed HUD, footstep audio, or a
    /// locomotion blend: it is the clean source that stays steady under lag, unlike differencing
    /// <see cref="LocalRenderState"/>.Position, which carries the decaying reconciliation render offset and so wobbles
    /// during a steady run. Zero until the first snapshot seeds prediction.</summary>
    public float LocalHorizontalSpeed => prediction.PredictedHorizontalSpeed;

    /// <summary>
    /// A read-only snapshot of this client's connection health for a diagnostics/telemetry overlay: RTT, packet
    /// loss, and byte rates (from the transport - 0 over loopback), the AoI snapshot ingest rate, and the
    /// prediction-reconciliation correction magnitude (last + rolling average). <see cref="ClientNetStats.Connected"/>
    /// tracks <see cref="Joined"/>. Rates refresh once per ~1s window as <see cref="AdvancePresentation"/> is pumped;
    /// reading this never mutates state.
    /// </summary>
    public ClientNetStats NetStats
    {
        get
        {
            NetTransportStats t = net.TransportStats;
            return new ClientNetStats
            {
                Connected = Joined,
                RttMs = t.RttMs,
                PacketLoss = t.PacketLoss,
                BytesInPerSec = bytesInPerSec,
                BytesOutPerSec = bytesOutPerSec,
                SnapshotsPerSec = snapshotsPerSec,
                LastCorrectionMeters = lastCorrection,
                AvgCorrectionMeters = correctionCount > 0 ? correctionSum / correctionCount : 0f,
            };
        }
    }

    /// <summary>Pumps the session: ingests AoI snapshots, applies remote replication, reconciles the local avatar.
    /// Pass <paramref name="dt"/> (seconds elapsed since last call) to drive the snapshot-starvation detector and
    /// reconnect backoff timer. After <see cref="WorldClientConfig.DisconnectTimeoutSeconds"/> with no server frame
    /// the state transitions to <see cref="WorldConnectionState.Reconnecting"/> (factory ctor) or
    /// <see cref="WorldConnectionState.Disconnected"/> (instance ctor), with the appropriate
    /// <see cref="DisconnectReason"/>. The detector only advances when dt &gt; 0; callers that pass 0 (the default)
    /// disable it for that call.</summary>
    public void Poll(float dt = 0f)
    {
        // Waiting out a backoff delay between attempts: count down, then start the next attempt.
        if (awaitingBackoff)
        {
            retryWaitRemaining -= dt;
            if (retryWaitRemaining > 0f) return;
            StartAttempt();
        }

        net.Poll();
        bool gotFrame = false;
        while (net.TryDequeueEvent(out ClientSessionEvent ev))
        {
            switch (ev.Kind)
            {
                case ClientSessionEventKind.Joined:
                    disconnectReason = DisconnectReason.None;
                    disconnectReasonDetail = string.Empty;
                    sawShutdownNotice = false;
                    attempt = 0;
                    secondsSinceServerFrame = 0f;
                    SetState(WorldConnectionState.Connected);
                    break;
                case ClientSessionEventKind.Data:
                    gotFrame = true;
                    OnServerFrame(ev.Data);
                    break;
                case ClientSessionEventKind.Rejected:
                    if (ProtocolHandshake.TryParseIncompatibleReason(ev.RejectReason, out string requiredVersion))
                    {
                        // Version handshake rejected us: terminal (retrying the same build will keep failing).
                        disconnectReason = DisconnectReason.IncompatibleVersion;
                        disconnectReasonDetail = requiredVersion;
                        FailAttempt(allowReconnect: false);
                    }
                    else
                    {
                        disconnectReason = DisconnectReason.RejectedToken;
                        disconnectReasonDetail = ev.RejectReason;
                        FailAttempt(allowReconnect: retryOnReject);
                    }
                    break;
                case ClientSessionEventKind.Disconnected:
                    if (state != WorldConnectionState.Disconnected)
                    {
                        disconnectReason = sawShutdownNotice ? DisconnectReason.ServerShutdown : DisconnectReason.Unreachable;
                        FailAttempt(allowReconnect: true);
                    }
                    break;
            }
        }
        if (gotFrame) secondsSinceServerFrame = 0f;
        if (dt <= 0f) return;

        if (state == WorldConnectionState.Connected)
        {
            secondsSinceServerFrame += dt;
            if (secondsSinceServerFrame >= disconnectTimeout)
            {
                disconnectReason = DisconnectReason.Timeout;
                FailAttempt(allowReconnect: true);
            }
        }
        else if (!awaitingBackoff && state != WorldConnectionState.Disconnected)
        {
            // A live attempt (initial Connecting or a reconnect attempt) that never joins: enforce a join deadline
            // so a down server (no transport-drop event over loopback) still fails the attempt and backs off.
            attemptDeadlineRemaining -= dt;
            if (attemptDeadlineRemaining <= 0f)
            {
                if (disconnectReason == DisconnectReason.None) disconnectReason = DisconnectReason.Timeout;
                FailAttempt(allowReconnect: true);
            }
        }
    }

    private bool CanReconnect => connectFactory is not null && autoReconnect;

    // A live attempt failed (drop, reject, or join-deadline). Either schedule another attempt or go terminal.
    private void FailAttempt(bool allowReconnect)
    {
        if (allowReconnect && CanReconnect && (backoff.MaxAttempts == 0 || attempt < backoff.MaxAttempts))
        {
            attempt = Math.Max(1, attempt + 1);
            retryWaitRemaining = backoff.DelayForAttempt(attempt);
            awaitingBackoff = true;
            SetState(WorldConnectionState.Reconnecting);
        }
        else
        {
            awaitingBackoff = false;
            SetState(WorldConnectionState.Disconnected);
        }
    }

    // Build a fresh transport + session for the next attempt, keeping prediction; drop stale replicated entities.
    private void StartAttempt()
    {
        awaitingBackoff = false;
        DisposeCurrentTransport();
        INetTransport transport = connectFactory!();
        ownedTransport = transport;
        net = new NetClient(transport, token);
        world = new World();
        view = new ClientReplicationView(registry);
        LocalNetId = -1;
        secondsSinceServerFrame = 0f;
        attemptDeadlineRemaining = disconnectTimeout;
        // Reset remote-interpolation bookkeeping so the new stream starts clean.
        snapshotInterval = tickSeconds;
        secondsSinceSnapshot = 0f;
        sawFirstSnapshot = false;
        // state stays Reconnecting until this attempt's Joined.
    }

    private void DisposeCurrentTransport()
    {
        if (connectFactory is not null) ownedTransport?.Dispose();
        ownedTransport = null;
    }

    /// <summary>Closes the current transport (if this client built it). Idempotent.</summary>
    public void Dispose()
    {
        DisposeCurrentTransport();
        SetState(WorldConnectionState.Disconnected);
    }

    /// <summary>Predicts one command forward and transmits it. Returns the assigned seq, or <c>-1</c> when the
    /// session is not <see cref="WorldConnectionState.Connected"/> (nothing is predicted or sent). A game loop that
    /// calls this every frame regardless of state is safe: input produced during a (possibly minutes-long)
    /// auto-reconnect outage is dropped here rather than predicted forward and queued. Without this gate the
    /// prediction sequence inflated and the predicted avatar marched away from authority for the whole outage, so on
    /// rejoin the player was frozen/vibrating (driven by stale input) until the backlog drained - the relaunch-fixes-it
    /// reconnect bug. The transports also drop sends while disconnected, so this is the engine-level guarantee that no
    /// consumer's send loop can build that backlog, complementing the server-side catch-up cap
    /// (<see cref="WorldServerConfig.MaxInputBacklog"/>).</summary>
    public int SendInput(in MoveCommand cmd)
    {
        if (state != WorldConnectionState.Connected) return -1;
        int seq = prediction.Predict(cmd);
        net.Send(MoveProtocol.EncodeMove(seq, cmd), NetChannelReliability.ReliableOrdered);
        return seq;
    }

    /// <summary>Asks the authoritative server to move THIS player to a server-decided safe position - a
    /// "return to spawn" / "unstuck". Fire-and-forget over the reliable channel; the result reconciles back exactly
    /// like an admin teleport. The client never names the destination (that would be a teleport-anywhere cheat): the
    /// server owns it and rate-limits it (see <see cref="WorldServerConfig.SelfRescueDestination"/> /
    /// <see cref="WorldServerConfig.SelfRescueCooldownSeconds"/>, mirrored on <see cref="ShardedWorldServerConfig"/>),
    /// and the feature is off until the server configures a destination. Returns false (and sends nothing) when not
    /// connected.</summary>
    public bool RequestSelfRescue()
    {
        if (state != WorldConnectionState.Connected) return false;
        net.Send(MoveProtocol.EncodeClientControl(MoveProtocol.ClientControlKind.SelfRescue), NetChannelReliability.ReliableOrdered);
        return true;
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
        UpdateNetStatsWindow(dt);
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

    /// <summary>
    /// Reads a replicated component off the entity with network id <paramref name="netId"/> (the id carried by
    /// <see cref="EntityRenderState.Id"/>). Returns <c>true</c> and the component when the entity is currently in
    /// this client's area of interest AND carries a <typeparamref name="T"/> whose type id both ends registered;
    /// <c>false</c> otherwise (unknown id, entity not in view, or the component absent). This is how a game reads a
    /// server-assigned discriminator per entity — an NPC kind, HP, faction — to pick a model or drive behaviour,
    /// registered on the shared <see cref="ReplicationRegistry"/> (see
    /// <see cref="MoveProtocol.CreateRegistry(System.Action{ReplicationRegistry})"/>). Version-skew-safe: against an
    /// OLDER server that never sends <typeparamref name="T"/>, this simply returns <c>false</c> — no handshake, no
    /// disconnect. Reflects the last applied snapshot; call it after <see cref="Poll"/>.
    /// </summary>
    public bool TryGetComponent<T>(int netId, out T component) where T : struct, IComponent
    {
        if (view.TryGetEntity(netId, out Entity e) && world.IsAlive(e) && world.TryGet(e, out component))
            return true;
        component = default;
        return false;
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
                ServerNotice notice = MoveProtocol.DecodeNotice(payload);
                if (notice.Kind == ServerNoticeKind.Shutdown) sawShutdownNotice = true;
                LastNotice = notice;
                NoticeReceived?.Invoke(notice);
                break;
        }
    }

    private void OnSnapshot(byte[] data)
    {
        if (!MoveProtocol.TryDecodeSnapshotFrame(data, out int localNetId, out int ackSeq, out byte[] snapshot)) return;
        // Last-resort backstop: a snapshot we can't decode (e.g. an unregistered component type id from a newer
        // server protocol) must become a clean disconnect, never an unhandled exception in the consumer's frame
        // loop. Surfaced as IncompatibleVersion so the consumer shows "client out of date, please update".
        if (!view.TryApply(world, snapshot, out string? decodeError))
        {
            OnSnapshotDecodeFailed(decodeError);
            return;
        }
        snapshotsSinceWindow++;                                  // NetStats: AoI snapshot ingest rate
        bool first = LocalNetId < 0;
        LocalNetId = localNetId;

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
            if (first)
            {
                // First snapshot of the genuine initial connect: seed prediction at the authoritative spawn from
                // seq 0 (client + server both start fresh). First snapshot of a RECONNECT (we have seeded before):
                // re-seed the predicted state but keep the seq counter monotonic - the fresh server already advanced
                // its ack from the commands sent in the join gap, so zeroing the seq would get every post-reconnect
                // command rejected as stale and pin the avatar forever.
                if (seededPredictionOnce) prediction.Reseed(basis);
                else { prediction.Reset(basis); seededPredictionOnce = true; }
            }
            ReconciliationResult rr = prediction.Reconcile(authoritativeTick++, basis, ackSeq);
            RecordCorrection(rr.PositionError);                  // NetStats: predicted-vs-authoritative delta (m)
        }
    }

    // A snapshot could not be decoded: surface it as a clean, terminal disconnect (the client is out of date;
    // reconnecting to the same build would just fail the same way) rather than letting the throw escape into the
    // consumer's frame loop. Matches the connect-time handshake's DisconnectReason.IncompatibleVersion path.
    private void OnSnapshotDecodeFailed(string? error)
    {
        string detail = string.IsNullOrEmpty(error) ? "snapshot decode failed" : error!;
        disconnectReason = DisconnectReason.IncompatibleVersion;
        disconnectReasonDetail = detail;
        SnapshotDecodeFailed?.Invoke(detail);
        FailAttempt(allowReconnect: false);
    }

    // --- NetStats helpers (diagnostics only) ---

    /// <summary>Roll the byte/snapshot-rate window forward by <paramref name="dt"/>; recompute rates each ~1s.</summary>
    private void UpdateNetStatsWindow(float dt)
    {
        if (dt > 0f) statsElapsed += dt;

        NetTransportStats t = net.TransportStats;
        if (!statsBaselineSet)
        {
            bytesInBaseline = t.BytesReceivedTotal;
            bytesOutBaseline = t.BytesSentTotal;
            statsBaselineSet = true;
        }

        if (statsElapsed >= StatsWindowSeconds)
        {
            snapshotsPerSec = snapshotsSinceWindow / statsElapsed;
            bytesInPerSec = (t.BytesReceivedTotal - bytesInBaseline) / statsElapsed;
            bytesOutPerSec = (t.BytesSentTotal - bytesOutBaseline) / statsElapsed;
            statsElapsed = 0f;
            snapshotsSinceWindow = 0;
            bytesInBaseline = t.BytesReceivedTotal;
            bytesOutBaseline = t.BytesSentTotal;
        }
    }

    /// <summary>Record one reconciliation correction magnitude into the last-value + rolling-average buffer.</summary>
    private void RecordCorrection(float meters)
    {
        if (float.IsNaN(meters) || float.IsInfinity(meters) || meters < 0f) return;
        lastCorrection = meters;
        if (correctionCount < correctionRing.Length)
        {
            correctionRing[correctionHead] = meters;
            correctionSum += meters;
            correctionCount++;
        }
        else
        {
            correctionSum += meters - correctionRing[correctionHead];
            correctionRing[correctionHead] = meters;
        }
        correctionHead = (correctionHead + 1) % correctionRing.Length;
    }
}
