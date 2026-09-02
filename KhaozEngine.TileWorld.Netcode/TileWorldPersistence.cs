using System;
using System.Numerics;
using System.Threading.Tasks;
using KhaozEngine.Diagnostics;
using KhaozEngine.WorldStore;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>Tunables and game-state hooks for <see cref="TileWorldPersistence"/>. The first six are the shared core's
/// knobs under their own names, so a game reads one config type rather than two.</summary>
public sealed record TileWorldPersistenceConfig
{
    /// <summary>Seconds between dirty-record save passes. A crash loses at most this much.</summary>
    public float SaveIntervalSeconds { get; init; } = 30f;

    /// <summary>Key prefix for a player record. The stored key is <c>{KeyPrefix}{accountId}</c>.</summary>
    public string KeyPrefix { get; init; } = "player:";

    /// <summary>Key prefix a record that failed validation is filed under instead of overwriting it, so the intact
    /// original survives for offline repair while the primary is free to be rewritten from the fresh spawn.</summary>
    public string QuarantineKeyPrefix { get; init; } = "quarantine:";

    /// <summary>Persist tokenless connections under a minted durable key. Off by default: a head keys a tokenless
    /// connection by its SEAT, and a seat is inherited by the next connection, so a record filed under one would
    /// load onto a stranger.</summary>
    public bool PersistGuests { get; init; }

    /// <summary>How many rejoin hints to keep, so a returning player's entity is BUILT on the tile they left rather
    /// than at the spawn and then moved. Zero or less turns the seed off.</summary>
    public int ResumeHintCapacity { get; init; } = 1024;

    /// <summary>Tile distance below which a restore is applied without reporting a teleport. A rejoiner seeded from
    /// the hint is already standing on the loaded tile, and a teleport for a move of nothing makes the client cut
    /// when it should not.
    /// <para>HALF a tile by default, deliberately below the core's own 1f. This binding is a LATTICE and it carries
    /// the plane on the position's Y (see <c>Binding</c> below), so the only distance that means "did not move" is
    /// zero, and every real move is at least one. At the core's default a one tile step and a whole FLOOR both
    /// measure exactly 1 and both passed as no move, so a restore that moved a player between floors was applied
    /// quietly and the client glided between them instead of cutting. Half a tile keeps a positive epsilon around
    /// "the same cell" while every one unit move stays loud. The alternative weighed was a per-binding predicate on
    /// the shared core, which would add a seam to express what one number already expresses exactly here, and the
    /// float binding would still want its slack shaped in metres.</para></summary>
    public float QuietRestoreDistance { get; init; } = 0.5f;

    /// <summary>Tile rect a loaded record must land inside, or it is quarantined. Null accepts any tile. This is the
    /// authoritative play area, checked against the STORED record rather than against the live player, so a record
    /// edited outside the server cannot place anyone off the map.</summary>
    public TileRect? Bounds { get; init; }

    /// <summary>Captures the game's opaque blob for a slot, on the server thread, given the runtime slot and the
    /// RESOLVED store key. Raised at every save point, so it may read the live per-player object by slot. Null
    /// persists the tile only.
    /// <para>Null or empty means "no game state", NOT "keep what is stored". Returning it after a previous save
    /// wrote bytes erases the stored blob on the next pass.</para></summary>
    public Func<int, string, byte[]?>? CaptureGameState { get; init; }

    /// <summary>Applies a loaded blob to a slot, on the server thread, as the loaded tile is applied. Given the
    /// runtime slot, the account id and the blob. Never raised for a player with no stored blob.</summary>
    public Action<int, string, byte[]?>? ApplyGameState { get; init; }

    /// <summary>Vets a loaded blob. A non-null return is the quarantine reason and rejects the WHOLE record, tile
    /// included. Only raised for a record that actually carries a blob.</summary>
    public Func<byte[]?, string?>? ValidateGameState { get; init; }
}

/// <summary>
/// The TILE binding of <see cref="StatePersistence{TState}"/>, the sibling of <c>NetWorld.WorldPersistence</c>.
/// Everything subtle (the save interval, the dirty pass, the load guard, quarantine, the guest policy, the rejoin
/// hints) is the shared core, and is documented once on that type. This type supplies only the four things that are
/// tile-shaped: how a state becomes a record, how a record becomes a state, where a state is in space, and what
/// makes a record invalid.
/// <para>The position hint is carried as <c>(tileX, plane, tileZ)</c> in a <see cref="Vector3"/>, which is the
/// core's currency. Tile coordinates are small integers, so that is exact: no rounding enters the round trip, and
/// the quiet-restore distance is measured in whole tiles.</para>
/// <para>The map it is built with is the one the head runs on, and the validation step measures a loaded record
/// against it: a record naming a plane or a region the running world no longer has is quarantined and its player
/// placed at the configured spawn, rather than reaching the host's door and throwing out of <see cref="Update"/>.
/// The refusals mirror <see cref="TileWorldServer.SetPlayerState"/>'s own, which is the point: the binding refuses
/// exactly what the door would over anything a record can spell. The door's step-origin and step-progress refusals
/// have no mirror here on purpose: a record carries neither field, and the state it builds through
/// <see cref="TileMoveState.At"/> is always standing.</para>
/// <para>The route is not persisted, deliberately. A player who logs out mid-walk logs back in standing on the tile
/// they had reached, which is where the server had already committed them. Persisting a route would restore a walk
/// nobody asked for, against a world that may have changed under it.</para>
/// </summary>
public sealed class TileWorldPersistence
{
    // Resolved once per type, ambient: it follows Log.Configure rather than pinning whatever manager happened to be
    // configured when this type was first touched.
    static readonly ILogger Log = Diagnostics.Log.Get("TileWorldPersistence");

    readonly StatePersistence<TileMoveState> core;

    /// <summary>Subscribes to the host's join/leave events and installs the rejoin seed, so the layer is live the
    /// moment it is constructed. Build it before the head can admit anyone.</summary>
    /// <param name="host">The head persistence drives, usually the <see cref="TileWorldServer"/> itself.</param>
    /// <param name="store">Where records are kept.</param>
    /// <param name="world">The SAME baked collision map the head runs on. Required rather than optional because
    /// it is what lets a loaded record be measured against the world this build actually has: a record naming a
    /// plane or a region an edited world dropped is quarantined here instead of being applied through a door that
    /// throws. See the Validate step in <c>Binding</c>.</param>
    /// <param name="config">Tunables and game-state hooks. Null takes every default.</param>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> or <paramref name="world"/> is null.</exception>
    public TileWorldPersistence(IPersistenceHost<TileMoveState> host, IWorldStore store, TileCollisionMap world,
        TileWorldPersistenceConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(world);
        TileWorldPersistenceConfig c = config ?? new TileWorldPersistenceConfig();

        // The blob's verdict goes to the core rather than into the binding, so it is raised only for a record that
        // actually carries a blob, exactly as the float binding raises its own. The binding validates the STATE.
        Func<int, string, byte[]?, string?>? validate = null;
        if (c.ValidateGameState is { } validateHook)
            validate = (_, _, blob) => validateHook(blob);

        core = new StatePersistence<TileMoveState>(host, store, Binding(c, world), new PersistenceCoreConfig
        {
            SaveIntervalSeconds = c.SaveIntervalSeconds,
            KeyPrefix = c.KeyPrefix,
            QuarantineKeyPrefix = c.QuarantineKeyPrefix,
            PersistGuests = c.PersistGuests,
            ResumeHintCapacity = c.ResumeHintCapacity,
            QuietRestoreDistance = c.QuietRestoreDistance,
            CaptureGameState = c.CaptureGameState,
            ApplyGameState = c.ApplyGameState,
            ValidateGameState = validate,
            // The core carries no logger of its own, so its lines come back out here under this category.
            Diagnostic = (message, ex) => { if (ex is null) Log.Info(message); else Log.Warn(message, ex); },
        });
    }

    // What the core cannot know about a tile player. The plane rides the Vector3's Y so one hint carries a full
    // lattice address, and a record from another plane therefore measures exactly one unit of distance. That is a
    // MOVE and it is why the config above sets its own sub-1 QuietRestoreDistance: at the core's default of 1f a
    // whole floor was not greater than the threshold and the restore went in quietly.
    static PersistenceBinding<TileMoveState> Binding(TileWorldPersistenceConfig c, TileCollisionMap world) => new(
        PositionOf: s => new Vector3(s.Tile.X, s.Tile.Plane, s.Tile.Z),
        Encode: (s, game) => TilePlayerRecord.From(s, game).Encode(),
        Decode: (byte[] data, out TileMoveState state, out byte[]? game) =>
        {
            // Throws on bytes that are not JSON, which the core catches and routes to quarantine. A record that
            // parses but carries nonsense is caught by Validate below instead.
            TilePlayerRecord record = TilePlayerRecord.Decode(data);
            state = record.ToState();
            game = record.Game;
            return true;
        },
        Validate: (s, _) =>
        {
            // The plane and the region are measured against the world THIS BUILD ACTUALLY HAS, and they are the
            // whole reason the binding is handed the map. TileWorldServer.SetPlayerState is a door and THROWS for
            // both (a player on a plane the world does not have can never step, and one in a region the map never
            // loaded is invisible to everyone and to itself), while the core applies a record that passed
            // validation with no try around it. So a record that outlived a world edit, a dropped region or a
            // lowered plane count, used to come out of Update(dt) as an ArgumentException in the head's frame
            // loop. Refused here it QUARANTINES instead: the bad record is copied aside intact and the player is
            // placed at the head's configured spawn, which is what the core's contract promises for every other
            // rejection on this list.
            if (s.Tile.Plane < 0 || s.Tile.Plane >= world.PlaneCount) return "plane the world does not have";
            if (!world.HasRegion(s.Tile.Region)) return "tile in a region the world has not loaded";
            if (c.Bounds is { } b && !b.Contains(s.Tile.X, s.Tile.Z)) return "tile out of bounds";
            // The facing is stored as a raw byte, so a hand-edited or corrupted record can carry one no direction
            // maps to. Rejecting it here is what keeps an illegal enum value out of the simulator.
            if ((byte)s.Facing > (byte)TileDirection.NE) return "facing out of range";
            return null;
        });

    /// <summary>The rejoin hints, so a head can seed a join before the record loads. Pre-warm it from the game's own
    /// store at boot to keep rejoins quiet across a process restart.</summary>
    public PositionHintCache Hints => core.Hints;

    /// <inheritdoc cref="StatePersistence{TState}.OnStoreError"/>
    public event Action<Exception>? OnStoreError
    {
        add => core.OnStoreError += value;
        remove => core.OnStoreError -= value;
    }

    /// <inheritdoc cref="StatePersistence{TState}.OnRecordQuarantined"/>
    public event Action<string, string>? OnRecordQuarantined
    {
        add => core.OnRecordQuarantined += value;
        remove => core.OnRecordQuarantined -= value;
    }

    /// <inheritdoc cref="StatePersistence{TState}.OnLoadApplyDropped"/>
    public event Action<string, int>? OnLoadApplyDropped
    {
        add => core.OnLoadApplyDropped += value;
        remove => core.OnLoadApplyDropped -= value;
    }

    /// <summary>Advances the save interval and drains the apply queue. Call once per server frame, on the server
    /// thread, so a completed load is applied there and never from a background continuation.</summary>
    public void Update(float dt) => core.Update(dt);

    /// <summary>Saves every dirty record now.</summary>
    public void SaveDirtyPass() => core.SaveDirtyPass();

    /// <summary>Awaits every outstanding write. Call on shutdown, after the drain.</summary>
    public Task FlushAsync() => core.FlushAsync();
}
