using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileComponentCodecRefusalTests
{
    [Theory]
    [InlineData(true, TileComponentCodecRefusal.RouteStreamShortRead)]
    [InlineData(false, TileComponentCodecRefusal.NameStreamShortRead)]
    public void An_unbounded_stream_short_read_names_the_missing_bytes(bool route, TileComponentCodecRefusal reason)
    {
        void Check(int actualReadCount)
        {
            if (route) TileProtocol.RefuseRouteStreamShortRead(3, actualReadCount);
            else TileProtocol.RefuseNameStreamShortRead(3, actualReadCount);
        }

        Check(3);
        var refusal = Assert.Throws<TileComponentCodecDataException>(() => Check(2));
        Assert.Equal(reason, refusal.Reason);
        Assert.Equal(2, refusal.ActualReadCount);
        if (route) Assert.Equal(3, refusal.DeclaredCount);
        else Assert.Equal(3, refusal.DeclaredByteCount);
    }

    [Fact]
    public void An_instance_at_the_cap_round_trips_and_one_byte_more_is_refused()
    {
        var registry = TileProtocol.CreateRegistry();
        var server = new World();
        Entity entity = server.Spawn();
        server.Set(entity, new NetId(1));
        byte[] payload = Enumerable.Repeat((byte)7, TileProtocol.MaxInstancePayloadBytes).ToArray();
        server.Set(entity, new TileGroundItemInstance { InstanceId = 19, Payload = payload });

        byte[] Encode() => SnapshotWriter.WriteFiltered(server, registry, new HashSet<long> { 1 });

        var client = new World();
        new ClientReplicationView(registry).Apply(client, Encode());
        Entity got = client.Query().With<TileGroundItemInstance>().Entities().Single();
        Assert.Equal(19, client.Get<TileGroundItemInstance>(got).InstanceId);
        Assert.Equal(payload, client.Get<TileGroundItemInstance>(got).Payload);

        server.Set(entity, new TileGroundItemInstance
        {
            InstanceId = 19,
            Payload = new byte[TileProtocol.MaxInstancePayloadBytes + 1],
        });
        var refusal = Assert.Throws<TileComponentCodecArgumentException>(() => Encode());
        Assert.Equal(TileComponentCodecRefusal.InstanceWriteLimitExceeded, refusal.Reason);
        Assert.Equal("v", refusal.ParamName);
        Assert.Equal(TileProtocol.MaxInstancePayloadBytes + 1, refusal.ActualByteCount);
        Assert.Equal(TileProtocol.MaxInstancePayloadBytes, refusal.MaximumByteCount);
    }
}
