using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using KhaozEngine.WorldStore;

namespace MmoServerSample;

/// <summary>Tunables for <see cref="MmoServer"/>.</summary>
public sealed class MmoServerConfig
{
    /// <summary>World-grid cell edge length.</summary>
    public float CellSize { get; init; } = 100f;
    /// <summary>Fixed server tick, seconds.</summary>
    public float TickSeconds { get; init; } = 1f / 30f;
    /// <summary>Border-overlap distance for ghosting. Must be &gt;= <see cref="InterestRadius"/>.</summary>
    public float OverlapMargin { get; init; } = 32f;
    /// <summary>Per-client area-of-interest radius.</summary>
    public float InterestRadius { get; init; } = 32f;
    /// <summary>Maximum concurrent players.</summary>
    public int MaxPlayers { get; init; } = 64;
    /// <summary>Where a new player spawns.</summary>
    public float SpawnX { get; init; } = 50f;
    public float SpawnY { get; init; } = 50f;
}

/// <summary>
/// Reference dedicated MMO server wiring the whole stack together: a multi-cell <see cref="ShardHost"/>
/// (Sharding 3A-3D) driven over a <see cref="NetServer"/> session layer (any <see cref="INetTransport"/> -
/// LiteNetLib in production, loopback in tests), with per-client home-cell area-of-interest serving and
/// <see cref="IWorldStore"/> persistence. Transport-injected and headless: <see cref="Poll"/> ingests session
/// events + client input, <see cref="Tick"/> steps one authoritative server frame and serves every client.
/// Player state is account-keyed by the verified session subject (<see cref="ServerSessionEvent.Subject"/>, falling
/// back to a per-slot guest id) via <see cref="WorldPersistence"/> - captured on leave, validated and applied on
/// join - and demonstrates a game-state validation quarantine through its <see cref="PrivateStats"/> health blob.
/// It also implements the resume-spawn seam (<see cref="IWorldPersistenceHost.SetResumePositionProvider"/> and
/// <see cref="IWorldPersistenceHost.TryGetConfiguredSpawn"/>), so a rejoining account is BUILT where it left rather
/// than served the spawn first and moved afterwards. Both are default interface methods, so a head that omits them
/// keeps the two-teleport reconnect: this sample implements them because it is the file a game copies.
/// </summary>
public sealed class MmoServer : ICellPersistenceHost, IWorldPersistenceHost
{
    private readonly MmoServerConfig config;
    private readonly ReplicationRegistry registry;
    private readonly ShardHost host;
    private readonly NetServer net;
    private readonly RemoteCommandQueue<MoveCommand> commands = new(neutralCommand: default);
    private readonly IWorldStore store;
    private readonly CellPersistence cellPersistence;
    private readonly WorldPersistence persistence;
    private readonly AoiDeltaReplicator deltaReplicator;
    private readonly Dictionary<int, long> playerNetIdBySlot = new();
    private readonly Dictionary<int, string> accountBySlot = new();
    // The single NetId allocator (node 0) player joins + SpawnEntity draw from, so ids never collide (see NetIdAllocator).
    private readonly NetIdAllocator allocator = new();

    public MmoServer(INetTransport transport, MmoServerConfig config) : this(transport, config, new InMemoryWorldStore()) { }

    public MmoServer(INetTransport transport, MmoServerConfig config, IWorldStore store)
    {
        ArgumentNullException.ThrowIfNull(transport);
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        registry = CreateRegistry();
        host = new ShardHost(
            cellSize: config.CellSize,
            tickSeconds: config.TickSeconds,
            registry: registry,
            interestCellSize: config.CellSize,
            overlapMargin: config.OverlapMargin,
            positionAccessor: MmoProtocol.PositionAccessor);
        net = new NetServer(transport, config.MaxPlayers, new AllowAllAuthenticator());
        cellPersistence = new CellPersistence(this, store);
        persistence = new WorldPersistence(this, store, new WorldPersistenceConfig
        {
            CaptureGameState = CapturePrivateStats,
            ApplyGameState = ApplyPrivateStats,
            ValidateGameState = ValidatePrivateStats,
        });
        deltaReplicator = new AoiDeltaReplicator(registry);
        host.CellCreated += cell => CellCreated?.Invoke(cell.Coord);
    }

    /// <summary>The shard topology (cells, ownership, ghosts).</summary>
    public ShardHost Host => host;

    /// <summary>Durable world store (player blobs on leave).</summary>
    public IWorldStore Store => store;

    /// <summary>The replicated-component registry; clients must build the matching one.</summary>
    public static ReplicationRegistry CreateRegistry() => MmoProtocol.CreateRegistry();

    /// <summary>This server's live registry (<see cref="ICellPersistenceHost.Registry"/>), which is what
    /// <see cref="CellPersistence"/> infers a pre-v4 cell blob's wire generation against.</summary>
    public ReplicationRegistry Registry => registry;

    /// <summary>The NetId of the player entity for a joined <paramref name="slot"/>.</summary>
    public bool TryGetPlayerNetId(int slot, out long netId) => playerNetIdBySlot.TryGetValue(slot, out netId);

    /// <summary>The game-message <c>kind</c> this reference server uses for a chat line. A game defines its own kind
    /// space (attack, interaction, inventory op, …); this sample carries just one.</summary>
    public const ushort ChatMessageKind = 1;

    /// <summary>Raised when a client sends a chat game message (slot + text). Demonstrates the engine's generic
    /// game-message seam: the client frames an opaque payload with <see cref="MoveProtocol.EncodeGameMessage"/> and the
    /// server demuxes it from the movement stream with <see cref="MoveProtocol.TryDecodeGameMessage"/> - the same codec
    /// a turn-key <see cref="WorldServer"/> / <see cref="ShardedWorldServer"/> consumer gets for free behind
    /// <see cref="WorldClient.SendGameMessage"/> / <see cref="WorldServer.OnGameMessage"/>.</summary>
    public event Action<int, string>? ChatReceived;

    /// <summary>The most recent chat line received (or null). Lets a test/console poll instead of subscribing.</summary>
    public string? LastChat { get; private set; }

    /// <summary>
    /// Spawns a server-owned entity at a world position, allocating a fresh <see cref="NetId"/> from the same
    /// allocator player joins draw from (never colliding), placing it in the owning cell, and pre-setting its
    /// <see cref="Position"/>; <paramref name="configure"/> then adds the game's own components. The reference
    /// pattern for authoring NPCs/resources, the same shape as <see cref="ShardedWorldServer.SpawnEntity"/>.
    /// </summary>
    public long SpawnEntity(float x, float y, Action<World, Entity>? configure = null)
    {
        long netId = allocator.Next().Value;
        Entity e = host.SpawnOwned(x, y, netId, out CellSim cell); // eager: registers netId in the O(1) ownership index
        cell.World.Set(e, new Position { X = x, Y = y });
        configure?.Invoke(cell.World, e);
        return netId;
    }

    /// <summary>Spawns a server-owned NPC tagged with a <see cref="Creature"/> kind (the consumer discriminator a
    /// client reads to pick its model) plus a hidden <see cref="AggroCounter"/> (Persist|Migrate, never replicated):
    /// the mob keeps its threat across handoff + restart but no client ever sees it. Players carry no
    /// <see cref="Creature"/>, so the client tells them apart.</summary>
    public long SpawnNpc(float x, float y, int kind = 0) =>
        SpawnEntity(x, y, (w, e) =>
        {
            w.Set(e, new Creature { Kind = kind });
            w.Set(e, new AggroCounter { Value = 0 });   // hidden server-only state, demonstrates Persist|Migrate-only
        });

    /// <summary>Spawns a persistable resource node at a world position. Returns its NetId.</summary>
    public long SpawnResourceNode(float x, float y, int amount) =>
        SpawnEntity(x, y, (w, e) => w.Set(e, new ResourceNode { Amount = amount }));

    /// <inheritdoc />
    public event Action<CellCoord>? CellCreated;

    /// <inheritdoc />
    public IReadOnlyCollection<CellCoord> LiveCellCoords
    {
        get { var l = new List<CellCoord>(host.CellCount); foreach (CellSim c in host.Cells) l.Add(c.Coord); return l; }
    }

    /// <inheritdoc />
    public byte[]? SnapshotCell(CellCoord coord) =>
        host.TryGetCell(coord, out CellSim cell) ? cell.SnapshotOwned(new HashSet<long>(playerNetIdBySlot.Values)) : null;

    /// <inheritdoc />
    public IReadOnlyList<long> RestoreCell(CellCoord coord, byte[] snapshot) =>
        host.TryGetCell(coord, out CellSim cell) ? cell.RestoreOwned(snapshot) : Array.Empty<long>();

    /// <inheritdoc />
    public void EnsureCell(CellCoord coord) => host.EnsureCell(coord);

    /// <inheritdoc />
    public long NextNetId => allocator.NextValue;

    /// <inheritdoc />
    public void EnsureNextNetIdAtLeast(long atLeast) => allocator.EnsureNextAtLeast(atLeast);

    /// <inheritdoc />
    public event Action<int, string>? PlayerJoined;

    /// <inheritdoc />
    public event Action<int, string, PlayerMoveState>? PlayerLeaving;

    /// <inheritdoc />
    public IReadOnlyCollection<int> JoinedSlots => accountBySlot.Keys;

    /// <inheritdoc />
    public bool TryGetAccountId(int slot, out string accountId) => accountBySlot.TryGetValue(slot, out accountId!);

    /// <inheritdoc />
    public bool TryGetPlayerState(int slot, out PlayerMoveState state)
    {
        if (playerNetIdBySlot.TryGetValue(slot, out long netId)
            && host.TryGetOwner(netId, out CellSim cell, out Entity e)
            && cell.World.TryGet(e, out Position p))
        {
            state = new PlayerMoveState { Position = new Vector3(p.X, 0f, p.Y) };
            return true;
        }
        state = default;
        return false;
    }

    /// <summary>Overrides a joined player's 2D position (sample X from <see cref="PlayerMoveState.Position"/>.X, sample Y
    /// from .Z). No-op for an unknown slot. This reference sample carries no teleport-epoch/interpolation state, so
    /// <paramref name="teleport"/> is accepted for interface parity but otherwise unused.</summary>
    public void SetPlayerState(int slot, in PlayerMoveState state, bool teleport = false)
    {
        if (!playerNetIdBySlot.TryGetValue(slot, out long netId)) return;
        if (!host.TryGetOwner(netId, out CellSim cell, out Entity e)) return;
        cell.World.Set(e, new Position { X = state.Position.X, Y = state.Position.Z });
    }

    // ---- the resume-spawn seam, the same shape as WorldServer.ResumeSpawn.cs / ShardedWorldServer.ResumeSpawn.cs ----
    // A custom head is only opted into the #642 reconnect fix if it implements these two: they are DEFAULT interface
    // methods, so leaving them out compiles and silently keeps the double teleport (spawn first, restore afterwards).
    // This sample implements them so a game copying it as the starting point for its own head gets the contract.

    private ResumePositionProvider? resumePosition;

    /// <inheritdoc />
    public void SetResumePositionProvider(ResumePositionProvider? provider) => resumePosition = provider;

    /// <inheritdoc />
    public bool TryGetConfiguredSpawn(int slot, out PlayerMoveState spawn)
    {
        spawn = new PlayerMoveState { Position = new Vector3(config.SpawnX, 0f, config.SpawnY) };
        return playerNetIdBySlot.ContainsKey(slot);
    }

    // Where a joining slot's entity is BUILT: the resume hint for this account when one is known, else the configured
    // spawn. This sample has no terrain, so there is no ground clamp to run over the result - a real head clamps here
    // (see WorldServer.OnJoin), which is also why TryGetConfiguredSpawn above hands back a full PlayerMoveState.
    private Vector3 JoinSpawn(string accountId) =>
        resumePosition is not null && resumePosition(accountId, out Vector3 resumed)
            ? resumed
            : new Vector3(config.SpawnX, 0f, config.SpawnY);

    /// <summary>Boot: resume the NetId allocator + instantiate saved cells, then apply restores. Call once before ticking.</summary>
    public async Task PreloadAsync()
    {
        await cellPersistence.LoadMetaAsync();
        await cellPersistence.PreloadAsync();
        await cellPersistence.FlushAsync();
    }

    /// <summary>Shutdown: persists all dirty cells + the NetId high-water, and every joined player's account-keyed
    /// record (position + the <see cref="PrivateStats"/> health blob).</summary>
    public Task FlushAsync() => Task.WhenAll(cellPersistence.FlushAsync(), persistence.FlushAsync());

    /// <summary>Ingests session events (join/leave) and client input. Call once before <see cref="Tick"/>.</summary>
    public void Poll()
    {
        net.Poll();
        while (net.TryDequeueEvent(out ServerSessionEvent ev))
        {
            switch (ev.Kind)
            {
                case ServerSessionEventKind.Joined:
                    // The account id is resolved BEFORE the spawn, because the spawn depends on it: a rejoining
                    // account's entity is built where it left, so the first snapshot of the new session is already
                    // the truth and the restore landing a frame later has nothing to move (#642).
                    string accountId = ev.Subject.Length > 0 ? ev.Subject : $"guest-{ev.Slot}";
                    Vector3 at = JoinSpawn(accountId);
                    // The player carries an OwnerOnly PrivateStats (exact HP) - replicated only back to its own
                    // client, never to another player observing it - alongside its replicated Position.
                    long playerNetId = SpawnEntity(at.X, at.Z,
                        (w, e) => w.Set(e, new PrivateStats { Health = 100 }));
                    playerNetIdBySlot[ev.Slot] = playerNetId;
                    host.BindClient(ev.Slot, playerNetId);
                    accountBySlot[ev.Slot] = accountId;
                    PlayerJoined?.Invoke(ev.Slot, accountId);
                    break;

                case ServerSessionEventKind.Left:
                    OnClientLeft(ev.Slot);
                    break;

                case ServerSessionEventKind.Data:
                    if (!playerNetIdBySlot.ContainsKey(ev.Slot)) break;
                    // Demux the client->server Data channel. A game message (the engine's generic seam - here a chat
                    // line) is tried BEFORE the move, exactly as WorldServer.HandleData does: it can never alias the
                    // move (see MoveProtocol's aliasing contract). Then the replication ack (advances the delta
                    // baseline), then the move command.
                    if (MoveProtocol.TryDecodeGameMessage(ev.Data, out ushort kind, out ReadOnlySpan<byte> payload))
                        OnGameMessage(ev.Slot, kind, payload);
                    else if (MmoProtocol.TryDecodeAck(ev.Data, out int ackSeq)) deltaReplicator.Acknowledge(ev.Slot, ackSeq);
                    else if (MmoProtocol.TryDecodeMove(ev.Data, out int seq, out MoveCommand cmd)) commands.Store(ev.Slot, seq, cmd);
                    break;
            }
        }
    }

    /// <summary>
    /// Steps one authoritative server frame: apply each client's queued input, transfer authority for boundary
    /// crossings, refresh border ghosts, then serve each client its home-cell area-of-interest snapshot.
    /// </summary>
    public void Tick(float dt)
    {
        persistence.Update(dt);
        cellPersistence.Update(dt);

        // Apply one input per client to its (wherever-owned) player.
        foreach (KeyValuePair<int, long> kv in playerNetIdBySlot)
        {
            MoveCommand cmd = commands.Dequeue(kv.Key, out _);
            if ((cmd.Dx != 0f || cmd.Dy != 0f)
                && host.TryGetOwner(kv.Value, out CellSim cell, out Entity e)
                && cell.World.TryGet(e, out Position p))
                cell.World.Set(e, new Position { X = p.X + cmd.Dx, Y = p.Y + cmd.Dy });
        }

        host.ProcessHandoffs();   // authority follows entities across boundaries (exactly-once)
        host.SyncGhosts();        // refresh border ghosts so home cells hold full AoI

        // Serve each client a per-client home-cell area-of-interest DELTA (only what changed since its acknowledged
        // baseline; a full snapshot the first time / until it acks). The baseline is keyed by NetId, so a boundary
        // crossing reads as a component delta, never a despawn+respawn.
        deltaReplicator.BeginTick();
        foreach (KeyValuePair<int, long> kv in playerNetIdBySlot)
        {
            int slot = kv.Key;
            long ownerNetId = kv.Value;
            (World world, HashSet<long> interest) = host.HomeInterest(slot, config.InterestRadius);
            // Owner-scope the Replicate channel to this client's own player so its OwnerOnly PrivateStats reach only it.
            byte[] delta = deltaReplicator.WriteFor(slot, world, interest, ownerNetId);
            net.SendTo(slot, delta, NetChannelReliability.ReliableOrdered);
        }
    }

    // Dispatch a decoded client game message. The payload is opaque to the engine; this reference server interprets a
    // ChatMessageKind payload as a UTF-8 chat line and surfaces it. A real game would branch on kind for attacks,
    // interactions, inventory ops, … A production server should also bound the payload size (see
    // WorldServerConfig.MaxGameMessageBytes) - the turn-key WorldServer does this for you.
    private void OnGameMessage(int slot, ushort kind, ReadOnlySpan<byte> payload)
    {
        if (kind != ChatMessageKind) return;
        string text = System.Text.Encoding.UTF8.GetString(payload);
        LastChat = text;
        ChatReceived?.Invoke(slot, text);
    }

    private void OnClientLeft(int slot)
    {
        if (!playerNetIdBySlot.TryGetValue(slot, out long playerNetId)) return;

        if (accountBySlot.TryGetValue(slot, out string? accountId) && TryGetPlayerState(slot, out PlayerMoveState st))
            PlayerLeaving?.Invoke(slot, accountId, st);

        if (host.TryGetOwner(playerNetId, out CellSim cell, out Entity e))
        {
            cell.UnregisterOwned(playerNetId); // eager: drop it from the ownership index before despawning
            cell.World.Despawn(e);
        }

        host.UnbindClient(slot);
        deltaReplicator.Forget(slot);
        playerNetIdBySlot.Remove(slot);
        accountBySlot.Remove(slot);
    }

    // WorldPersistenceConfig.CaptureGameState: the game's opaque durable per-player blob, here just the exact HP
    // (the engine never interprets it). Null when the player entity or its PrivateStats can't be resolved.
    private byte[]? CapturePrivateStats(in PlayerPersistenceContext context)
    {
        if (!playerNetIdBySlot.TryGetValue(context.Slot, out long netId)) return null;
        if (!host.TryGetOwner(netId, out CellSim cell, out Entity e)) return null;
        return cell.World.TryGet(e, out PrivateStats stats) ? BitConverter.GetBytes(stats.Health) : null;
    }

    // WorldPersistenceConfig.ApplyGameState: re-attaches a previously captured (and already-validated) health blob
    // at load-on-join. Ignores a blob of the wrong length rather than throwing - ValidatePrivateStats already
    // rejects it before this ever runs, so this guard is only a defensive backstop.
    private void ApplyPrivateStats(in PlayerPersistenceContext context, ReadOnlySpan<byte> blob)
    {
        if (blob.Length != 4) return;
        if (!playerNetIdBySlot.TryGetValue(context.Slot, out long netId)) return;
        if (!host.TryGetOwner(netId, out CellSim cell, out Entity e)) return;
        cell.World.Set(e, new PrivateStats { Health = BitConverter.ToInt32(blob) });
    }

    // WorldPersistenceConfig.ValidateGameState: the validation demo. A blob of the wrong length or a health outside
    // the game's valid 1..100 range quarantines the WHOLE loaded record (position included) rather than applying it.
    private static PlayerGameStateVerdict ValidatePrivateStats(in PlayerPersistenceContext context, ReadOnlySpan<byte> blob)
    {
        if (blob.Length != 4) return PlayerGameStateVerdict.Invalid("health blob must be 4 bytes");
        int health = BitConverter.ToInt32(blob);
        return health is < 1 or > 100
            ? PlayerGameStateVerdict.Invalid($"health {health} out of range 1..100")
            : PlayerGameStateVerdict.Valid();
    }
}
