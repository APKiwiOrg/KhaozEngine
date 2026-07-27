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

    /// <summary>Smooth remote players between the discrete (~tick-rate) replicated snapshots by rendering them on a
    /// fixed interpolation delay (see <see cref="InterpolationDelayTicks"/>) and lerping the two buffered snapshots
    /// bracketing that render time by their true timestamps, instead of teleporting one snapshot-step per ingest.
    /// Default <c>true</c>: every remote glides, decoupled from both the tick cadence and the render fps (no hold
    /// frames, no catch-up snaps at a non-integer render:tick ratio), at the cost of <see cref="InterpolationDelayTicks"/>
    /// ticks of remote render latency. Set <c>false</c> to restore the raw-latest-position behaviour (no delay, but
    /// steppy remotes).</summary>
    public bool InterpolateRemotes { get; init; } = true;

    /// <summary>Fixed remote-interpolation delay, in ticks: remotes are rendered this far behind the newest received
    /// snapshot so the two snapshots bracketing the render time are (almost) always already in hand, absorbing tick
    /// and render jitter. Default 2 ticks (~66 ms at a 30 Hz tick). Lower it toward ~1.5 for less latency at the cost
    /// of less jitter headroom (a late snapshot then holds the remote sooner); raise it for a rougher network. Keep it
    /// at least ~1 tick: because a snapshot is stamped at the render clock as of its arrival frame and the render time
    /// advances one more frame before it is used, the EFFECTIVE delay is about one render frame less, so a value below
    /// ~1 tick increasingly degrades toward holding the newest snapshot (0 leaves no bracket = remotes frozen at their
    /// last snapshot). Only used when <see cref="InterpolateRemotes"/> is set. Clamped to at least 0.</summary>
    public float InterpolationDelayTicks { get; init; } = 2f;

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

    /// <summary>Advertise delta replication on join so a delta-aware server serves this client per-tick area-of-interest
    /// deltas (only what changed since the client's acknowledged baseline) instead of a full snapshot every tick.
    /// Default true. The client still decodes full snapshots, so against a server that predates the feature (or one
    /// with <see cref="WorldServerConfig.DeltaReplication"/> off) it transparently keeps receiving full snapshots and
    /// sends no acks - client and server upgrade independently, no disconnect. Set false to force full snapshots (the
    /// pre-9.17.0 wire).</summary>
    public bool RequestDeltaReplication { get; init; } = true;

    /// <summary>Enable the debug-only per-frame <see cref="PresentationTrace"/> (default false = off, zero overhead).
    /// When set, <see cref="WorldClient.PresentationTrace"/> is non-null and records the presentation-layer internal
    /// signals (render time, interpolation delay, seconds-since-snapshot, per-remote hold flag, snapshot arrivals,
    /// local reconcile-error) plus rendered positions every <see cref="WorldClient.AdvancePresentation"/>, dumpable to
    /// CSV. Gate a game's diagnostic key on it; leave off in shipping.</summary>
    public bool PresentationTraceEnabled { get; init; }

    /// <summary>The coordinate space this client's sampler delegates (ground height, ground normal, medium) read.
    /// It must MATCH the server's <see cref="WorldServerConfig.SamplerSpace"/> /
    /// <see cref="ShardedWorldServerConfig.SamplerSpace"/>, because prediction replays the same step the server ran
    /// and a sampler answering in the other space produces a different trajectory from identical inputs.
    /// <see cref="NetWorld.SamplerSpace.World"/> (the default) keeps them on absolute coordinates and the step
    /// converts for them. <see cref="NetWorld.SamplerSpace.Frame"/> hands them frame-local coordinates, which is what
    /// a sampler backed by the client's own physics world needs.
    /// <para><b>Nothing on the wire carries this value, so a client/server mismatch is never detected or refused.</b>
    /// It degrades exactly like feeding <c>WorldFrame</c>-local coordinates to a sampler that expects absolute ones
    /// (or the reverse): every ground/normal/medium query returns a plausible-looking but wrong answer, so
    /// prediction diverges from the server's own step by whatever the sampler's actual world/frame delta is,
    /// producing a steady reconciliation correction rather than a clean failure. Matching this against the server's
    /// config is entirely the consumer's responsibility.</para></summary>
    public SamplerSpace SamplerSpace { get; init; } = SamplerSpace.World;

    /// <summary>Mirrors <see cref="WorldServerConfig.FrameAnchoring"/> / <see cref="ShardedWorldServerConfig.FrameAnchoring"/>:
    /// whether this client ever needs to follow a re-anchored island frame. Default true. Gates two things: the
    /// constructor's "the physics world must be able to rebase" guard, so a consumer whose server never frames
    /// (its own <c>FrameAnchoring</c> is false, and it therefore never stamps a <see cref="ReplicatedPosition"/>
    /// frame off the world origin) can hand the client a non-rebasable <c>IPhysicsWorld</c> without
    /// losing the physics-backed prediction that ctor param buys, and the runtime frame-adopt step's
    /// <c>IPhysicsWorld.Rebase</c> call (a belt-and-braces match: with this off, the client never attempts to
    /// rebase even if a frame it did not expect arrived). <b>Off is only correct when the server it connects to
    /// ALSO has framing off</b> - against a framed server this leaves the client's physics world stuck at its
    /// original origin the first time the server re-anchors, so prediction queries colliders a frame-width from
    /// where the authoritative state says it is.</summary>
    public bool FrameAnchoring { get; init; } = true;
}
