using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

/// <summary>Tunables for <see cref="WorldServer"/>.</summary>
public sealed class WorldServerConfig
{
    /// <summary>Fixed server tick, seconds.</summary>
    public float TickSeconds { get; init; } = 1f / 30f;
    /// <summary>Per-client area-of-interest radius (world units).</summary>
    public float InterestRadius { get; init; } = 200f;
    /// <summary>Maximum concurrent players.</summary>
    public int MaxPlayers { get; init; } = 16;
    /// <summary>Per-slot spawn position (XZ used; Y is ground-clamped). Default spreads players along +X.</summary>
    public Func<int, Vector3>? SpawnPosition { get; init; }
}

/// <summary>
/// Reference single-<see cref="World"/> authoritative movement server. A <see cref="NetServer"/> session layer
/// spawns one player entity per connection; each tick it drains that client's queued <see cref="MoveCommand"/>,
/// runs the shared <see cref="PlayerMoveSimulator"/> (ground-clamped), and serves every client a per-area-of-
/// interest snapshot (<see cref="SnapshotWriter.WriteFiltered"/> over an <see cref="InterestGrid"/>) prefixed
/// with that client's net id + last-acked move seq so the client can reconcile. Headless, transport-injected.
/// Multi-cell sharding folds in with world streaming later; this is the single-world slice.
/// </summary>
public sealed class WorldServer
{
    private readonly WorldServerConfig config;
    private readonly ReplicationRegistry registry = MoveProtocol.CreateRegistry();
    private readonly World world = new();
    private readonly NetServer net;
    private readonly InterestGrid interest;
    private readonly RemoteCommandQueue<MoveCommand> commands = new(neutralCommand: default);
    private readonly PlayerMoveSimulator simulator;

    private readonly Dictionary<int, int> netIdBySlot = new();
    private readonly Dictionary<int, Entity> entityBySlot = new();
    private readonly Dictionary<int, PlayerMoveState> stateBySlot = new();
    private readonly Dictionary<int, int> lastAckBySlot = new();
    private int nextNetId = 1;

    public WorldServer(INetTransport transport, WorldServerConfig config,
        Func<float, float, float> groundHeight, MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));
        simulator = new PlayerMoveSimulator(groundHeight, tuning, groundNormal);
        net = new NetServer(transport, config.MaxPlayers, new AllowAllAuthenticator());
        interest = new InterestGrid(MathF.Max(1f, config.InterestRadius));
    }

    /// <summary>The authoritative ECS world.</summary>
    public World World => world;
    /// <summary>The replicated-component registry; clients build the matching one via MoveProtocol.</summary>
    public ReplicationRegistry Registry => registry;
    /// <summary>Number of joined players.</summary>
    public int PlayerCount => netIdBySlot.Count;
    /// <summary>The net id of the player entity for a joined slot.</summary>
    public bool TryGetPlayerNetId(int slot, out int netId) => netIdBySlot.TryGetValue(slot, out netId);

    /// <summary>Ingests session events (join/leave) and client input. Call once before <see cref="Tick"/>.</summary>
    public void Poll()
    {
        net.Poll();
        while (net.TryDequeueEvent(out ServerSessionEvent ev))
        {
            switch (ev.Kind)
            {
                case ServerSessionEventKind.Joined:
                    OnJoin(ev.Slot);
                    break;
                case ServerSessionEventKind.Left:
                    OnLeave(ev.Slot);
                    break;
                case ServerSessionEventKind.Data:
                    if (netIdBySlot.ContainsKey(ev.Slot)
                        && MoveProtocol.TryDecodeMove(ev.Data, out int seq, out MoveCommand cmd))
                        commands.Store(ev.Slot, seq, cmd);
                    break;
            }
        }
    }

    /// <summary>Steps one authoritative frame: apply each client's queued input, then serve every client its AoI.</summary>
    public void Tick(float dt)
    {
        // Authoritative movement: one command per player per tick.
        var slots = new List<int>(netIdBySlot.Keys);
        foreach (int slot in slots)
        {
            MoveCommand cmd = commands.Dequeue(slot, out int ack);
            lastAckBySlot[slot] = ack;
            PlayerMoveState state = simulator.Step(stateBySlot[slot], cmd, dt);
            stateBySlot[slot] = state;
            world.Set(entityBySlot[slot], new ReplicatedPosition { Value = state.Position });
        }

        // Rebuild AoI index from current positions.
        interest.Clear();
        world.ForEach<NetId>((Entity e, ref NetId id) =>
        {
            if (world.TryGet(e, out ReplicatedPosition p)) interest.Insert(id.Value, p.Value.X, p.Value.Z);
        });

        // Serve each client its area-of-interest snapshot, headered with its own net id + ack.
        foreach (int slot in slots)
        {
            int netId = netIdBySlot[slot];
            Vector3 p = stateBySlot[slot].Position;
            HashSet<int> set = interest.Query(p.X, p.Z, config.InterestRadius);
            byte[] snapshot = SnapshotWriter.WriteFiltered(world, registry, set);
            byte[] frame = MoveProtocol.EncodeSnapshotFrame(netId, lastAckBySlot[slot], snapshot);
            net.SendTo(slot, frame, NetChannelReliability.ReliableOrdered);
        }
    }

    private void OnJoin(int slot)
    {
        Vector3 spawn = config.SpawnPosition?.Invoke(slot) ?? new Vector3(slot * 2f, 0f, 0f);
        // Ground-clamp the spawn (an idle step settles Y onto the terrain + half-height).
        PlayerMoveState state = simulator.Step(new PlayerMoveState { Position = spawn }, MoveCommand.Idle, config.TickSeconds);

        int netId = nextNetId++;
        Entity e = world.Spawn();
        world.Set(e, new NetId(netId));
        world.Set(e, new ReplicatedPosition { Value = state.Position });

        netIdBySlot[slot] = netId;
        entityBySlot[slot] = e;
        stateBySlot[slot] = state;
        lastAckBySlot[slot] = -1;
    }

    private void OnLeave(int slot)
    {
        if (entityBySlot.TryGetValue(slot, out Entity e) && world.IsAlive(e)) world.Despawn(e);
        netIdBySlot.Remove(slot);
        entityBySlot.Remove(slot);
        stateBySlot.Remove(slot);
        lastAckBySlot.Remove(slot);
    }
}
