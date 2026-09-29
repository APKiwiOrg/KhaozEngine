using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Sharding;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

internal sealed class PreparationDeliveryScenario : IDisposable
{
    internal sealed class CaptureTransport(INetTransport inner) : INetTransport
    {
        public readonly List<(NetConnectionId Connection, byte[] Data, NetChannelReliability Reliability)> Sent = new();
        public readonly List<NetConnectionId> Disconnected = new();
        public Action<NetConnectionId, byte[]>? AfterSend;
        public void Poll() => inner.Poll();
        public bool TryDequeueEvent(out NetEvent ev) => inner.TryDequeueEvent(out ev);
        public void Send(NetConnectionId connection, ReadOnlySpan<byte> data, NetChannelReliability reliability)
        {
            Sent.Add((connection, data.ToArray(), reliability));
            inner.Send(connection, data, reliability);
            AfterSend?.Invoke(connection, data.ToArray());
        }
        public void Disconnect(NetConnectionId connection) { Disconnected.Add(connection); inner.Disconnect(connection); }
        public void Disconnect(NetConnectionId connection, ReadOnlySpan<byte> reason)
        { Disconnected.Add(connection); inner.Disconnect(connection, reason); }
        public void Dispose() => inner.Dispose();
    }

    public readonly InMemoryTransportHub Hub = new();
    public readonly CaptureTransport Wire;
    public readonly TileWorldServer Server;
    public readonly PreparationScenario.FixedRules Rules = new();
    public readonly List<TileWorldClient> Clients = new();
    readonly List<INetTransport> transports = new();
    readonly TileWorldDocument document;
    readonly bool enabled;

    public PreparationDeliveryScenario(bool enabled = true, TileCoord? spawn = null, float radius = 15,
        TileCombatPreparationProfile? profile = null)
    {
        this.enabled = enabled;
        var profiles = new PreparationScenario.Profiles();
        if (profile is { } chosen) profiles.Current = chosen;
        document = TileMoveSimulatorTests.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0));
        Wire = new CaptureTransport(Hub.Server);
        Server = new TileWorldServer(Wire, TileWorldServerTickTests.Config(spawn ?? new TileCoord(20, 20, 0)) with
        {
            InterestRadius = radius,
            CombatPreparationRules = enabled ? profiles : null
        }, TileMoveSimulatorTests.Bake(document), new TileDocumentTargets(document, TileMoveSimulatorTests.Catalogs),
            new AllowAllAuthenticator());
        Server.CombatRules = Rules;
        AddClient();
    }

    public TileWorldClient Client => Clients[0];
    public TileWorldClient AddClient()
    {
        INetTransport transport = Hub.CreateClient();
        transports.Add(transport);
        var client = new TileWorldClient(transport, new TileWorldClientConfig
        {
            TickSeconds = .25f,
            StepTicks = new TileStepTicks(4, 2),
            CombatPreparationEnabled = enabled
        }, TileMoveSimulatorTests.Bake(document));
        Clients.Add(client);
        client.Tick(.06f);
        client.Poll();
        Server.Poll();
        Step();
        return client;
    }

    public (long Attacker, long Target) Fight(TileCoord? tile = null)
    {
        TileCoord at = tile ?? new TileCoord(20, 21, 0);
        long attacker = Server.SpawnActor(at, new TileActorSpawn(1000, 14, TileDirection.N));
        long target = Server.SpawnActor(new TileCoord(at.X, at.Z + 1, at.Plane), new TileActorSpawn(1000, 14, TileDirection.S));
        TileCombatResolveTests.Lock(Server, attacker, target);
        return (attacker, target);
    }

    public void Step(bool poll = true)
    {
        Server.Tick(.25f);
        if (poll) foreach (TileWorldClient client in Clients) client.Poll();
    }
    public void Through(long tick, bool poll = true) { while (Server.TickCount <= tick) Step(poll); }
    public void Move(long id, TileCoord tile)
    {
        Assert.True(Server.Host.TryGetOwner(id, out CellSim cell, out Entity entity));
        Assert.True(cell.World.TryGet(entity, out TileMoveState state));
        state.Tile = tile;
        state.StepFrom = tile;
        cell.World.Set(entity, state);
    }
    public byte[][] Payloads(int connection = 1) => Wire.Sent
        .Where(x => x.Connection.Value == connection && SessionFrame.ReadOpcode(x.Data) == SessionOpcode.Data)
        .Select(x => SessionFrame.ReadBody(x.Data)).ToArray();
    public void Inject(byte[] payload, int connection = 1) => Hub.Server.Send(new NetConnectionId(connection),
        SessionFrame.Write(SessionOpcode.Data, payload), NetChannelReliability.ReliableOrdered);
    public void Dispose()
    {
        foreach (TileWorldClient client in Clients) client.Dispose();
        Server.Dispose();
        foreach (INetTransport transport in transports) transport.Dispose();
    }
}
