using System;
using System.Linq;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

public class HierarchyTests
{
    [Fact]
    public void SetParentLinksBothDirections()
    {
        var w = new World();
        var p = w.Spawn(); var c = w.Spawn();
        w.SetParent(c, p);
        Assert.Equal(p, w.GetParent(c));
        Assert.Equal(new[] { c }, w.Children(p).ToArray());
        Assert.Null(w.GetParent(p));
        Assert.Empty(w.Children(c));
    }

    [Fact]
    public void ReParentMovesChildBetweenParents()
    {
        var w = new World();
        var p1 = w.Spawn(); var p2 = w.Spawn(); var c = w.Spawn();
        w.SetParent(c, p1);
        w.SetParent(c, p2);
        Assert.Equal(p2, w.GetParent(c));
        Assert.Empty(w.Children(p1));
        Assert.Equal(new[] { c }, w.Children(p2).ToArray());
    }

    [Fact]
    public void SelfParentAndCyclesThrow()
    {
        var w = new World();
        var a = w.Spawn(); var b = w.Spawn();
        Assert.Throws<ArgumentException>(() => w.SetParent(a, a));
        w.SetParent(b, a);                                   // a -> b
        Assert.Throws<InvalidOperationException>(() => w.SetParent(a, b)); // would cycle
    }

    [Fact]
    public void SetParentToDeadParentThrows()
    {
        var w = new World();
        var p = w.Spawn(); var c = w.Spawn();
        w.Despawn(p);
        Assert.Throws<ArgumentException>(() => w.SetParent(c, p));
    }

    [Fact]
    public void DetachOrphansChild()
    {
        var w = new World();
        var p = w.Spawn(); var c = w.Spawn();
        w.SetParent(c, p);
        w.Detach(c);
        Assert.Null(w.GetParent(c));
        Assert.Empty(w.Children(p));
        w.Detach(c);                                         // no-op on a root
    }

    [Fact]
    public void DespawnDetachesChildrenToRootAndUnlinksFromParent()
    {
        var w = new World();
        var grand = w.Spawn(); var p = w.Spawn(); var c = w.Spawn();
        w.SetParent(p, grand);
        w.SetParent(c, p);
        w.Despawn(p);
        Assert.True(w.IsAlive(c));                           // child survives
        Assert.Null(w.GetParent(c));                         // ... as a root
        Assert.Empty(w.Children(grand));                     // p unlinked from its parent
    }

    [Fact]
    public void DespawnTreeRemovesWholeSubtree()
    {
        var w = new World();
        var root = w.Spawn(); var a = w.Spawn(); var b = w.Spawn(); var leaf = w.Spawn();
        w.SetParent(a, root);
        w.SetParent(b, root);
        w.SetParent(leaf, a);
        w.DespawnTree(root);
        Assert.False(w.IsAlive(root));
        Assert.False(w.IsAlive(a));
        Assert.False(w.IsAlive(b));
        Assert.False(w.IsAlive(leaf));
    }
}
