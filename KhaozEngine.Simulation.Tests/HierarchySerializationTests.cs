using System.Linq;
using System.Text.Json.Nodes;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

public struct HsTag : IComponent { public int V; }

public class HierarchySerializationTests
{
    [Fact]
    public void HierarchyRoundTripsAndIndexIsRebuilt()
    {
        var w = new World();
        var root = w.Spawn(); var a = w.Spawn(); var leaf = w.Spawn();
        w.Set(root, new HsTag { V = 1 });
        w.SetParent(a, root);
        w.SetParent(leaf, a);

        var ser = new WorldSerializer(typeof(HsTag));        // Parent is auto-included
        World loaded = ser.Load(ser.Save(w));

        Assert.Equal(root, loaded.GetParent(a));             // links restored
        Assert.Equal(a, loaded.GetParent(leaf));
        Assert.Equal(new[] { a }, loaded.Children(root).ToArray());   // index rebuilt
        Assert.Equal(new[] { leaf }, loaded.Children(a).ToArray());

        loaded.DespawnTree(root);                            // rebuilt index drives the cascade
        Assert.False(loaded.IsAlive(a));
        Assert.False(loaded.IsAlive(leaf));
    }

    [Fact]
    public void LoadDropsAChildWhoseSavedParentHandleIsStale()
    {
        var w = new World();
        var root = w.Spawn(); var child = w.Spawn();
        w.Set(root, new HsTag { V = 1 });
        w.SetParent(child, root);

        var ser = new WorldSerializer(typeof(HsTag));
        // Corrupt save: the child's Parent keeps root's id but carries a version root never had, so the handle is
        // dead while its bare id still names a live entity. Indexing it would hang child off the real root.
        JsonNode save = JsonNode.Parse(ser.Save(w))!;
        JsonNode staleParent = FindParentValue(save, child.Id);
        staleParent["Version"] = staleParent["Version"]!.GetValue<uint>() + 1;

        World loaded = ser.Load(save.ToJsonString());

        Assert.True(loaded.IsAlive(root));
        Assert.True(loaded.IsAlive(child));
        Assert.Empty(loaded.Children(root));                 // never filed under the live id
        Assert.Null(loaded.GetParent(child));                // dangling link dropped: the child loads as a root

        loaded.DespawnTree(root);                            // and no cascade into an entity that is not in the tree
        Assert.False(loaded.IsAlive(root));
        Assert.True(loaded.IsAlive(child));
    }

    private static JsonNode FindParentValue(JsonNode save, int entityId)
    {
        JsonNode? found = null;
        foreach (JsonNode? entity in save["Entities"]!.AsArray())
            if (entity!["Id"]!.GetValue<int>() == entityId)
                found = entity["Components"]![typeof(Parent).FullName!]!["Value"];
        Assert.NotNull(found);
        return found;
    }
}
