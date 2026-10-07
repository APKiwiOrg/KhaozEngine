using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class WaterExcursionRenderStateTests
{
    [Theory]
    [InlineData(WaterExcursionState.Surface)]
    [InlineData(WaterExcursionState.AirborneFromWater)]
    public void ReplicatedExcursionReachesTheRenderSnapshotAndClearsOnFooting(WaterExcursionState excursion)
    {
        var (serverTransport, clientTransport) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30, InterestRadius = 100, MaxPlayers = 2 };
        var server = new WorldServer(serverTransport, config, (_, _) => 0, MoveTuning.Default);
        using var client = new WorldClient(clientTransport, (_, _) => 0, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = config.TickSeconds });
        for (int tick = 0; tick < 6; tick++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.True(client.Joined);
        Entity remote = default;
        long id = server.SpawnEntity(2, 2, (world, entity) =>
        {
            remote = entity;
            world.Set(entity, new MovementState { WaterExcursion = excursion, Swimming = excursion == WaterExcursionState.Surface });
        });
        for (int tick = 0; tick < 8; tick++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        EntityRenderState rendered = client.Snapshot().Single(entity => entity.Id.Value == id);
        Assert.False(rendered.IsLocal);
        Assert.Equal(excursion, Read(rendered));
        Assert.Equal(excursion == WaterExcursionState.Surface, rendered.Swimming);
        server.World.Set(remote, new MovementState { Grounded = true, WaterExcursion = WaterExcursionState.None });
        for (int tick = 0; tick < 8; tick++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        rendered = client.Snapshot().Single(entity => entity.Id.Value == id);
        Assert.Equal(WaterExcursionState.None, Read(rendered));
        Assert.True(rendered.Grounded);
    }

    [Fact]
    public void ExistingRenderConstructorsDefaultToNoWaterExcursion()
    {
        var state = new EntityRenderState(new NetId(1), Vector3.Zero, false, "name", false, 3, false, 0, 0, 0, 1);
        Assert.Equal(WaterExcursionState.None, Read(state));
        Assert.Equal(1f, state.FacingYaw);
        Assert.Equal(3f, state.VerticalVelocity);
    }

    static WaterExcursionState Read(EntityRenderState state)
    {
        PropertyInfo? property = typeof(EntityRenderState).GetProperty("WaterExcursion");
        Assert.NotNull(property);
        return Assert.IsType<WaterExcursionState>(property.GetValue(state));
    }
}
