using System;
using System.Linq;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests;

// #1771: both client heads must bound retained entity generations at their receive boundary.
public abstract class ClientWorldTickContract
{
    protected abstract ClientHead CreateClient(INetTransport transport, ReplicationRegistry registry, bool? advance);
    protected abstract byte[] Frame(byte[] snapshot);

    protected sealed record ClientHead(World World, Action Poll, Func<bool> Joined, IDisposable Lifetime) : IDisposable
    {
        public void Dispose() => Lifetime.Dispose();
    }

    private readonly record struct Tracked(int Value) : IComponent;

    [Fact]
    public void Area_of_interest_turnover_retains_only_the_latest_poll_including_dead_versions()
    {
        using var session = new Session(this);
        Entity previous = default;
        for (int cycle = 0; cycle < 64; cycle++)
        {
            session.Send(present: true, cycle);
            session.Head.Poll();
            Entity current = Assert.Single(session.World.Added<Tracked>());
            if (cycle > 0) Assert.NotEqual(previous, current);
            Assert.Empty(session.World.Changed<Tracked>());
            Assert.Empty(session.World.Removed<Tracked>());
            Assert.Equal((2, 2, 0, 0, 0), session.World.ChangeTrackingCountsForTests);

            session.Send(present: true, cycle + 1);
            session.Head.Poll();
            Assert.Empty(session.World.Added<Tracked>());
            Assert.Equal(current, Assert.Single(session.World.Changed<Tracked>()));
            Assert.Equal((0, 0, 1, 1, 0), session.World.ChangeTrackingCountsForTests);

            session.Send(present: false);
            session.Head.Poll();
            Assert.Equal(current, Assert.Single(session.World.Removed<Tracked>()));
            Assert.False(session.World.IsAlive(current));
            Assert.Empty(session.World.Added<Tracked>());
            Assert.Empty(session.World.Changed<Tracked>());
            Assert.Equal((0, 0, 0, 0, 2), session.World.ChangeTrackingCountsForTests);
            previous = current;
        }
    }

    [Fact]
    public void Poll_keeps_all_its_ingests_and_events_until_the_next_even_empty_poll()
    {
        using var session = new Session(this);
        session.World.Emit(-1);
        session.Send(present: true, 7);
        session.Send(present: true, 8);
        ulong before = session.World.Tick;
        session.Head.Poll();
        Assert.Equal(before + 1, session.World.Tick);
        Entity entity = Assert.Single(session.World.Added<Tracked>());
        Assert.Equal(entity, Assert.Single(session.World.Changed<Tracked>()));
        Assert.Equal(8, session.World.Get<Tracked>(entity).Value);
        Assert.Equal(new[] { 7, 8 }, session.World.Events<int>().ToArray());

        // Work between polls belongs to the same observation window, however long the caller waits.
        session.World.MarkChanged<Tracked>(entity);
        session.World.Emit(9);
        Assert.Equal(before + 1, session.World.Tick);
        Assert.Equal(entity, Assert.Single(session.World.Changed<Tracked>()));
        Assert.Equal(new[] { 7, 8, 9 }, session.World.Events<int>().ToArray());

        session.Head.Poll();
        Assert.Equal(before + 2, session.World.Tick);
        Assert.Equal((0, 0, 0, 0, 0), session.World.ChangeTrackingCountsForTests);
        Assert.Empty(session.World.Events<int>());
        // A second drain in one render frame is still a boundary, even with no elapsed time or packets.
        session.Head.Poll();
        Assert.Equal(before + 3, session.World.Tick);
    }

    [Fact]
    public void Caller_owned_tick_opt_out_preserves_tracking_while_the_default_clears_it()
    {
        using var automatic = new Session(this);
        using var manual = new Session(this, advance: false);
        automatic.Send(present: true, 3);
        manual.Send(present: true, 3);
        automatic.Head.Poll();
        manual.Head.Poll();
        Assert.Single(automatic.World.Added<Tracked>());
        Assert.Single(manual.World.Added<Tracked>());
        ulong automaticTick = automatic.World.Tick;
        ulong manualTick = manual.World.Tick;

        automatic.Head.Poll();
        manual.Head.Poll();
        Assert.Empty(automatic.World.Added<Tracked>());
        Assert.Empty(automatic.World.Events<int>());
        Assert.Equal(automaticTick + 1, automatic.World.Tick);
        Assert.Single(manual.World.Added<Tracked>());
        Assert.Equal(new[] { 3 }, manual.World.Events<int>().ToArray());
        Assert.Equal(manualTick, manual.World.Tick);
        manual.World.AdvanceTick();
        Assert.Empty(manual.World.Added<Tracked>());
        Assert.Empty(manual.World.Events<int>());
    }

    private sealed class Session : IDisposable
    {
        readonly ClientWorldTickContract contract;
        readonly LoopbackTransport serverTransport;
        readonly LoopbackTransport clientTransport;
        readonly NetServer server;
        readonly ReplicationRegistry registry;
        readonly World source = new();
        Entity? sourceEntity;
        public ClientHead Head { get; }
        public World World => Head.World;

        public Session(ClientWorldTickContract contract, bool? advance = null)
        {
            this.contract = contract;
            (serverTransport, clientTransport) = LoopbackTransport.CreatePair();
            server = new NetServer(serverTransport, 1, new AllowAllAuthenticator());
            World? receivingWorld = null;
            registry = new ReplicationRegistry();
            registry.Register<Tracked>(ReplicationRegistry.FirstExtensionTypeId,
                (value, writer) => writer.Write(value.Value), reader =>
                {
                    int value = reader.ReadInt32();
                    receivingWorld?.Emit(value);
                    return new Tracked(value);
                });
            Head = contract.CreateClient(clientTransport, registry, advance);
            receivingWorld = World;
            Head.Poll();
            server.Poll();
            Head.Poll();
            Assert.True(Head.Joined());
        }

        public void Send(bool present, int value = 0)
        {
            if (present)
            {
                if (!sourceEntity.HasValue)
                {
                    sourceEntity = source.Spawn();
                    source.Set(sourceEntity.Value, new NetId(41));
                }
                source.Set(sourceEntity.Value, new Tracked(value));
            }
            else if (sourceEntity.HasValue)
            {
                source.Despawn(sourceEntity.Value);
                sourceEntity = null;
            }
            server.SendTo(0, contract.Frame(SnapshotWriter.Write(source, registry)),
                NetChannelReliability.ReliableOrdered);
        }

        public void Dispose()
        {
            Head.Dispose();
            clientTransport.Dispose();
            serverTransport.Dispose();
        }
    }
}
