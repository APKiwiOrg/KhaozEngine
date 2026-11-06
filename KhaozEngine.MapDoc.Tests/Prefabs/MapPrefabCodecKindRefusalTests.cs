using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapPrefabCodecKindRefusalTests
{
    sealed record UnsupportedChild(MapPrefabChildKey ChildKey) : MapPrefabChild(ChildKey);
    sealed record UnsupportedOverride(MapPrefabChildKey ChildKey) : MapPrefabOverride(ChildKey);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownDiscriminators_RetainTheSubmittedKindAndChild(bool child)
    {
        MapPrefabChildKey key = new(MapPrefabChildKind.Placement, "child");
        MapPrefabChild validChild = new MapPrefabPlacementChild(key, new() { Id = "child", Kind = "mesh", AssetId = "asset", Scale = 1 });
        var validOverride = new MapPrefabRemoveOverride(key, null);
        byte[] bytes = child ? MapPrefabJson.Bytes(validChild) : MapPrefabJson.Bytes<MapPrefabOverride>(validOverride);
        var root = JsonNode.Parse(bytes)!.AsObject();
        root["type"] = "Future";
        string before = root.ToJsonString();
        var error = Assert.Throws<MapDocumentException>(() => Read(before));
        Assert.Equal(MapDocumentRefusal.OperationKindUnsupported, error.Reason);
        Assert.Equal("Future", error.ActualText);
        Assert.Equal("$.type", error.FieldPath);
        Assert.Equal(key, error.Child);
        Assert.Equal(before, root.ToJsonString());
        Read(Encoding.UTF8.GetString(bytes));

        void Read(string json)
        {
            if (child) Assert.Equal(key, MapPrefabJson.ReadChild(json).Key);
            else Assert.Equal(key, MapPrefabJson.ReadOverride(json).Child);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsupportedRuntimeKinds_RetainTheConcreteTypeAndLeaveTheSourceUntouched(bool child)
    {
        MapPrefabChildKey key = new(MapPrefabChildKind.Storey, "child");
        MapPrefabChild invalidChild = new UnsupportedChild(key);
        MapPrefabOverride invalidOverride = new UnsupportedOverride(key);
        string before = child ? JsonSerializer.Serialize(invalidChild) : JsonSerializer.Serialize(invalidOverride);
        var error = Assert.Throws<MapDocumentException>(() =>
        {
            if (child) MapPrefabJson.Bytes(invalidChild);
            else MapPrefabJson.Bytes(invalidOverride);
        });
        Assert.Equal(MapDocumentRefusal.OperationKindUnsupported, error.Reason);
        Assert.Equal(child ? typeof(UnsupportedChild) : typeof(UnsupportedOverride), error.RecordType);
        Assert.Equal(key, error.Child);
        Assert.Equal(before, child ? JsonSerializer.Serialize(invalidChild) : JsonSerializer.Serialize(invalidOverride));
        Assert.NotEmpty(child ? MapPrefabJson.Bytes<MapPrefabChild>(new MapPrefabStoreyChild(key, "Storey"))
            : MapPrefabJson.Bytes<MapPrefabOverride>(new MapPrefabRemoveOverride(key, null)));
    }
}
