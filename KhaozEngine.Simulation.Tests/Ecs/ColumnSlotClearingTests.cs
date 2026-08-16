using System;
using System.Runtime.CompilerServices;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

// A component that carries a managed reference. Column<T> only requires T : struct, so this is legal, and it is
// the shape that makes a stale column slot a retention leak rather than a harmless stale number (#119).
file struct Held : IComponent { public object? Payload; }

file struct Plain : IComponent { public int V; }

/// <summary>
/// Pins the vacated-slot clearing in <see cref="Column{T}.SwapRemove"/> and in the tail-removal branch of
/// <see cref="Archetype.SwapRemove"/> (#119). Before the fix a removed row's data stayed reachable through
/// <c>Column&lt;T&gt;.Data</c> past <see cref="Archetype.Count"/>, so a reference-carrying component pinned
/// whatever it held for as long as the archetype existed.
/// </summary>
public class ColumnSlotClearingTests
{
    [Fact]
    public void SwapRemoveClearsTheVacatedTailSlot()
    {
        var reg = new ComponentRegistry();
        int id = reg.Id<Held>();
        var arch = new Archetype(new[] { id }, reg);
        var col = (Column<Held>)arch.Columns[id];

        int r0 = arch.AddRow(new Entity(0, 1));
        int r1 = arch.AddRow(new Entity(1, 1));
        col.Set(r0, new Held { Payload = "row0" });
        col.Set(r1, new Held { Payload = "row1" });

        Assert.True(arch.SwapRemove(r0, out Entity moved));
        Assert.Equal(new Entity(1, 1), moved);

        Assert.Equal("row1", col.Data[0].Payload);   // the surviving row moved down intact
        Assert.Null(col.Data[1].Payload);            // and the slot it came from no longer pins it
    }

    [Fact]
    public void RemovingTheLastRowClearsItsSlot()
    {
        var reg = new ComponentRegistry();
        int id = reg.Id<Held>();
        var arch = new Archetype(new[] { id }, reg);
        var col = (Column<Held>)arch.Columns[id];

        int r0 = arch.AddRow(new Entity(0, 1));
        col.Set(r0, new Held { Payload = "only" });

        Assert.False(arch.SwapRemove(r0, out _));    // tail removal: nothing moves down
        Assert.Equal(0, arch.Count);
        Assert.Null(col.Data[0].Payload);            // the vacated slot is still cleared
    }

    // The tail branch has no copy-down to piggyback on, so it is the branch most likely to be missed. A despawn
    // wave hits it once per archetype (the final entity), and a one-entity archetype hits it every time.
    [Fact]
    public void SwapRemoveDownToEmptyClearsEverySlot()
    {
        var reg = new ComponentRegistry();
        int id = reg.Id<Held>();
        var arch = new Archetype(new[] { id }, reg);
        var col = (Column<Held>)arch.Columns[id];

        for (int i = 0; i < 4; i++)
        {
            int r = arch.AddRow(new Entity(i, 1));
            col.Set(r, new Held { Payload = "e" + i });
        }
        while (arch.Count > 0) arch.SwapRemove(arch.Count - 1, out _);

        for (int i = 0; i < 4; i++) Assert.Null(col.Data[i].Payload);
    }

    // An unmanaged column takes the same code path with the clear compiled away, so its behaviour must be
    // unchanged: the copy-down still lands and nothing else about the column moves.
    [Fact]
    public void UnmanagedColumnStillSwapsTheLastRowDown()
    {
        var reg = new ComponentRegistry();
        int id = reg.Id<Plain>();
        var arch = new Archetype(new[] { id }, reg);
        var col = (Column<Plain>)arch.Columns[id];

        int r0 = arch.AddRow(new Entity(0, 1));
        int r1 = arch.AddRow(new Entity(1, 1));
        col.Set(r0, new Plain { V = 10 });
        col.Set(r1, new Plain { V = 20 });

        Assert.True(arch.SwapRemove(r0, out _));
        Assert.Equal(20, col.Data[0].V);
        Assert.Equal(1, arch.Count);
    }
}

/// <summary>
/// The retention half of #119, measured rather than inspected: after a despawn (or a component removal that moves
/// the entity out of the archetype), the object a reference-carrying component held must become collectable.
/// Enlisted in the AllocSensitive collection because these tests force full blocking collections, which is exactly
/// the GC churn that collection exists to keep away from the allocation-measuring tests.
/// </summary>
[Collection("AllocSensitive")]
public class ComponentRetentionTests
{
    [Fact]
    public void DespawnReleasesReferenceCarryingComponentData()
    {
        var w = new World();
        WeakReference weak = SpawnSetAndDespawn(w);
        Collect();
        Assert.False(weak.IsAlive);
    }

    [Fact]
    public void RemovingTheComponentReleasesItsData()
    {
        var w = new World();
        WeakReference weak = SpawnSetAndRemove(w);
        Collect();
        Assert.False(weak.IsAlive);
    }

    // The peak-then-shrink shape from the issue: a wave of entities despawns and never comes back, so the tail
    // slots are never revisited by a later spawn that would have overwritten them.
    [Fact]
    public void ADespawnedWaveReleasesEveryPayload()
    {
        var w = new World();
        WeakReference[] weak = SpawnWaveAndDespawn(w, 16);
        Collect();
        foreach (WeakReference r in weak) Assert.False(r.IsAlive);
    }

    // NoInlining keeps the payload local inside this frame: by the time the caller collects, the frame is gone, so
    // the only thing that could still be holding the object is the ECS itself. (A local in the calling method would
    // stay live to the end of that method under a debug JIT and make the test meaningless.)
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference SpawnSetAndDespawn(World w)
    {
        var payload = new object();
        Entity e = w.Spawn();
        w.Set(e, new Held { Payload = payload });
        w.Despawn(e);
        return new WeakReference(payload);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference SpawnSetAndRemove(World w)
    {
        var payload = new object();
        Entity e = w.Spawn();
        w.Set(e, new Held { Payload = payload });
        w.Remove<Held>(e);   // archetype move: the destination has no Held column, so the source row is vacated
        Assert.True(w.IsAlive(e));
        return new WeakReference(payload);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] SpawnWaveAndDespawn(World w, int count)
    {
        var entities = new Entity[count];
        var weak = new WeakReference[count];
        for (int i = 0; i < count; i++)
        {
            var payload = new object();
            entities[i] = w.Spawn();
            w.Set(entities[i], new Held { Payload = payload });
            weak[i] = new WeakReference(payload);
        }
        foreach (Entity e in entities) w.Despawn(e);
        return weak;
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
