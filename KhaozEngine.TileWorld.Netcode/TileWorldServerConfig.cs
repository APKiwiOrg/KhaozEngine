using System;
using KhaozEngine.Netcode;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// Everything a tile server is handed rather than assumes. <see cref="TickSeconds"/>, <see cref="StepTicks"/> and
/// <see cref="Spawn"/> are <c>required</c> on purpose: a tick length is the game's decision, and an engine default
/// for it would be exactly the constant this design refuses to have. The rest carry defaults that are either a
/// property of the tile grid itself (the cell edge) or a starting point a game is expected to tune.
/// <para>The knobs that reach <see cref="TileMoveSimulator"/> (<see cref="StepTicks"/> and <see cref="Move"/>) are
/// part of the DETERMINISM CONTRACT: the client builds its predicting simulator from the same pair, so a server
/// that changes either without telling the client turns every step into a misprediction. The rest are server-side
/// only and a client neither needs nor is told them.</para>
/// </summary>
public sealed record TileWorldServerConfig
{
    /// <summary>Seconds per simulation tick. The snapshot cadence is welded to it: one serve per tick, so this is
    /// also how often a client hears from the server.</summary>
    public required float TickSeconds { get; init; }

    /// <summary>Ticks per step, per mode. Half of the determinism contract with the client, see the type doc.</summary>
    public required TileStepTicks StepTicks { get; init; }

    /// <summary>Where a player with no saved record is built. A returning player is placed by the persistence layer
    /// instead, which is why this is a single tile rather than a spawn area.</summary>
    public required TileCoord Spawn { get; init; }

    /// <summary>Shard cell edge in tiles. One region by default, which is the only value that makes a cell a
    /// region, and a value that is not <see cref="TileCells.CellSize"/> gives up the alignment every other tile
    /// system (streaming, persistence, the file layout) already runs on.</summary>
    public float CellSize { get; init; } = TileCells.CellSize;

    /// <summary>Area-of-interest radius in tiles: how far a player sees other players. 15 is the tile stack's
    /// traditional view distance, and a bigger one costs a bigger snapshot on every tick for every player.</summary>
    public float InterestRadius { get; init; } = 15f;

    /// <summary>Border overlap in tiles. Must be at least <see cref="InterestRadius"/> or the home cell cannot hold
    /// the whole interest as ghosts, which is checked at construction rather than left to the first serve after a
    /// player walks near an edge.</summary>
    public float OverlapMargin { get; init; } = 16f;

    /// <summary>Session slot capacity, and the ceiling on how many players the command queue tracks.</summary>
    public int MaxPlayers { get; init; } = 200;

    /// <summary>Planes the world has, used to refuse a command naming one it does not. It is the world's number
    /// rather than the protocol's: the wire carries a plane in one byte, so a document with more planes than this
    /// would be refused at the decoder long before it got here.</summary>
    public int PlaneCount { get; init; } = TileWorldDocument.DefaultPlaneCount;

    /// <summary>Largest Chebyshev distance from the player a walk goal may name. A farther goal is dropped rather
    /// than pathed, because the pathfinder's search window is (2r+1)^2 scratch entries and an unbounded goal is an
    /// unbounded allocation a client chooses. Dropped rather than clamped, so the two heads never end up walking to
    /// two different tiles.</summary>
    public int MaxGoalRadius { get; init; } = TilePathfinder.DefaultMaxRadius;

    /// <summary>How many server-owned actors one cell may hold. A cell is a REGION (see <see cref="TileCells"/>),
    /// so this is the per-region monster budget and it multiplies by the resident regions to give the world's.
    /// Enforced as a refusal at <see cref="TileWorldServer.SpawnActor"/>, which answers 0 rather than throwing so a
    /// spawner cannot take a tick down with it.</summary>
    public int MaxActorsPerCell { get; init; } = 64;

    /// <summary>Inbound message budget per connection, spent per POLL rather than per wall-clock second. The token
    /// bucket is topped up once per <see cref="TileWorldServer.Poll"/> with <c>MaxCommandsPerSecond * TickSeconds</c>
    /// tokens, so a head admits that many messages per poll and its sustained ceiling is
    /// <c>pollRate * MaxCommandsPerSecond * TickSeconds</c> messages per second. That is the name's own number only
    /// on a head that polls exactly once per tick, and proportionally more on one polling faster, which is every
    /// real one. Deliberate rather than a slip: it is the <c>RateLimiter</c> contract <c>ShardedWorldServer</c> and
    /// <c>WorldServer</c> also run on, and a budget topped up on the TICK would throttle a 60 Hz client that sent
    /// nothing unusual, because a client sends on its frame cadence.
    /// <para>Deliberately well ABOVE the drain rate, which is
    /// one command per player per tick (four per second at a 250 ms tick): the budget is a flood gate, not a
    /// cadence, and a client whose packets bunch after a lag spike or a reconnect is delivering real input late
    /// rather than cheating. What stops a burst turning into a server that walks stale input for the next minute is
    /// the queue's own catch-up threshold, which sheds a backlog deeper than a couple of seconds and jumps to the
    /// newest command, movement being latest wins. Lower this toward <c>1 / TickSeconds</c> only if a head wants
    /// bursts REFUSED at the door instead of shed at the drain.</para></summary>
    public int MaxCommandsPerSecond { get; init; } = 40;

    /// <summary>Inbound burst allowance per connection, so a client that batches a frame's worth of input is not
    /// throttled for being bursty rather than loud.</summary>
    public double CommandBurst { get; init; } = 20;

    /// <summary>Simulator knobs both heads must agree on, the other half of the determinism contract. The route cap
    /// lives here (<see cref="TileMoveOptions.MaxRouteSteps"/>), so a server never holds a route the wire cannot
    /// carry and a client predicts the same truncation.</summary>
    public TileMoveOptions Move { get; init; } = new();

    /// <summary>Simulator knobs for ACTORS, which are deliberately not the player's. The default drops
    /// <see cref="TileMoveOptions.MaxPathRadius"/> from 64 to 12, because
    /// <c>TilePathfinder.FindPath</c> allocates its scratch per call at <c>(2r+1)^2</c> entries: at 64 that is about
    /// 83 KB of Gen0 per call and at 12 it is about 3 KB, a 26-fold saving on the one path a chasing actor runs most
    /// often. Size it against the LEASH rather than against a player's click, since an actor never legitimately
    /// paths further than its leash. This is also where a larger <see cref="TileMoveOptions.AgentSize"/> would go
    /// the day multi-tile actors land.</summary>
    public TileMoveOptions ActorMove { get; init; } = new() { MaxPathRadius = 12 };

    /// <summary>How many ticks a player who was in combat keeps their entity in world after the session drops. Zero,
    /// the default, removes them at once. A game picks the number, because it is a combat number and the engine owns
    /// none of those: an engine default here would be exactly the constant this design refuses to have.
    /// <para>While it runs, the body LINGERS: still stepped, still served to everyone in interest, still attackable,
    /// and only then persisted and drained through the ordinary leave path. That is what stops a losing fight being
    /// escaped by pulling the plug. An operator <see cref="TileWorldServer.Kick"/> and a
    /// <see cref="TileWorldServer.BeginDrain"/> both bypass it, because neither is the player's decision.</para>
    /// <para>A RECONNECT by the same account inside the window ends the lingering body rather than being refused or
    /// seated beside it, so one account never holds two live entities. The player comes back where they left, into
    /// the fight they left, which is what the window is for.</para>
    /// <para>ONE NUMBER, TWO JOBS, deliberately. It is the length of the window a leave is held for, and it is also
    /// the LOOKBACK that decides whether a leaving player counts as being in a fight at all
    /// (<see cref="TileCombatState.LastCombatTick"/> within this many ticks). Raising it therefore widens what
    /// counts as combat as well as how long a body stands, which is the coherent reading of section 13.3's single
    /// "window" and is worth knowing before tuning it.</para></summary>
    public int CombatLogoutTicks { get; init; }

    /// <summary>Synchronous ban check over a verified account id, consulted at the door. Null admits everyone the
    /// authenticator admits. A head backs this with whatever store it keeps, which is why it is a delegate rather
    /// than an interface the engine would then have to define a schema for.</summary>
    public Func<string, bool>? IsBanned { get; init; }

    /// <summary>What a second live session for one account does. Kicking the older one is the default because the
    /// alternative refuses the player who is actually at the keyboard.</summary>
    public DuplicateSessionPolicy DuplicateSessions { get; init; } = DuplicateSessionPolicy.KickOlder;
}
