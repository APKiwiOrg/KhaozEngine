using System;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

public class RegistrationInspectionTests
{
    struct Sample : IComponent { }
    struct Other : IComponent { }

    [Theory]
    [InlineData(ReplicationChannels.None)]
    [InlineData(ReplicationChannels.Migrate)]
    [InlineData(ReplicationChannels.Default)]
    [InlineData(ReplicationChannels.Default | ReplicationChannels.OwnerOnly)]
    public void Typed_inspection_requires_the_exact_id_type_and_channels_without_executing_codecs(ReplicationChannels channels)
    {
        const ushort id = ReplicationRegistry.FirstExtensionTypeId;
        var registry = new ReplicationRegistry();
        Assert.False(registry.IsRegistered<Sample>(id, channels));
        registry.Register<Sample>(id,
            (_, _) => throw new InvalidOperationException("Inspection must not serialize."),
            _ => throw new InvalidOperationException("Inspection must not deserialize."), channels: channels);

        Assert.True(registry.IsRegistered(id));
        Assert.True(registry.IsRegistered<Sample>(id, channels));
        Assert.False(registry.IsRegistered<Other>(id, channels));
        Assert.False(registry.IsRegistered<Sample>(id + 1, channels));
        Assert.False(registry.IsRegistered<Sample>(id, channels ^ ReplicationChannels.Persist));
        Assert.True(registry.IsRegistered<Sample>(id, channels));
    }
}
