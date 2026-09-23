using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The owner-only half of the movement state (#136). <see cref="MovementOwnerState"/> carries the two feel timers only
/// the owning client's reconciliation replay reads, on the built-in id <see cref="MoveProtocol.MovementOwnerTypeId"/>
/// registered <c>Default | OwnerOnly</c>. These pin the four things that make that split safe: a non-owner observer
/// is sent no timer bytes, the owner is sent both and replays exactly as before, handoff and persistence carry them
/// as they always did, and a stored body from before the split still restores its timers.
/// </summary>
public class MovementOwnerStateReplicationTests
{
    private const float Dt = 1f / 30f;
    private static readonly Func<float, float, float> Flat = (_, _) => 0f;
    private static readonly MovementOwnerState Timers = new() { TimeSinceGrounded = 0.375f, JumpBufferRemaining = 0.0625f };

    private static Entity SpawnPlayer(World world, long netId, Vector3 position, in MovementOwnerState owner)
    {
        Entity e = world.Spawn();
        world.Set(e, new NetId(netId));
        world.Set(e, ReplicatedPosition.FromWorld(position, WorldFrame.Origin));
        world.Set(e, new MovementState { VerticalVelocity = -2f, Grounded = false, Swimming = true, ClimbRateQ = 4 });
        world.Set(e, owner);
        return e;
    }

    private static List<ushort> FrameIds(byte[] snapshot, long netId)
    {
        var reader = new SnapshotBlobReader(snapshot,
            id => BuiltinBlobLayout.PayloadLength(id, BuiltinBlobLayout.CurrentWireGeneration));
        SnapshotBlobEntity entity = reader.Entities.Single(x => x.NetId == netId);
        return entity.Components.Select(c => c.TypeId).ToList();
    }

    [Fact]
    public void A_non_owner_observer_is_sent_no_timer_bytes_and_the_owner_is_sent_both()
    {
        ReplicationRegistry registry = MoveProtocol.CreateRegistry();
        var server = new World();
        SpawnPlayer(server, 1, new Vector3(1f, 1f, 1f), new MovementOwnerState { TimeSinceGrounded = 0.5f });
        SpawnPlayer(server, 2, new Vector3(2f, 1f, 2f), Timers);

        // Served to player 1's client: its own entity carries the owner frame, the player it observes does not.
        byte[] toOne = SnapshotWriter.WriteFiltered(server, registry, new HashSet<long> { 1, 2 },
            ReplicationChannels.Replicate, ownerNetId: 1);
        Assert.Contains(MoveProtocol.MovementOwnerTypeId, FrameIds(toOne, 1));
        Assert.DoesNotContain(MoveProtocol.MovementOwnerTypeId, FrameIds(toOne, 2));
        Assert.Contains(MoveProtocol.MovementTypeId, FrameIds(toOne, 2));   // the remote-needed half still arrives

        var view = new ClientReplicationView(registry);
        var client = new World();
        view.Apply(client, toOne);
        Assert.True(view.TryGetEntity(2, out Entity remote));
        Assert.False(client.Has<MovementOwnerState>(remote));
        MovementState seen = client.Get<MovementState>(remote);
        Assert.True(seen.Swimming);                // remotes still drive the swim pose from this
        Assert.Equal((sbyte)4, seen.ClimbRateQ);   // and the climb glide from this
        Assert.Equal(-2f, seen.VerticalVelocity);

        // Served to player 2's client: now it is player 2's timers that arrive, exactly.
        byte[] toTwo = SnapshotWriter.WriteFiltered(server, registry, new HashSet<long> { 1, 2 },
            ReplicationChannels.Replicate, ownerNetId: 2);
        var ownerView = new ClientReplicationView(registry);
        var owner = new World();
        ownerView.Apply(owner, toTwo);
        Assert.True(ownerView.TryGetEntity(2, out Entity local));
        Assert.Equal(Timers, owner.Get<MovementOwnerState>(local));
        Assert.True(ownerView.TryGetEntity(1, out Entity other));
        Assert.False(owner.Has<MovementOwnerState>(other));
    }

    /// <summary>
    /// The measured saving. A moving player as one non-owner observer receives it in a full snapshot is
    /// <c>[count:4][netId:8]</c>, a position frame <c>[2 + 16]</c>, a movement frame <c>[2 + 56]</c> and the
    /// terminator: 90 bytes. Generation 11 sent the same entity with a 64-byte movement payload, 98 bytes. The owner
    /// pays two bytes more than before for the frame id, and nobody else pays anything.
    /// </summary>
    [Fact]
    public void Bytes_per_move_to_a_non_owner_drop_by_the_eight_timer_bytes()
    {
        ReplicationRegistry registry = MoveProtocol.CreateRegistry();
        var server = new World();
        SpawnPlayer(server, 2, new Vector3(2f, 1f, 2f), Timers);
        var only = new HashSet<long> { 2 };

        byte[] toObserver = SnapshotWriter.WriteFiltered(server, registry, only, ReplicationChannels.Replicate, ownerNetId: 1);
        byte[] toOwner = SnapshotWriter.WriteFiltered(server, registry, only, ReplicationChannels.Replicate, ownerNetId: 2);

        Assert.Equal(64, BuiltinBlobLayout.MovementPayloadLength(BuiltinBlobLayout.MovementOwnerWireGeneration - 1));
        Assert.Equal(56, BuiltinBlobLayout.MovementPayloadLength(BuiltinBlobLayout.MovementOwnerWireGeneration));
        Assert.Equal(4 + 8 + (2 + 16) + (2 + 56) + 2, toObserver.Length);
        Assert.Equal(toObserver.Length + 2 + BuiltinBlobLayout.MovementOwnerPayloadBytes, toOwner.Length);
    }

    /// <summary>
    /// On the delta path a change confined to the timers used to force the whole movement payload out to every
    /// observer, because the timers sat inside it. Now it reaches the owner alone and an observer's delta is empty.
    /// </summary>
    [Fact]
    public void A_timer_only_change_sends_nothing_to_a_non_owner_on_the_delta_path()
    {
        ReplicationRegistry registry = MoveProtocol.CreateRegistry();
        var server = new World();
        Entity mover = SpawnPlayer(server, 2, new Vector3(2f, 1f, 2f), Timers);
        SpawnPlayer(server, 1, new Vector3(1f, 1f, 1f), default);
        var all = new HashSet<long> { 1, 2 };
        var repl = new AoiDeltaReplicator(registry);

        int seq = repl.BeginTick();
        repl.WriteFor(0, server, all, ownerNetId: 1);
        repl.WriteFor(1, server, all, ownerNetId: 2);
        repl.Acknowledge(0, seq);
        repl.Acknowledge(1, seq);

        server.Set(mover, new MovementOwnerState { TimeSinceGrounded = 0.4f, JumpBufferRemaining = 0.03f });
        repl.BeginTick();
        byte[] toObserver = repl.WriteFor(0, server, all, ownerNetId: 1);
        byte[] toOwner = repl.WriteFor(1, server, all, ownerNetId: 2);

        // [baselineSeq][snapshotSeq][removedCount = 0][changedCount = 0]: nothing about the mover at all.
        Assert.Equal(16, toObserver.Length);
        Assert.Equal(0, BitConverter.ToInt32(toObserver, 12));
        Assert.Equal(1, BitConverter.ToInt32(toOwner, 12));
    }

    private sealed class Rig : IDisposable
    {
        public IDisposable? Server { get; init; }
        public required Action Step { get; init; }
        public required WorldClient Observer { get; init; }
        public required WorldClient Mover { get; init; }
        public required Func<long, MovementOwnerState> ServerTimers { get; init; }

        public void Dispose() => Server?.Dispose();
    }

    private static Rig FlatRig(bool sharded, bool delta)
    {
        var hub = new InMemoryTransportHub();
        Action tick;
        Func<long, MovementOwnerState> serverTimers;
        IDisposable? owned = null;
        if (sharded)
        {
            var config = new ShardedWorldServerConfig { TickSeconds = Dt, DeltaReplication = delta };
            var server = new ShardedWorldServer(hub.Server, config, Flat, MoveTuning.Default);
            owned = server;
            tick = () => { server.Poll(); server.Tick(Dt); };
            serverTimers = netId =>
            {
                Assert.True(server.Host.TryGetOwner(netId, out CellSim cell, out Entity e));
                return cell.World.Get<MovementOwnerState>(e);
            };
        }
        else
        {
            var config = new WorldServerConfig { TickSeconds = Dt, InterestRadius = 500f, MaxPlayers = 8, DeltaReplication = delta };
            var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
            tick = () => { server.Poll(); server.Tick(Dt); };
            serverTimers = netId =>
            {
                foreach (int slot in server.JoinedSlots)
                    if (server.TryGetPlayerNetId(slot, out long id) && id == netId
                        && server.TryGetPlayerState(slot, out PlayerMoveState s))
                        return MovementOwnerState.From(s);
                throw new Xunit.Sdk.XunitException($"player {netId} is not joined");
            };
        }
        var clientConfig = new WorldClientConfig { TickSeconds = Dt };
        var observer = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default, clientConfig);
        var mover = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default, clientConfig);
        return new Rig
        {
            Server = owned,
            Step = () => { tick(); observer.Poll(); mover.Poll(); },
            Observer = observer,
            Mover = mover,
            ServerTimers = serverTimers,
        };
    }

    /// <summary>End to end through both server heads and both serve paths: the timers reach the owning client, equal
    /// to the server's, and never reach the client watching it.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void A_live_server_serves_the_timers_to_the_owner_only(bool sharded, bool delta)
    {
        using Rig rig = FlatRig(sharded, delta);
        for (int i = 0; i < 40 && !(rig.Observer.Joined && rig.Mover.Joined); i++)
        {
            rig.Observer.SendInput(MoveCommand.Idle);
            rig.Mover.SendInput(MoveCommand.Idle);
            rig.Step();
        }
        Assert.True(rig.Observer.Joined && rig.Mover.Joined);
        long moverId = rig.Mover.LocalNetId;

        // Jump, then hang in the air a few ticks, so both timers are live (non-zero) on the server.
        var jump = new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f, jump: true);
        for (int i = 0; i < 6; i++)
        {
            rig.Observer.SendInput(MoveCommand.Idle);
            rig.Mover.SendInput(i == 0 ? jump : MoveCommand.Idle);
            rig.Step();
        }

        MovementOwnerState authoritative = rig.ServerTimers(moverId);
        Assert.True(authoritative.TimeSinceGrounded > 0f, "the mover should be airborne with a running coyote clock");
        Assert.True(rig.Mover.TryGetComponent(moverId, out MovementOwnerState mine));
        Assert.Equal(authoritative, mine);

        Assert.True(rig.Observer.TryGetComponent(moverId, out MovementState _));
        Assert.False(rig.Observer.TryGetComponent(moverId, out MovementOwnerState _));
    }

    /// <summary>
    /// The owner's reconcile basis is built from what the owner is served, and it has to replay bit for bit what the
    /// server steps. Two states where the timers decide the outcome: a buffered jump that fires on the next grounded
    /// tick, and an airborne jump press whose coyote window has long expired. The same basis with the timers dropped
    /// (what a non-owner could build) diverges on both, which is what makes the owner-only half load-bearing.
    /// </summary>
    [Fact]
    public void The_owners_wire_basis_replays_exactly_and_needs_the_timers_to()
    {
        ReplicationRegistry registry = MoveProtocol.CreateRegistry();
        var sim = new PlayerMoveSimulator(Flat, MoveTuning.Default);
        var jump = new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f, jump: true);
        float rest = MoveTuning.Default.CapsuleHalfHeight;

        var buffered = new PlayerMoveState
        {
            Move = new MoveState { Position = new Vector3(0f, rest, 0f), Grounded = true, JumpBufferRemaining = 0.08f },
        };
        var lateCoyote = new PlayerMoveState
        {
            Move = new MoveState { Position = new Vector3(0f, 3f, 0f), VerticalVelocity = -1f, TimeSinceGrounded = 1f },
        };

        foreach ((PlayerMoveState authoritative, MoveCommand command) in new[] { (buffered, MoveCommand.Idle), (lateCoyote, jump) })
        {
            var server = new World();
            Entity e = server.Spawn();
            server.Set(e, new NetId(1));
            server.Set(e, ReplicatedPosition.FromWorld(authoritative.Position, WorldFrame.Origin));
            server.Set(e, MovementState.From(authoritative));
            server.Set(e, MovementOwnerState.From(authoritative));

            PlayerMoveState ownerBasis = Basis(registry, server, ownerNetId: 1);
            PlayerMoveState observerBasis = Basis(registry, server, ownerNetId: 2);
            Assert.Equal(authoritative.Move.TimeSinceGrounded, ownerBasis.Move.TimeSinceGrounded);
            Assert.Equal(authoritative.Move.JumpBufferRemaining, ownerBasis.Move.JumpBufferRemaining);

            PlayerMoveState a = authoritative, o = ownerBasis, x = observerBasis;
            bool diverged = false;
            for (int i = 0; i < 8; i++)
            {
                MoveCommand c = i == 0 ? command : MoveCommand.Idle;
                a = sim.Step(a, c, Dt);
                o = sim.Step(o, c, Dt);
                x = sim.Step(x, c, Dt);
                Assert.Equal(a.Position, o.Position);
                Assert.Equal(a.VerticalVelocity, o.VerticalVelocity);
                Assert.Equal(a.Grounded, o.Grounded);
                Assert.Equal(a.Move.TimeSinceGrounded, o.Move.TimeSinceGrounded);
                Assert.Equal(a.Move.JumpBufferRemaining, o.Move.JumpBufferRemaining);
                diverged |= a.Position != x.Position;
            }
            Assert.True(diverged, "a basis without the timers should replay differently, or the test proves nothing");
        }
    }

    private static PlayerMoveState Basis(ReplicationRegistry registry, World server, long ownerNetId)
    {
        byte[] snapshot = SnapshotWriter.WriteFiltered(server, registry, new HashSet<long> { 1 },
            ReplicationChannels.Replicate, ownerNetId);
        var view = new ClientReplicationView(registry);
        var client = new World();
        view.Apply(client, snapshot);
        Assert.True(view.TryGetEntity(1, out Entity e));
        client.TryGet(e, out MovementOwnerState owner);
        return PlayerMoveState.From(client.Get<ReplicatedPosition>(e), client.Get<MovementState>(e), owner);
    }

    private static bool PosAccessor(World w, Entity e, out float x, out float y)
    {
        if (w.TryGet(e, out ReplicatedPosition p)) { x = p.Value.X; y = p.Value.Z; return true; }
        x = y = 0f;
        return false;
    }

    /// <summary>Handoff carries the timers (the destination keeps simulating the player), a border ghost does not (it
    /// is never simulated, and it serves other cells' clients), and cell persistence carries them.</summary>
    [Fact]
    public void Handoff_and_persistence_carry_the_timers_and_a_ghost_does_not()
    {
        ReplicationRegistry registry = MoveProtocol.CreateRegistry();
        using var host = new ShardHost(cellSize: 10f, tickSeconds: Dt, registry, interestCellSize: 10f,
            overlapMargin: 2f, positionAccessor: PosAccessor);
        Entity e = host.SpawnOwned(9f, 5f, 7, out CellSim start);
        SetPlayer(start.World, e, new Vector3(9f, 1f, 5f));
        host.SpawnOwned(15f, 5f, 8, out CellSim east);   // makes the neighbour exist, so the border mirrors into it
        Assert.NotEqual(start.Coord, east.Coord);

        byte[] persisted = start.SnapshotOwned(new HashSet<long>());
        Assert.Contains(MoveProtocol.MovementOwnerTypeId, FrameIds(persisted, 7));

        host.SyncGhosts();
        Assert.True(east.TryGetGhost(7, out Entity ghost));
        Assert.True(east.World.Has<MovementState>(ghost));
        Assert.False(east.World.Has<MovementOwnerState>(ghost));

        start.World.Set(e, ReplicatedPosition.FromWorld(new Vector3(11f, 1f, 5f), WorldFrame.Origin));
        host.ProcessHandoffs();
        Assert.True(host.TryGetOwner(7, out CellSim destination, out Entity moved));
        Assert.Equal(east.Coord, destination.Coord);
        Assert.Equal(Timers, destination.World.Get<MovementOwnerState>(moved));

        using var restoredHost = new ShardHost(cellSize: 10f, tickSeconds: Dt, registry, interestCellSize: 10f);
        CellSim fresh = restoredHost.EnsureCell(start.Coord);
        fresh.RestoreOwned(persisted);
        Assert.True(fresh.TryGetOwned(7, out Entity restored));
        Assert.Equal(Timers, fresh.World.Get<MovementOwnerState>(restored));
    }

    private static void SetPlayer(World world, Entity e, Vector3 position)
    {
        world.Set(e, ReplicatedPosition.FromWorld(position, WorldFrame.Origin));
        world.Set(e, new MovementState { VerticalVelocity = 1f });
        world.Set(e, Timers);
    }

    /// <summary>
    /// A generation-11 cell blob, stamped, as every blob on disk reads on the first boot of this build: the timers sit
    /// inside its movement payload. The driver brings it forward by splitting them into an owner frame, placed after
    /// the entity's remaining built-ins and before its extension frames, so the restored player keeps its windows.
    /// </summary>
    [Fact]
    public void A_generation_11_blob_restores_its_timers_onto_the_owner_component()
    {
        const int Gen11 = 11;
        var movement = new MovementState { VerticalVelocity = -0.5f, Grounded = true, Swimming = true, FacingYawQ = 99 };
        var owner = new MovementOwnerState { TimeSinceGrounded = 0.125f, JumpBufferRemaining = 0.25f };
        byte[] extension = { 3, 1, 4, 1, 5, 9 };
        byte[] gen11 = new CellBlobFixtures.BodyBuilder()
            .Entity(5,
                (MoveProtocol.PositionTypeId, CellBlobFixtures.Position(Gen11, new Vector3(3f, 1f, 4f))),
                (MoveProtocol.MovementTypeId, CellBlobFixtures.Movement(Gen11, movement, owner)),
                (MoveProtocol.IdentityTypeId, CellBlobFixtures.Identity("Runner")),
                (ReplicationRegistry.FirstExtensionTypeId, CellBlobFixtures.Extension(extension)))
            .ToBody();

        byte[] current = BuiltinBlobLayout.NormalizeToCurrent(gen11, Gen11);

        byte[] expected = new CellBlobFixtures.BodyBuilder()
            .Entity(5,
                (MoveProtocol.PositionTypeId, CellBlobFixtures.Position(MoveProtocol.WireProtocolVersion, new Vector3(3f, 1f, 4f))),
                (MoveProtocol.MovementTypeId, CellBlobFixtures.Movement(MoveProtocol.WireProtocolVersion, movement)),
                (MoveProtocol.IdentityTypeId, CellBlobFixtures.Identity("Runner")),
                (MoveProtocol.MovementOwnerTypeId, CellBlobFixtures.MovementOwner(owner)),
                (ReplicationRegistry.FirstExtensionTypeId, CellBlobFixtures.Extension(extension)))
            .ToBody();
        Assert.Equal(expected, current);

        var world = new World();
        var view = new ClientReplicationView(MoveProtocol.CreateRegistry());
        view.Apply(world, current);
        Assert.True(view.TryGetEntity(5, out Entity e));
        Assert.Equal(owner, world.Get<MovementOwnerState>(e));
        MovementState back = world.Get<MovementState>(e);
        Assert.Equal(movement.VerticalVelocity, back.VerticalVelocity);
        Assert.True(back.Swimming);
        Assert.Equal(movement.FacingYawQ, back.FacingYawQ);
        Assert.Equal("Runner", world.Get<PlayerIdentity>(e).DisplayName);
    }

    /// <summary>An entity that steps without an owner component (one a consumer built by hand, or one from before the
    /// split) gains it at the end of that tick, so its coyote clock keeps counting instead of resetting every tick and
    /// re-opening the coyote window mid-air.</summary>
    [Fact]
    public void The_per_cell_step_seeds_a_missing_owner_component_and_carries_it_after()
    {
        var world = new World();
        var system = new PlayerMovementSystem(Flat, MoveTuning.Default);
        Entity e = world.Spawn();
        world.Set(e, new NetId(3));
        world.Set(e, ReplicatedPosition.FromWorld(new Vector3(0f, 5f, 0f), WorldFrame.Origin));
        world.Set(e, new MovementState());
        world.Set(e, new PendingMove { Command = MoveCommand.Idle });

        system.Update(world, Dt);
        Assert.True(world.TryGet(e, out MovementOwnerState first));
        Assert.True(first.TimeSinceGrounded > 0f);

        system.Update(world, Dt);
        MovementOwnerState second = world.Get<MovementOwnerState>(e);
        Assert.True(second.TimeSinceGrounded > first.TimeSinceGrounded, "the coyote clock must carry across ticks");
    }
}
