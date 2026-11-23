using System.Collections.Generic;
using System.IO;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

public class LegacyDeltaCodecOrderTests
{
    private readonly record struct First(int Value) : IComponent;
    private readonly record struct Second(int Value) : IComponent;

    [Fact]
    public void ChangedComponentsAndRemovalsFollowRegistrationOrder()
    {
        const ushort first = ReplicationRegistry.FirstExtensionTypeId + 2;
        const ushort second = ReplicationRegistry.FirstExtensionTypeId;
        var registry = new ReplicationRegistry();
        registry.Register<First>(first, static (c, bw) => bw.Write(c.Value), static br => new First(br.ReadInt32()));
        registry.Register<Second>(second, static (c, bw) => bw.Write(c.Value), static br => new Second(br.ReadInt32()));
        var world = new World();
        Entity entity = world.Spawn();
        world.Set(entity, new NetId(1L));
        world.Set(entity, new First(11));
        world.Set(entity, new Second(22));
        var interest = new HashSet<long> { 1L };
        var writer = new AoiDeltaReplicator(registry);

        writer.BeginTick();
        using (var full = new BinaryReader(new MemoryStream(writer.WriteFor(0, world, interest))))
        {
            full.BaseStream.Position = 25;
            Assert.Equal(0, full.ReadInt32());
            Assert.Equal(first, full.ReadUInt16());
            Assert.Equal(4, full.Read7BitEncodedInt());
            Assert.Equal(11, full.ReadInt32());
            Assert.Equal(second, full.ReadUInt16());
            Assert.Equal(4, full.Read7BitEncodedInt());
            Assert.Equal(22, full.ReadInt32());
            Assert.Equal((ushort)0, full.ReadUInt16());
        }

        world.Remove<First>(entity);
        world.Remove<Second>(entity);
        writer.BeginTick();
        using var delta = new BinaryReader(new MemoryStream(writer.WriteFor(0, world, interest)));
        delta.BaseStream.Position = 25;
        Assert.Equal(2, delta.ReadInt32());
        Assert.Equal(first, delta.ReadUInt16());
        Assert.Equal(second, delta.ReadUInt16());
        Assert.Equal((ushort)0, delta.ReadUInt16());
    }
}
