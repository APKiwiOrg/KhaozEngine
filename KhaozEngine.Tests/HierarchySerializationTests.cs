using System.Linq;
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
}
