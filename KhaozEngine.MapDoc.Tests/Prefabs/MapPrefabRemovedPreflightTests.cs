using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapPrefabRemovedPreflightTests
{
    [Theory]
    [InlineData("missing-version", false)]
    [InlineData("hut", true)]
    [InlineData("mesh", false)]
    public void RemovedInstance_WithoutExactVersionChargesPersistedInventory(string resourceId, bool wrongSha)
    {
        var f = PrefabFixtures.Create("roomless");
        var original = f.Document.Prefabs!.Instances.Single();
        var instance = original with
        {
            Removed = true,
            ResourceId = resourceId,
            ResourceSha256 = wrongSha ? new string('b', 64) : original.ResourceSha256,
            Overrides = new MapPrefabOverride[] { new MapPrefabPlacementOverride(original.Bindings.Entries.Single().Child, null, "missing-leaf", null) }
        };
        f.Document.Prefabs = new(1, 1, new[] { instance with { Unpacked = true } }, []);
        var inventory = MapPrefabPreflight.Count(f.Document, f.Assets, new());
        f.Document.Prefabs = new(1, 1, new[] { instance }, []);
        Assert.Equal(inventory, MapPrefabPreflight.Count(f.Document, f.Assets, new()));
        Assert.Equal(instance.Bindings.Entries.Count, inventory.Bindings);
        Assert.Throws<MapDocumentException>(() => MapPrefabResolver.Expand(f.Document, f.Assets, new(MaxBindings: checked((int)inventory.Bindings - 1))));
        Assert.Empty(MapPrefabResolver.Expand(f.Document, f.Assets).Placements);
    }

    [Fact]
    public void RemovedInstance_WithExactVersionKeepsRetainedBaseWorkBudget()
    {
        var f = PrefabFixtures.PreflightRich();
        var existing = MapPrefabPreflight.Count(f.Document, f.Assets, new());
        var removed = f.Document.Prefabs!.Instances.Single(i => i.Removed);
        f.Document.Prefabs = new(1, 1,
            f.Document.Prefabs.Instances.Select(i => i.Id == removed.Id ? i with { Unpacked = true } : i).ToArray(), f.Document.Prefabs.Transfers)
        { Attachments = f.Document.Prefabs.Attachments };
        var inventoryOnly = MapPrefabPreflight.Count(f.Document, f.Assets, new());
        Assert.True(existing.Children > inventoryOnly.Children);
        Assert.True(existing.GeometryElements > inventoryOnly.GeometryElements);
        Assert.Equal(existing.Bindings, inventoryOnly.Bindings);
    }
}
