using System;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;

namespace KhaozEngine.NetWorld;

/// <summary>Tunables for <see cref="ShardedWorldServer"/>.</summary>
public sealed partial class ShardedWorldServerConfig
{
    /// <summary>Fixed server tick, seconds.</summary>
    public float TickSeconds { get; init; } = 1f / 30f;
    /// <summary>World-grid cell edge length (world units). Align to the terrain/streaming chunk grid.</summary>
    public float CellSize { get; init; } = 60f;
    /// <summary>Border-overlap distance for ghosting. Must be &gt;= <see cref="InterestRadius"/>.</summary>
    public float OverlapMargin { get; init; } = 24f;
    /// <summary>Per-client area-of-interest radius (world units).</summary>
    public float InterestRadius { get; init; } = 24f;
    /// <summary>Maximum concurrent players.</summary>
    public int MaxPlayers { get; init; } = 64;
    /// <summary>Per-slot spawn position (XZ used; Y is ground-clamped). Default spreads players along +X near origin.</summary>
    public Func<int, Vector3>? SpawnPosition { get; init; }

    /// <summary>When true (the default), each <see cref="ShardedWorldServer.Tick"/> calls
    /// <see cref="World.AdvanceTick"/> once on every cell's world, clearing the per-tick change-tracking sets and
    /// event buffer so they don't accumulate on a long-running server (one entry per <c>Set</c>/<c>Despawn</c> in
    /// the owning cell, never reclaimed otherwise). The authoritative movement path doesn't consume them. Set false
    /// ONLY if your own systems read a cell's change-tracking/events and you advance each cell's tick yourself.</summary>
    public bool AdvanceWorldTick { get; init; } = true;

    /// <summary>Opt-in server-side anti-cheat / input-hardening knobs (rate limiting, movement-correction anomaly).
    /// All off by default, so behaviour is unchanged until a consumer tightens it.</summary>
    public AntiCheatConfig AntiCheat { get; init; } = new();

    /// <summary>Server-owned destination for a client <see cref="WorldClient.RequestSelfRescue"/> ("unstuck" /
    /// return-to-spawn): given the requesting player, returns the safe world position to teleport it to (across cells,
    /// reusing the admin path - position set, vertical velocity zeroed). The client never supplies a position - the
    /// server alone decides - so a hostile client can't teleport anywhere. A fixed point is just <c>_ =&gt; point</c>.
    /// <b>Null (the default) =&gt; the feature is OFF</b>: a self-rescue request is ignored.</summary>
    public Func<PlayerRef, Vector3>? SelfRescueDestination { get; init; }

    /// <summary>Per-player minimum interval (seconds) between honored self-rescues; further requests inside the window
    /// are dropped. Default 5s. Ignored when <see cref="SelfRescueDestination"/> is null.</summary>
    public float SelfRescueCooldownSeconds { get; init; } = 5f;

    /// <summary>Per-player input-backlog catch-up cap, in commands (ticks); mirrors
    /// <see cref="WorldServerConfig.MaxInputBacklog"/>. When a client's queued move backlog grows deeper than this the
    /// server skips stale moves and applies only the most recent, so a reconnect flush / lag burst can't freeze a
    /// player under minutes-old input on rejoin. Default 8 (~0.27s at 30Hz); 0 disables (pre-8.8.0 one-per-tick).</summary>
    public int MaxInputBacklog { get; init; } = 8;

    /// <summary>Serve each client per-tick home-cell area-of-interest DELTAS (only what changed since that client's
    /// acknowledged baseline) instead of a full snapshot every tick. Default true. Mirrors
    /// <see cref="WorldServerConfig.DeltaReplication"/>: a client opts in with the
    /// <see cref="MoveProtocol.ClientControlKind.DeltaCapable"/> hello; older clients keep getting full snapshots, so
    /// client and server upgrade independently. The delta baseline is keyed by <see cref="NetId"/>, so a boundary
    /// crossing (home-cell change) stays a component delta, never a despawn+respawn. Set false to force full snapshots.</summary>
    public bool DeltaReplication { get; init; } = true;

    /// <summary>Maximum payload size (bytes) accepted on a client-to-server game message
    /// (<see cref="WorldClient.SendGameMessage"/>); mirrors <see cref="WorldServerConfig.MaxGameMessageBytes"/>. A
    /// larger payload is DROPPED (never dispatched to <see cref="ShardedWorldServer.OnGameMessage"/>) and flagged
    /// <see cref="SuspiciousReason.OversizedMessage"/>. The payload is opaque bytes to the engine. Default 1024. The
    /// rate limiter runs in front of this.</summary>
    public int MaxGameMessageBytes { get; init; } = 1024;

    /// <summary>What happens when a client presents a connect token whose authenticated subject ALREADY holds a slot
    /// on this server (one account, two clients), mirroring <see cref="WorldServerConfig.DuplicateSessions"/>. Default
    /// <see cref="DuplicateSessionPolicy.KickOlder"/>: the new session wins and the older one is disconnected with a
    /// distinct reason the client surfaces (<see cref="DisconnectReason.SignedInElsewhere"/>), its leave running
    /// before the new join is admitted so persistence sees leave-then-join rather than two live sessions sharing one
    /// account record. Set <see cref="DuplicateSessionPolicy.RefuseNewer"/> to keep the existing session and refuse
    /// the newcomer instead. A TOKENLESS connection has no subject and is never deduped.</summary>
    public DuplicateSessionPolicy DuplicateSessions { get; init; } = DuplicateSessionPolicy.KickOlder;

    /// <summary>Global cap on connections accepted but holding no slot yet (connected, Hello not yet answered),
    /// forwarded to <see cref="NetServer"/>. 0, the default, leaves it unlimited. Above 0, a connect past the cap is
    /// refused immediately, so a connection flood degrades to refused handshakes rather than unbounded server-side
    /// state, which is the ONE flood mitigation available without a remote address (the per-connection rate limiter
    /// only engages after a slot exists). Size it above the concurrent-join burst a launch or a restart produces, not
    /// at <see cref="MaxPlayers"/>. Watch <see cref="ShardedWorldServer.RefusedPendingConnectionCount"/> to tell a
    /// flood being shed from a cap set too low.</summary>
    public int MaxPendingConnections { get; init; }
}
