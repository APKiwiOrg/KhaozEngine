using System.Linq;
using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapPrefabBindingRefusalTests
{
    [Theory]
    [InlineData("entries", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("entry", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("child", MapDocumentRefusal.IdentityDuplicate)]
    [InlineData("world", MapDocumentRefusal.IdentityDuplicate)]
    public void Existing_bindings_distinguish_missing_values_and_repeated_identities(string fault, MapDocumentRefusal reason)
    {
        var f = PrefabFixtures.Create("roomless");
        var asset = f.Assets.GetPrefab("hut");
        var binding = new MapPrefabBinding(Assert.Single(asset.Children).Key, "b/item", null, false);
        var existing = new MapPrefabBindings(fault switch
        {
            "entries" => null!,
            "entry" => [null!],
            "child" => [binding, binding],
            _ => [binding, binding with { Child = new(MapPrefabChildKind.Placement, "other") }],
        });
        string before = MapDocumentFile.SaveText(f.Document), supplied = JsonSerializer.Serialize(existing);
        var error = Assert.Throws<MapDocumentException>(() => MapPrefabBindingPlanner.Plan(f.Document, asset, f.Assets, "b", existing));
        Assert.Equal(reason, error.Reason);
        Assert.Equal("b", error.InstanceId);
        if (fault == "child") Assert.Equal(binding.Child, error.Child);
        if (fault == "world") Assert.Equal((binding.WorldId, binding.Child.Kind), (error.WorldId, error.ChildKind));
        else Assert.Equal("$.bindings", error.FieldPath);
        Assert.Equal(before, MapDocumentFile.SaveText(f.Document));
        Assert.Equal(supplied, JsonSerializer.Serialize(existing));
        var accepted = MapPrefabBindingPlanner.Plan(f.Document, asset, f.Assets, "b", new([binding]));
        Assert.Equal(binding, Assert.Single(accepted.Bindings.Entries));
    }
}
