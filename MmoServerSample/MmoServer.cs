using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
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
/// </summary>
public sealed class MmoServer
{
    private readonly MmoServerConfig config;
    private readonly ReplicationRegistry registry;
    private readonly ShardHost host;
    private readonly NetServer net;
    private readonly RemoteCommandQueue<MoveCommand> commands = new(neutralCommand: default);
    private readonly IWorldStore store = new InMemoryWorldStore();
    private readonly Dictionary<int, int> playerNetIdBySlot = new();
    private int nextNetId = 1;

    public MmoServer(INetTransport transport, MmoServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(transport);
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        registry = CreateRegistry();
        host = new ShardHost(
            cellSize: config.CellSize,
            tickSeconds: config.TickSeconds,
            registry: registry,
            interestCellSize: config.CellSize,
            overlapMargin: config.OverlapMargin,
            positionAccessor: MmoProtocol.PositionAccessor);
        net = new NetServer(transport, config.MaxPlayers, new AllowAllAuthenticator());
    }

    /// <summary>The shard topology (cells, ownership, ghosts).</summary>
    public ShardHost Host => host;

    /// <summary>Durable world store (player blobs on leave).</summary>
    public IWorldStore Store => store;

    /// <summary>The replicated-component registry; clients must build the matching one.</summary>
    public static ReplicationRegistry CreateRegistry() => MmoProtocol.CreateRegistry();

    /// <summary>The NetId of the player entity for a joined <paramref name="slot"/>.</summary>
    public bool TryGetPlayerNetId(int slot, out int netId) => playerNetIdBySlot.TryGetValue(slot, out netId);

    /// <summary>Spawns a static server-owned entity (e.g. an NPC) at a world position. Returns its NetId.</summary>
    public int SpawnNpc(float x, float y) => SpawnEntity(x, y);

    /// <summary>Ingests session events (join/leave) and client input. Call once before <see cref="Tick"/>.</summary>
    public void Poll()
    {
        net.Poll();
        while (net.TryDequeueEvent(out ServerSessionEvent ev))
        {
            switch (ev.Kind)
            {
                case ServerSessionEventKind.Joined:
                    int playerNetId = SpawnEntity(config.SpawnX, config.SpawnY);
                    playerNetIdBySlot[ev.Slot] = playerNetId;
                    host.BindClient(ev.Slot, playerNetId);
                    break;

                case ServerSessionEventKind.Left:
                    OnClientLeft(ev.Slot);
                    break;

                case ServerSessionEventKind.Data:
                    if (playerNetIdBySlot.ContainsKey(ev.Slot)
                        && MmoProtocol.TryDecodeMove(ev.Data, out int seq, out MoveCommand cmd))
                        commands.Store(ev.Slot, seq, cmd);
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
        // Apply one input per client to its (wherever-owned) player.
        foreach (KeyValuePair<int, int> kv in playerNetIdBySlot)
        {
            MoveCommand cmd = commands.Dequeue(kv.Key, out _);
            if ((cmd.Dx != 0f || cmd.Dy != 0f)
                && host.TryGetOwner(kv.Value, out CellSim cell, out Entity e)
                && cell.World.TryGet(e, out Position p))
                cell.World.Set(e, new Position { X = p.X + cmd.Dx, Y = p.Y + cmd.Dy });
        }

        host.ProcessHandoffs();   // authority follows entities across boundaries (exactly-once)
        host.SyncGhosts();        // refresh border ghosts so home cells hold full AoI

        // Serve each client its area-of-interest from its single home cell.
        foreach (int slot in playerNetIdBySlot.Keys)
        {
            byte[] snapshot = host.SnapshotForClient(slot, config.InterestRadius);
            net.SendTo(slot, snapshot, NetChannelReliability.ReliableOrdered);
        }
    }

    private int SpawnEntity(float x, float y)
    {
        int netId = nextNetId++;
        Entity e = host.SpawnAt(x, y, out CellSim cell);
        cell.World.Set(e, new NetId(netId));
        cell.World.Set(e, new Position { X = x, Y = y });
        return netId;
    }

    private void OnClientLeft(int slot)
    {
        if (!playerNetIdBySlot.TryGetValue(slot, out int playerNetId)) return;

        if (host.TryGetOwner(playerNetId, out CellSim cell, out Entity e))
        {
            if (cell.World.TryGet(e, out Position p))
                store.SaveAsync($"player/{slot}", MmoProtocol.EncodeMove(0, new MoveCommand(p.X, p.Y)))
                    .GetAwaiter().GetResult(); // leave is rare; off the hot path
            cell.World.Despawn(e);
        }

        host.UnbindClient(slot);
        playerNetIdBySlot.Remove(slot);
    }
}
