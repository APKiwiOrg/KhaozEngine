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

/// <summary>
/// Client glue over the shipped netcode: wraps a <see cref="NetClient"/> session, a
/// <see cref="ClientReplicationView"/> for remote entities, and <see cref="ClientPrediction{TState,TCommand}"/>
/// for the local avatar. Per frame the sample <see cref="Poll"/>s (ingests AoI snapshots; reconciles the local
/// player against the authoritative basis), <see cref="SendInput"/>s once per tick (predicts + transmits), and
/// reads <see cref="Snapshot"/> to render a capsule per entity (local predicted, remotes replicated). Render-free.
/// </summary>
public sealed partial class WorldClient : IDisposable
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

    // Remote interpolation: render replicated remotes on a FIXED delay (interpolationDelaySeconds behind the newest
    // received snapshot), lerping the two buffered snapshots bracketing that render time by their true timestamps
    // (ClientReplicationView.RecordInterpolationSample / InterpolateAt). A monotonic presentation clock advances by
    // the render dt in AdvancePresentation; snapshots are stamped with it at ingest. This decouples presentation from
    // both the tick cadence and the render fps - no phase drift, no per-tick holds/catch-up snaps - replacing the old
    // estimate-the-interval-and-ramp-alpha scheme (which pinned alpha at 1 and held/snapped at a non-integer ratio).
    private readonly bool interpolateRemotes;
    private readonly bool requestDeltaReplication;    // advertise DeltaCapable on join + ack applied delta seqs
    private readonly float tickSeconds;
    private readonly float interpolationDelaySeconds; // fixed remote render delay (InterpolationDelayTicks * tick)
    private double presentationClock;                 // monotonic render-time seconds (drives snapshot stamps + renderTime)

    // Remote teleport flush: the last replicated MovementState.TeleportEpoch seen per remote entity. A teleport is a
    // server-authoritative hard cut, so when a remote's epoch advances its interpolation buffer now straddles the jump
    // (a pre- and post-teleport sample); interpolating across that would streak the remote across the world. On an
    // advance we snap that remote's interpolation to the newest sample (ClientReplicationView.SnapInterpolationToNewest)
    // so observers cut too - matching the local prediction hard-cut. The local owner's epoch is handled by prediction.
    private readonly Dictionary<long, uint> lastTeleportEpochByEntity = new();
    private readonly List<long> teleportEpochPruneScratch = new();   // reused per-ingest to prune departed entities
    private readonly List<long> remoteTeleports = new();             // remotes hard-cut this Poll (surfaced via RemoteTeleports)

    // Presentation trace (debug diagnostic; null unless PresentationTraceEnabled). Records per-frame internal signals.
    private readonly PresentationTrace? presentationTrace;
    private double lastSnapshotArrivalClock;          // presentationClock at the most recent ingest (for sinceSnapshot)
    private float lastReconcileError;                 // magnitude of the most recent local reconcile correction (m)
    private bool snapshotArrivedSinceFrame;           // a snapshot/delta ingested since the last traced frame

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
        WorldBounds? bounds = null, IPhysicsWorld? physics = null, ReplicationRegistry? registry = null,
        Func<float, float, float, MovementMedium>? medium = null)
        : this(connectFactory: null, transport, groundHeight, tuning, config, token, groundNormal, bounds, physics, registry, medium)
    {
        ArgumentNullException.ThrowIfNull(transport);
    }

    /// <summary>Reconnect-capable client: <paramref name="connect"/> is invoked once for the initial connection and
    /// again per reconnect attempt (rebuilding the transport + session), resuming the same <paramref name="token"/>.
    /// Auto-reconnect is on by default (see <see cref="WorldClientConfig.AutoReconnect"/>). This client owns and
    /// disposes the transports it builds; dispose the client to close the current one.</summary>
    public WorldClient(Func<INetTransport> connect, Func<float, float, float> groundHeight, MoveTuning tuning,
        WorldClientConfig? config = null, byte[]? token = null, Func<float, float, Vector3>? groundNormal = null,
        WorldBounds? bounds = null, IPhysicsWorld? physics = null, ReplicationRegistry? registry = null,
        Func<float, float, float, MovementMedium>? medium = null)
        : this(connect ?? throw new ArgumentNullException(nameof(connect)), connect(), groundHeight, tuning, config,
               token, groundNormal, bounds, physics, registry, medium)
    {
    }

    private WorldClient(Func<INetTransport>? connectFactory, INetTransport transport,
        Func<float, float, float> groundHeight, MoveTuning tuning, WorldClientConfig? config, byte[]? token,
        Func<float, float, Vector3>? groundNormal, WorldBounds? bounds, IPhysicsWorld? physics,
        ReplicationRegistry? registry, Func<float, float, float, MovementMedium>? medium)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));
        config ??= new WorldClientConfig();
        this.connectFactory = connectFactory;
        // Consumer-injectable registry: build it with MoveProtocol.CreateRegistry(configure), the SAME registry the
        // server uses, so the client decodes the game's extension components. Default = movement-only.
        this.registry = registry ?? MoveProtocol.CreateRegistry();
        // Always fold this build's engine wire generation into the Hello (even with no consumer ProtocolVersion), so a
        // wire-skewed server rejects us cleanly at connect rather than admitting a client that would then misparse its
        // snapshots. The opt-in consumer ProtocolVersion, when set, rides as an inner layer checked on top by a
        // VersionCheckingAuthenticator. Store the wrapped form so each reconnect attempt resends it.
        this.token = ProtocolHandshake.BuildClientToken(MoveProtocol.WireProtocolVersion, config.ProtocolVersion, token);
        ownedTransport = connectFactory is not null ? transport : null;   // we dispose only what we built
        net = new NetClient(transport, this.token);
        view = new ClientReplicationView(this.registry);
        // Predict against the SAME physics world the server is authoritative over (mirrors WorldServer),
        // so a solid-prop consumer predicts straight rather than rubber-banding. Defaults null = terrain-only.
        frameAnchoring = config.FrameAnchoring;
        // Gated exactly like WorldServerConfig.FrameAnchoring gates the server's own analogous guard: a server
        // whose own FrameAnchoring is off never re-anchors (see WorldServer.ReanchorIsland), so it never stamps a
        // ReplicatedPosition frame off Origin, and a client that will never adopt anything but Origin has nothing
        // to rebase for. Forcing rebasability on it anyway would refuse a perfectly safe non-rebasable world.
        if (frameAnchoring) RequireRebasablePhysics(physics);
        islandPhysics = physics;
        simulator = new PlayerMoveSimulator(groundHeight, tuning, groundNormal, bounds, physics, medium, config.SamplerSpace);
        PredictionSettings settings = config.Prediction ?? (PredictionSettings.Default with { TickSeconds = config.TickSeconds });
        prediction = new ClientPrediction<PlayerMoveState, MoveCommand>(simulator, settings);
        interpolateRemotes = config.InterpolateRemotes;
        requestDeltaReplication = config.RequestDeltaReplication;
        tickSeconds = config.TickSeconds;
        interpolationDelaySeconds = MathF.Max(0f, config.InterpolationDelayTicks) * config.TickSeconds;
        presentationTrace = config.PresentationTraceEnabled ? new PresentationTrace() : null;
        disconnectTimeout = config.DisconnectTimeoutSeconds;
        autoReconnect = config.AutoReconnect;
        retryOnReject = config.RetryOnReject;
        backoff = config.Reconnect;
        attemptDeadlineRemaining = disconnectTimeout;   // also bound the initial connect
    }

    /// <summary>Net id of the local player, or -1 until the first snapshot identifies it (64-bit since 10.0.0).</summary>
    public long LocalNetId { get; private set; } = -1;

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

    /// <summary>Raised on the client thread during <see cref="Poll"/> when the server pushes a game message
    /// (<see cref="WorldServer.SendGameMessageTo"/> / <see cref="WorldServer.BroadcastGameMessage"/>, mirrored on
    /// <see cref="ShardedWorldServer"/>): the game-defined kind and the opaque payload (the engine never interprets it -
    /// deserialize it in the handler). Rides the Data channel via the frame envelope, out-of-band from the movement
    /// stream. Version-skew-safe: an older client that predates game messages simply ignores the frame (its demux has
    /// no case for the kind). The span is only valid for the duration of the call - copy it (<c>payload.ToArray()</c>)
    /// to keep the bytes.</summary>
    public event ClientGameMessageHandler? GameMessageReceived;

    /// <summary>Raised when an incoming snapshot could not be decoded (e.g. an unregistered component type id from a
    /// newer server protocol). The client disconnects with <see cref="DisconnectReason.IncompatibleVersion"/>; the
    /// argument is the decode error. A consumer can subscribe to log it or show "client out of date, please update"
    /// without polling <see cref="DisconnectReason"/>.</summary>
    public event Action<string>? SnapshotDecodeFailed;

    /// <summary>The most recent <see cref="ServerNotice"/> received, or null if none. Lets a consumer that attaches
    /// late, or polls instead of subscribing, still read the latest notice.</summary>
    public ServerNotice? LastNotice { get; private set; }

    /// <summary>
    /// Raised during <see cref="Poll"/> (on snapshot/delta ingest) when a local teleport landed this ingest: the
    /// first authoritative frame after a connect or reconnect (join/reconnect placement), or an in-session server
    /// teleport (an advance of the authoritative <see cref="MovementState.TeleportEpoch"/> - admin, self-rescue,
    /// fast-travel). The local avatar has already cut to the new position; the consumer reacts by snapping the follow
    /// camera onto <see cref="LocalRenderState"/> (<c>FollowCamera3D.Warp</c>) and optionally running a screen
    /// transition. Distinct from an ordinary reconciliation correction, which never fires this.
    /// </summary>
    public event Action? LocalTeleported;

    /// <summary>Monotonic count of local teleports that have landed (bumped once per <see cref="LocalTeleported"/>).
    /// A consumer that polls each frame instead of subscribing compares this frame-to-frame to detect a teleport
    /// robustly - it survives multiple teleports between frames and needs no clearing. Zero until the first teleport
    /// lands (the initial join placement bumps it to 1).</summary>
    public uint LocalTeleportEpoch { get; private set; }

    /// <summary>The net ids of REMOTE entities whose authoritative teleport landed during the most recent
    /// <see cref="Poll"/> (an advance of their replicated <see cref="MovementState.TeleportEpoch"/> - admin move,
    /// self-rescue, fast-travel). Refreshed (cleared, then refilled) each <see cref="Poll"/>, so read it right after
    /// polling; it accumulates across multiple ingests within one poll and survives multiple teleports. Empty on a
    /// normal frame.
    ///
    /// A consumer driving <c>ReplicatedCharacterAnimators</c> for remotes wires this to the render-height hard-cut:
    /// after <see cref="Poll"/>, call <c>animators.SnapRenderHeight(id)</c> for each id here, so a remote's SHORT
    /// teleport (a vertical gap under the smoother's snap distance) cuts crisply instead of gliding. The remote's
    /// interpolation buffer is already hard-cut internally on the same epoch advance; this list only surfaces WHICH
    /// remotes cut so a cosmetic layer (render-height smoother, a spawn puff) can match. Only remotes are listed - the
    /// local owner's teleport is surfaced by <see cref="LocalTeleported"/> / <see cref="LocalTeleportEpoch"/> instead.
    /// Populated only while <see cref="WorldClientConfig.InterpolateRemotes"/> is enabled, since the remote teleport
    /// epochs are tracked on the interpolation-flush path.</summary>
    public IReadOnlyList<long> RemoteTeleports => remoteTeleports;

    /// <summary>The debug per-frame presentation trace, or null unless <see cref="WorldClientConfig.PresentationTraceEnabled"/>
    /// was set. When enabled it accrues one row per rendered entity per <see cref="AdvancePresentation"/> (the render
    /// clock, interpolation delay, render time, seconds-since-snapshot, arrival marks, local reconcile-error, and the
    /// per-remote starvation-hold flag); dump it with <see cref="PresentationTrace.WriteCsv"/>. Diagnostics only -
    /// reading it never affects simulation or presentation.</summary>
    public PresentationTrace? PresentationTrace => presentationTrace;

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
        remoteTeleports.Clear();   // reflects only THIS poll's remote teleports; refilled by FlushTeleportedRemotes below

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
                    // Advertise delta replication so a delta-aware server upgrades this slot to AoI deltas. Sent on
                    // every (re)join since a reconnect lands on a fresh server slot. Harmless to an older server (it
                    // reads an unknown control and ignores it), so the client keeps getting full snapshots there.
                    if (requestDeltaReplication)
                        net.Send(MoveProtocol.EncodeClientControl(MoveProtocol.ClientControlKind.DeltaCapable),
                            NetChannelReliability.ReliableOrdered);
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
        lastTeleportEpochByEntity.Clear();   // the fresh view has no entities/samples; start remote-teleport tracking clean
        LocalNetId = -1;
        secondsSinceServerFrame = 0f;
        attemptDeadlineRemaining = disconnectTimeout;
        // Reset remote-interpolation bookkeeping so the new stream starts clean. The fresh ClientReplicationView above
        // drops the old sample history; the presentation clock keeps advancing monotonically (new samples are stamped
        // against it), so it need not reset.
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

    /// <summary>Sends a game-defined message to the server, surfaced on <see cref="WorldServer.OnGameMessage"/> /
    /// <see cref="ShardedWorldServer.OnGameMessage"/> with the same <paramref name="kind"/> and <paramref name="payload"/>.
    /// The engine never interprets the payload (opaque bytes the game serialized - an attack, an interaction, a chat
    /// line, an inventory op). <paramref name="reliability"/> chooses the transport channel:
    /// <see cref="NetChannelReliability.ReliableOrdered"/> gives ordered exactly-once delivery, so a command consumer
    /// needs no seq of its own; <see cref="NetChannelReliability.UnreliableSequenced"/> is a lossy latest-wins state
    /// ping. Rides the same Data channel as movement, demuxed by the server ahead of the move (never aliases it - see
    /// the framing contract in <see cref="MoveProtocol"/>). Returns false (and sends nothing) when not
    /// <see cref="WorldConnectionState.Connected"/>. A server that predates game messages flags the frame as malformed;
    /// gate on the <see cref="WorldClientConfig.ProtocolVersion"/> handshake when adopting this.</summary>
    public bool SendGameMessage(ushort kind, ReadOnlySpan<byte> payload, NetChannelReliability reliability)
    {
        if (state != WorldConnectionState.Connected) return false;
        net.Send(MoveProtocol.EncodeGameMessage(kind, payload), reliability);
        return true;
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
    /// interpolation. Remotes render on a fixed delay (<see cref="WorldClientConfig.InterpolationDelayTicks"/> ticks
    /// behind the newest snapshot): the monotonic render clock advances by <paramref name="dt"/>, and the two buffered
    /// snapshots bracketing (clock - delay) are lerped by their true timestamps, so between the discrete ~tick-rate
    /// snapshots they glide rather than teleport. A snapshot stall past the buffer holds the remote at the newest value
    /// (no extrapolation) until the next arrives. Idempotent within a frame: it rewrites the same interpolated state,
    /// so multiple <see cref="Snapshot"/> reads per frame are consistent.</summary>
    public void AdvancePresentation(float dt)
    {
        prediction.AdvancePresentation(dt);                   // local avatar (unaffected by remote interpolation)
        UpdateNetStatsWindow(dt);
        if (dt > 0f) presentationClock += dt;                 // monotonic render clock (snapshots are stamped against it)
        if (interpolateRemotes)
            // Render remotes on a fixed delay: pick the render time interpolationDelaySeconds behind the newest snapshot
            // (which arrived at ~presentationClock), then lerp the two buffered snapshots bracketing it. Because
            // renderTime advances smoothly with the render dt - not by ramping alpha off an estimated interval - there
            // is no phase drift, so no hold frames and no catch-up snaps at a non-integer render:tick ratio. The LOCAL
            // avatar (LocalNetId) is excluded: it renders from prediction, and its client-world ReplicatedPosition must
            // stay the last-received authoritative value (the reconcile basis), not a fixed-delay interpolated one.
            view.InterpolateAt(world, presentationClock - interpolationDelaySeconds, LocalNetId);
        if (presentationTrace is not null) RecordTraceFrame(dt);
    }

    // Append this frame's presentation-trace rows (one per rendered entity): the render-clock/delay/render-time, the
    // seconds since the last snapshot + whether one arrived this frame, the local reconcile-error, and each remote's
    // rendered position + starvation-hold flag. Debug-gated (only called when a PresentationTrace is attached).
    private void RecordTraceFrame(float dt)
    {
        double renderTime = presentationClock - interpolationDelaySeconds;
        double sinceSnapshot = presentationClock - lastSnapshotArrivalClock;
        bool arrived = snapshotArrivedSinceFrame;
        snapshotArrivedSinceFrame = false;
        foreach (EntityRenderState e in Snapshot())
        {
            bool held = !e.IsLocal && view.WasHeldAtLastInterpolation(e.Id.Value);
            presentationTrace!.Add(new PresentationTrace.Row(
                presentationClock, dt, renderTime, interpolationDelaySeconds, sinceSnapshot, arrived,
                e.IsLocal ? lastReconcileError : float.NaN, e.IsLocal, e.Id.Value, e.Position,
                e.VerticalVelocity, held));
        }
    }

    /// <summary>The current renderable set: local player predicted, remotes from the replicated position (smoothly
    /// interpolated between the last two snapshots when <see cref="WorldClientConfig.InterpolateRemotes"/> is set -
    /// the default - else the raw latest). Each entry also carries the exact grounded flag + vertical velocity
    /// (local: predicted; remote: replicated <see cref="MovementState"/>) so an animator bridge reads true air state
    /// instead of finite-differencing the terrain-following position.</summary>
    public IReadOnlyList<EntityRenderState> Snapshot()
    {
        var list = new List<EntityRenderState>();
        foreach (KeyValuePair<long, Entity> kv in view.Entities)
        {
            if (!world.IsAlive(kv.Value)) continue;
            bool isLocal = kv.Key == LocalNetId;
            Vector3 pos;
            bool grounded;
            float verticalVelocity;
            bool swimming;
            float climbRate;
            float stepCumulativeY = 0f, landingImpact = 0f;   // local-only signals: a remote receives neither (see EntityRenderState)
            if (isLocal)
            {
                // The local avatar's exact movement state is the predicted/reconciled state, converted to ABSOLUTE
                // world metres like every other entry in this list (see LocalRenderState for why that rule exists).
                PlayerMoveState rs = LocalRenderState;
                pos = rs.Position;
                grounded = rs.Grounded;
                verticalVelocity = rs.VerticalVelocity;
                swimming = rs.Swimming;
                climbRate = rs.Move.ClimbRate; landingImpact = rs.Move.LandingImpactSpeed;   // local: the exact predicted climb rate + landing impact (un-quantized)
                // The local step-smoothing accumulator (rides no wire): a mesh smoother diffs it to ease isolated steps
                // the continuous glide renders raw. Read from the predictor (not the rendered state, whose Move.StepDeltaY
                // is a per-tick event, not the running sum). Remotes leave it 0 (their singles ride position interpolation).
                stepCumulativeY = prediction.StepCumulativeY;
            }
            else
            {
                // Remotes: surface the replicated vertical state (MovementState rides alongside the position) so a
                // consumer animates jump/fall/swim from the authoritative flags instead of finite-differencing the
                // terrain-following position (which reads "airborne" the faster a remote moves over a slope, and
                // cannot tell a swimmer from a walker at all). Default grounded / not-swimming when a remote has no
                // MovementState yet, so it never spuriously starts airborne or swimming. MovementState is discrete
                // nearest-sampled to renderTime (E2), so its flags + climb rate ride the SAME delayed timeline as the
                // interpolated position - no flag/position skew.
                world.TryGet(kv.Value, out ReplicatedPosition rp);
                pos = rp.Value;
                bool hasMs = world.TryGet(kv.Value, out MovementState ms);
                grounded = hasMs ? ms.Grounded : true;
                verticalVelocity = ms.VerticalVelocity;
                swimming = hasMs && ms.Swimming;
                climbRate = hasMs ? MovementState.DecodeClimbRate(ms.ClimbRateQ) : 0f;
            }
            string? name = world.TryGet(kv.Value, out PlayerIdentity identity) ? identity.DisplayName : null;
            list.Add(new EntityRenderState(new NetId(kv.Key), pos, isLocal, name, grounded, verticalVelocity, swimming, climbRate, stepCumulativeY, landingImpact));
        }
        return list;
    }

    /// <summary>
    /// Reads a replicated component off the entity with network id <paramref name="netId"/> (the id carried by
    /// <see cref="EntityRenderState.Id"/>). Returns <c>true</c> and the component when the entity is currently in
    /// this client's area of interest AND carries a <typeparamref name="T"/> whose type id both ends registered;
    /// <c>false</c> otherwise (unknown id, entity not in view, or the component absent). This is how a game reads a
    /// server-assigned discriminator per entity (an NPC kind, HP, faction) to pick a model or drive behaviour,
    /// registered on the shared <see cref="ReplicationRegistry"/> (see
    /// <see cref="MoveProtocol.CreateRegistry(System.Action{ReplicationRegistry})"/>). Version-skew-safe: against an
    /// OLDER server that never sends <typeparamref name="T"/>, this simply returns <c>false</c>: no handshake, no
    /// disconnect. Reflects the last applied snapshot; call it after <see cref="Poll"/>.
    /// </summary>
    public bool TryGetComponent<T>(long netId, out T component) where T : struct, IComponent
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
            case MoveProtocol.ServerFrameKind.Delta:
                OnDelta(payload);
                break;
            case MoveProtocol.ServerFrameKind.Notice:
                ServerNotice notice = MoveProtocol.DecodeNotice(payload);
                if (notice.Kind == ServerNoticeKind.Shutdown) sawShutdownNotice = true;
                LastNotice = notice;
                NoticeReceived?.Invoke(notice);
                break;
            case MoveProtocol.ServerFrameKind.GameMessage:
                if (MoveProtocol.TryDecodeGameMessageBody(payload, out ushort gameKind, out ReadOnlySpan<byte> gamePayload))
                    GameMessageReceived?.Invoke(gameKind, gamePayload);
                break;
            // An unknown ServerFrameKind (e.g. a newer server frame this build predates) falls through and is ignored -
            // this is what makes a new server-to-client frame version-skew-safe downstream (see MoveProtocol).
        }
    }

    private void OnSnapshot(byte[] data)
    {
        if (!MoveProtocol.TryDecodeSnapshotFrame(data, out long localNetId, out int ackSeq, out byte[] snapshot)) return;
        // Last-resort backstop: a snapshot we can't decode (e.g. an unregistered component type id from a newer
        // server protocol) must become a clean disconnect, never an unhandled exception in the consumer's frame
        // loop. Surfaced as IncompatibleVersion so the consumer shows "client out of date, please update".
        if (!view.TryApply(world, snapshot, out string? decodeError))
        {
            OnSnapshotDecodeFailed(decodeError);
            return;
        }
        IngestServerState(localNetId, ackSeq);
    }

    private void OnDelta(byte[] data)
    {
        if (!MoveProtocol.TryDecodeSnapshotFrame(data, out long localNetId, out int ackSeq, out byte[] delta)) return;
        // A delta rides the same [localNetId][ackSeq] header as a snapshot; its body is an AoiDeltaReplicator delta.
        // A decode / baseline failure is terminal, the same clean disconnect as an undecodable snapshot.
        if (!view.TryApplyDelta(world, delta, out string? decodeError))
        {
            OnSnapshotDecodeFailed(decodeError);
            return;
        }
        IngestServerState(localNetId, ackSeq);
        // Ack the applied replication seq so the server advances this client's delta baseline. Reliable-ordered, so a
        // dropped ack just self-heals: the server keeps diffing from the last acked baseline until a newer ack lands.
        net.Send(MoveProtocol.EncodeReplicationAck(view.LastAppliedSeq), NetChannelReliability.ReliableOrdered);
    }

    // Shared post-apply for a full snapshot or a delta: NetStats ingest count, remote-interpolation interval, and the
    // local prediction reconcile against the freshly applied authoritative basis. Identical for both frame kinds.
    private void IngestServerState(long localNetId, int ackSeq)
    {
        snapshotsSinceWindow++;                                  // NetStats: AoI snapshot/delta ingest rate
        bool first = LocalNetId < 0;
        LocalNetId = localNetId;

        if (presentationTrace is not null)
        {
            lastSnapshotArrivalClock = presentationClock;        // for the trace's sinceSnapshot signal
            snapshotArrivedSinceFrame = true;                   // mark the arrival on the next traced frame
        }

        if (interpolateRemotes)
        {
            // Buffer this snapshot's interpolatable state stamped at the current render-clock time. InterpolateAt then
            // renders the two samples bracketing (presentationClock - interpolationDelaySeconds) by their true stamps.
            // The LOCAL avatar (localNetId) is excluded from the buffer: it renders from prediction, so its samples are
            // wasted, and buffering-then-InterpolateAt would clobber its ReplicatedPosition (the reconcile basis read
            // just below) with a stale fixed-delay value - the post-teleport-slide bug. A reconnect that re-ids the
            // local player drops the new id's stale buffer inside RecordInterpolationSample.
            view.RecordInterpolationSample(presentationClock, localNetId);
            // Remote teleport hard-cut: AFTER the post-teleport sample is buffered above, snap any remote whose
            // authoritative teleport epoch advanced this ingest to that newest sample, dropping the pre-teleport ones -
            // otherwise the buffer straddles the jump and InterpolateAt streaks the remote across the world.
            FlushTeleportedRemotes(localNetId);
        }

        // Reconcile reads the freshly applied 'current' here, BEFORE any AdvancePresentation interpolates the world,
        // so the local prediction basis is the true authoritative value, never an interpolated one.
        if (view.TryGetEntity(localNetId, out Entity local) && world.TryGet(local, out ReplicatedPosition p))
        {
            // Build the full authoritative basis from BOTH replicated components - position and the vertical axis
            // (MovementState) - so prediction replay reproduces the jump/fall, not just the XZ plane. The basis KEEPS
            // the server's frame stamp, and the client adopts that frame (rebasing its own physics world with it)
            // before the replay runs, so replayed commands step in the space the basis is expressed in.
            world.TryGet(local, out MovementState ms);           // default (grounded, 0) until first replicated
            PlayerMoveState basis = PlayerMoveState.From(p, ms);
            AdoptIslandFrame(p.Frame);
            if (first)
            {
                // First frame of the genuine initial connect: seed prediction at the authoritative spawn from seq 0
                // (client + server both start fresh). First frame of a RECONNECT (we have seeded before): re-seed the
                // predicted state but keep the seq counter monotonic - the fresh server already advanced its ack from
                // the commands sent in the join gap, so zeroing the seq would get every post-reconnect command
                // rejected as stale and pin the avatar forever.
                if (seededPredictionOnce) prediction.Reseed(basis);
                else { prediction.Reset(basis); seededPredictionOnce = true; }
            }
            ReconciliationResult rr = prediction.Reconcile(authoritativeTick++, basis, ackSeq);
            RecordCorrection(rr.PositionError);                  // NetStats: predicted-vs-authoritative delta (m)
            lastReconcileError = rr.PositionError;               // for the trace's local reconcile-error signal
            if (rr.Teleported)                                   // a teleport landed: seed placement or an epoch advance
            {
                LocalTeleportEpoch++;
                LocalTeleported?.Invoke();
            }
        }
    }

    // Snap any REMOTE whose replicated MovementState.TeleportEpoch advanced this ingest to the newest interpolation
    // sample (dropping the pre-teleport samples), so a remote teleport renders as a hard cut instead of a streak across
    // the world, and record its id in remoteTeleports so a cosmetic layer (surfaced via RemoteTeleports) can cut too.
    // The local owner (localNetId) is skipped: its teleport is handled by prediction, and it renders from prediction,
    // not this interpolation buffer. First observation of an entity just records its epoch (no flush; its buffer has a
    // single sample anyway). Departed entities are pruned so the map stays bounded. Only called when interpolateRemotes
    // is set (without interpolation a remote already snaps to the latest snapshot each frame).
    private void FlushTeleportedRemotes(long localNetId)
    {
        foreach (KeyValuePair<long, Entity> kv in view.Entities)
        {
            long id = kv.Key;
            if (id == localNetId) continue;
            uint epoch = world.TryGet(kv.Value, out MovementState ms) ? ms.TeleportEpoch : 0u;
            if (lastTeleportEpochByEntity.TryGetValue(id, out uint prev) && epoch != prev)
            {
                view.SnapInterpolationToNewest(id);
                remoteTeleports.Add(id);   // surface the hard cut so a cosmetic layer (RemoteTeleports) can match it
            }
            lastTeleportEpochByEntity[id] = epoch;
        }

        // Prune epochs for entities no longer present (left AoI / despawned), so the map cannot grow unbounded.
        if (lastTeleportEpochByEntity.Count > view.Entities.Count)
        {
            teleportEpochPruneScratch.Clear();
            foreach (long id in lastTeleportEpochByEntity.Keys)
                if (!view.Entities.ContainsKey(id)) teleportEpochPruneScratch.Add(id);
            foreach (long id in teleportEpochPruneScratch) lastTeleportEpochByEntity.Remove(id);
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
