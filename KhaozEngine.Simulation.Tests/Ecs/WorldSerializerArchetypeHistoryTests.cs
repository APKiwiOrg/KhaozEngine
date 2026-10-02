using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests.Ecs;

[ComponentId("history-value")]
public struct HistoryValue : IComponent { public int Value; }

[ComponentId("history-tag")]
public struct HistoryTag : IComponent { }

[ComponentId("history-other-tag")]
public struct HistoryOtherTag : IComponent { }

[JsonSourceGenerationOptions(IncludeFields = true)]
[JsonSerializable(typeof(HistoryValue))]
[JsonSerializable(typeof(HistoryTag))]
[JsonSerializable(typeof(HistoryOtherTag))]
internal partial class HistoryJsonContext : JsonSerializerContext { }

public sealed class WorldSerializerArchetypeHistoryTests
{
    [Fact]
    public void RepopulatingAnEmptyHistoricalSignaturePreservesRawOrderAndAllocatorState()
    {
        var uninterrupted = new World();
        Entity first = uninterrupted.Spawn();
        uninterrupted.Set(first, new HistoryValue { Value = 11 });
        uninterrupted.Set(first, new HistoryTag());
        Entity second = uninterrupted.Spawn();
        uninterrupted.Set(second, new HistoryValue { Value = 22 });
        uninterrupted.Set(second, new HistoryOtherTag());
        Entity hole = uninterrupted.Spawn();
        uninterrupted.Despawn(hole);
        uninterrupted.Despawn(first);

        World restored = Serializer().Load(Serializer().Save(uninterrupted));

        Assert.False(restored.IsAlive(first));
        Assert.True(restored.IsAlive(second));
        Assert.True(restored.Has<HistoryOtherTag>(second));
        Assert.Equal(22, restored.Get<HistoryValue>(second).Value);
        Entity continued = uninterrupted.Spawn();
        Entity resumed = restored.Spawn();
        Assert.Equal(new Entity(0, 2), continued);
        Assert.Equal(continued, resumed);
        uninterrupted.Set(continued, new HistoryValue { Value = 33 });
        uninterrupted.Set(continued, new HistoryTag());
        restored.Set(resumed, new HistoryValue { Value = 33 });
        restored.Set(resumed, new HistoryTag());

        Assert.Equal(new[] { continued, second }, RawOrder(uninterrupted));
        Assert.Equal(RawOrder(uninterrupted), RawOrder(restored));
        Assert.Equal(33, restored.Get<HistoryValue>(resumed).Value);
        Assert.True(restored.Has<HistoryTag>(resumed));
        Assert.False(restored.Has<HistoryOtherTag>(resumed));
        Assert.Equal(new Entity(2, 2), uninterrupted.Spawn());
        Assert.Equal(new Entity(2, 2), restored.Spawn());
        Assert.Equal(new Entity(3, 1), uninterrupted.Spawn());
        Assert.Equal(new Entity(3, 1), restored.Spawn());
    }

    [Fact]
    public void LoadingDoesNotRetainTemporarySignaturesAbsentFromTheOriginalHistory()
    {
        var uninterrupted = new World();
        Assert.False(uninterrupted.TryGet<HistoryValue>(default, out _));
        Entity existing = uninterrupted.Spawn();
        uninterrupted.Set(existing, new HistoryTag());
        uninterrupted.Set(existing, new HistoryValue { Value = 7 });
        World restored = Serializer().Load(Serializer().Save(uninterrupted));

        Entity continued = uninterrupted.Spawn();
        Entity resumed = restored.Spawn();
        uninterrupted.Set(continued, new HistoryValue { Value = 9 });
        restored.Set(resumed, new HistoryValue { Value = 9 });

        Assert.Equal(new[] { existing, continued }, RawOrder(uninterrupted));
        Assert.Equal(RawOrder(uninterrupted), RawOrder(restored));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HistoricalSnapshotsWithoutArchetypeMetadataRemainReadable(bool declaresVersion)
    {
        string version = declaresVersion ? "\"FormatVersion\":1," : string.Empty;
        string json = "{" + version +
            "\"NextId\":3,\"FreeIds\":[{\"Id\":1,\"Version\":4}],\"Entities\":[" +
            "{\"Id\":2,\"Version\":3,\"Components\":{\"history-value\":{\"Value\":12}}}]}";

        World restored = Serializer().Load(json);

        Entity existing = new(2, 3);
        Assert.Equal(new[] { existing }, RawOrder(restored));
        Assert.Equal(12, restored.Get<HistoryValue>(existing).Value);
        Assert.Equal(new Entity(1, 4), restored.Spawn());
        Assert.Equal(new Entity(3, 1), restored.Spawn());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[[\"history-value\"],[]]")]
    [InlineData("[[],[]]")]
    [InlineData("[[],null]")]
    [InlineData("[[],[null]]")]
    [InlineData("[[],[\"history-value\",\"history-value\"]]")]
    [InlineData("[[],[\"history-value\"],[\"history-value\"]]")]
    [InlineData("[[],[\"unregistered\"]]")]
    public void InvalidArchetypeMetadataFailsClosed(string archetypes)
    {
        string json = "{\"FormatVersion\":" + WorldSerializer.CurrentFormatVersion +
            ",\"NextId\":0,\"FreeIds\":[],\"Entities\":[],\"Archetypes\":" + archetypes + "}";

        Assert.Throws<InvalidOperationException>(() => Serializer().Load(json));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ExplicitNullHistoryDoesNotOptIntoLegacyLoading(int version)
    {
        string json = "{\"FormatVersion\":" + version +
            ",\"NextId\":0,\"FreeIds\":[],\"Entities\":[],\"Archetypes\":null}";

        Assert.Throws<InvalidOperationException>(() => Serializer().Load(json));
    }

    [Fact]
    public void MetadataCannotOmitALiveSignature()
    {
        string json = "{\"FormatVersion\":" + WorldSerializer.CurrentFormatVersion +
            ",\"NextId\":1,\"FreeIds\":[],\"Entities\":[" +
            "{\"Id\":0,\"Version\":1,\"Components\":{\"history-value\":{\"Value\":5}}}],\"Archetypes\":[[]]}";

        Assert.Throws<InvalidOperationException>(() => Serializer().Load(json));
    }

    [Fact]
    public void NonArrayArchetypeMetadataIsRejectedByTheEnvelopeCodec()
    {
        string json = "{\"FormatVersion\":" + WorldSerializer.CurrentFormatVersion +
            ",\"NextId\":0,\"FreeIds\":[],\"Entities\":[],\"Archetypes\":{}}";

        Assert.Throws<JsonException>(() => Serializer().Load(json));
    }

    [Fact]
    public void FormatTwoWithoutHistoryIsNotTreatedAsALegacySnapshot()
    {
        const string json = "{\"FormatVersion\":2,\"NextId\":0,\"FreeIds\":[],\"Entities\":[]}";

        Assert.Throws<InvalidOperationException>(() => Serializer().Load(json));
    }

    static WorldSerializer Serializer() => WorldSerializer.Create().Add<HistoryValue>().Add<HistoryTag>().Add<HistoryOtherTag>()
        .Build(HistoryJsonContext.Default.Options);

    static Entity[] RawOrder(World world) => world.Query().With<HistoryValue>().Entities().ToArray();
}
