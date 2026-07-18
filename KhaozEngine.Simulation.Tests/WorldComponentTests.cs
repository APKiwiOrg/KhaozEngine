using System;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

file struct Position : IComponent { public int X, Y; }
file struct Velocity : IComponent { public int Dx; }
file struct Frozen : IComponent { }   // tag

public class WorldComponentTests
{
    [Fact]
    public void SetGetHasAndRefMutation()
    {
        var w = new World();
        var e = w.Spawn();
        w.Set(e, new Position { X = 1, Y = 2 });
        Assert.True(w.Has<Position>(e));
        Assert.False(w.Has<Velocity>(e));
        w.Get<Position>(e).X = 42;          // live ref
        Assert.Equal(42, w.Get<Position>(e).X);
        Assert.Equal(2, w.Get<Position>(e).Y);
    }

    [Fact]
    public void AddingASecondComponentPreservesTheFirst()
    {
        var w = new World();
        var e = w.Spawn();
        w.Set(e, new Position { X = 7, Y = 8 });
        w.Set(e, new Velocity { Dx = 5 });          // structural move to {Position,Velocity}
        Assert.Equal(7, w.Get<Position>(e).X);       // preserved across the move
        Assert.Equal(5, w.Get<Velocity>(e).Dx);
    }

    [Fact]
    public void RemoveMovesArchetypeAndDropsComponent()
    {
        var w = new World();
        var e = w.Spawn();
        w.Set(e, new Position { X = 1, Y = 1 });
        w.Set(e, new Velocity { Dx = 9 });
        w.Remove<Velocity>(e);
        Assert.False(w.Has<Velocity>(e));
        Assert.True(w.Has<Position>(e));
        Assert.Equal(1, w.Get<Position>(e).X);
    }

    [Fact]
    public void TagComponentTracksMembershipWithNoData()
    {
        var w = new World();
        var e = w.Spawn();
        w.Set(e, new Position());
        w.Set(e, new Frozen());
        Assert.True(w.Has<Frozen>(e));
        w.Remove<Frozen>(e);
        Assert.False(w.Has<Frozen>(e));
    }

    [Fact]
    public void TryGetReturnsValueOrFalse()
    {
        var w = new World();
        var e = w.Spawn();
        w.Set(e, new Position { X = 3, Y = 4 });
        Assert.True(w.TryGet<Position>(e, out var p));
        Assert.Equal(3, p.X);
        Assert.False(w.TryGet<Velocity>(e, out _));
    }

    // TryGet's contract is "copies out if present" with no tag carve-out, and the replication codecs
    // (TrySerialize / CaptureInto) call it on every registered component, so a present tag must copy out
    // its only value (default) rather than crash on the column lookup a tag doesn't have.
    [Fact]
    public void TryGetOnTagComponentReturnsPresenceWithoutThrowing()
    {
        var w = new World();
        var tagged = w.Spawn();
        var plain = w.Spawn();
        w.Set(tagged, new Frozen());
        w.Set(plain, new Position { X = 1, Y = 1 });

        Assert.True(w.TryGet<Frozen>(tagged, out _));    // present tag: true, no throw
        Assert.False(w.TryGet<Frozen>(plain, out _));    // absent tag: plain false
    }

    [Fact]
    public void StaleHandleOnGetAndSetThrowsAndDoesNotTouchRecycledEntity()
    {
        var w = new World();
        var stale = w.Spawn();
        w.Set(stale, new Position { X = 1 });
        w.Despawn(stale);
        var fresh = w.Spawn();                       // reuses stale.Id with a new version
        Assert.Equal(stale.Id, fresh.Id);

        Assert.Throws<InvalidOperationException>(() => w.Get<Position>(stale));
        Assert.Throws<InvalidOperationException>(() => w.Set(stale, new Position { X = 99 }));
        Assert.Throws<InvalidOperationException>(() => w.Add(stale, new Velocity { Dx = 1 }));
        Assert.Throws<InvalidOperationException>(() => w.Remove<Position>(stale));

        Assert.False(w.Has<Position>(fresh));        // the new entity was never written through the stale handle
    }

    [Fact]
    public void DespawnFromMultiEntityArchetypeKeepsOthersIntact()
    {
        var w = new World();
        var a = w.Spawn(); w.Set(a, new Position { X = 1 });
        var b = w.Spawn(); w.Set(b, new Position { X = 2 });
        var c = w.Spawn(); w.Set(c, new Position { X = 3 });
        w.Despawn(b);                                  // swap-remove inside the {Position} archetype
        Assert.True(w.IsAlive(a)); Assert.True(w.IsAlive(c));
        Assert.Equal(1, w.Get<Position>(a).X);
        Assert.Equal(3, w.Get<Position>(c).X);
    }
}
