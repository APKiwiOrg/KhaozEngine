using System.Collections.Generic;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

file struct Pos : IComponent { public int X; }
file struct Vel : IComponent { public int Dx; }
file struct Doomed : IComponent { public int V; }
file struct C3 : IComponent { public int V; }
file struct C4 : IComponent { public int V; }
file struct C5 : IComponent { public int V; }
file struct C6 : IComponent { public int V; }
file struct C7 : IComponent { public int V; }

/// <summary>
/// The serial iteration contract (#118): a structural change made DIRECTLY from inside a <c>ForEach</c> action or an
/// <c>Entities()</c> loop body is refused with <see cref="StructuralChangeDuringIterationException"/> rather than
/// silently corrupting the pass. Before the guard the loop cached <c>a.Count</c> once, so a swap-remove underneath
/// it skipped one entity and revisited a vacated row, and a growing archetype detached the in-flight action's ref
/// parameters from the resized column. The deferred path (<c>World.Commands</c> / an <c>EntityCommandBuffer</c>)
/// stays legal and is what callers are pointed at, and reading or writing components mid-iteration is untouched.
/// </summary>
public class StructuralChangeGuardTests
{
    private static World WorldWith(int count)
    {
        var w = new World();
        for (int i = 0; i < count; i++)
        {
            Entity e = w.Spawn();
            w.Set(e, new Pos { X = i });
            w.Set(e, new Vel { Dx = i });
        }
        return w;
    }

    // ---- refused: the four structural changes ----

    [Fact]
    public void DespawnInsideForEachThrows()
    {
        World w = WorldWith(4);
        var ex = Assert.Throws<StructuralChangeDuringIterationException>(
            () => w.ForEach((Entity e, ref Pos _) => w.Despawn(e)));
        Assert.Equal("Despawn", ex.Operation);
    }

    [Fact]
    public void SpawnInsideForEachThrows()
    {
        World w = WorldWith(4);
        var ex = Assert.Throws<StructuralChangeDuringIterationException>(
            () => w.ForEach((Entity _, ref Pos _) => w.Spawn()));
        Assert.Equal("Spawn", ex.Operation);
    }

    [Fact]
    public void AddingAComponentInsideForEachThrows()
    {
        World w = WorldWith(4);
        var ex = Assert.Throws<StructuralChangeDuringIterationException>(
            () => w.ForEach((Entity e, ref Pos _) => w.Set(e, new Doomed { V = 1 })));
        Assert.Equal("Set/Add", ex.Operation);   // adding a component moves the entity to another archetype
    }

    [Fact]
    public void RemovingAComponentInsideForEachThrows()
    {
        World w = WorldWith(4);
        var ex = Assert.Throws<StructuralChangeDuringIterationException>(
            () => w.ForEach((Entity e, ref Pos _) => w.Remove<Vel>(e)));
        Assert.Equal("Remove", ex.Operation);
    }

    // The whole point of the guard is that it fires AT the offending row, before the corrupted rows are read: the
    // row that despawned is the last one the action ever sees.
    [Fact]
    public void TheGuardStopsTheLoopAtTheOffendingRow()
    {
        World w = WorldWith(8);
        int visited = 0;
        Assert.Throws<StructuralChangeDuringIterationException>(
            () => w.ForEach((Entity e, ref Pos _) => { visited++; w.Despawn(e); }));
        Assert.Equal(1, visited);
    }

    [Fact]
    public void StructuralChangeInsideAnEntitiesLoopThrows()
    {
        World w = WorldWith(4);
        Assert.Throws<StructuralChangeDuringIterationException>(() =>
        {
            foreach (Entity e in w.Query().With<Pos>().Entities()) w.Despawn(e);
        });
    }

    /// <summary>
    /// The <c>Entities()</c> check has to follow the yield rather than precede it, and this is the pass that tells
    /// the two apart. The loop bound is the LIVE archetype count, so a despawn from the body shrinks it: with the
    /// check sitting before the yield, a two-entity pass yields the first entity, the body despawns it, the bound
    /// drops to 1, the index reaches 1, and the enumerator ends normally having never run the check again. It
    /// returns cleanly, one entity silently skipped, which is the exact failure #118 exists to refuse and which
    /// <c>ForEach</c> (checking after every action) already refused. Both forms must stop at the offending row.
    /// </summary>
    [Fact]
    public void EntitiesRefusesADespawnThatEmptiesTheArchetype()
    {
        World w = WorldWith(2);
        int visited = 0;
        Assert.Throws<StructuralChangeDuringIterationException>(() =>
        {
            foreach (Entity e in w.Query().With<Pos>().Entities()) { visited++; w.Despawn(e); }
        });
        Assert.Equal(1, visited);
    }

    [Fact]
    public void ForEachRefusesTheSameArchetypeEmptyingDespawn()
    {
        World w = WorldWith(2);
        int visited = 0;
        Assert.Throws<StructuralChangeDuringIterationException>(
            () => w.ForEach((Entity e, ref Pos _) => { visited++; w.Despawn(e); }));
        Assert.Equal(1, visited);
    }

    // Every arity got the guard, not only the one the issue quoted. T2 and T8 bracket the range.
    [Fact]
    public void TheGuardCoversTheTwoComponentOverload()
    {
        World w = WorldWith(4);
        Assert.Throws<StructuralChangeDuringIterationException>(
            () => w.ForEach((Entity e, ref Pos _, ref Vel _) => w.Despawn(e)));
    }

    [Fact]
    public void TheGuardCoversTheEightComponentOverload()
    {
        var w = new World();
        for (int i = 0; i < 3; i++)
        {
            Entity e = w.Spawn();
            w.Set(e, new Pos { X = i });
            w.Set(e, new Vel { Dx = i });
            w.Set(e, new Doomed { V = i });
            w.Set(e, new C3 { V = i });
            w.Set(e, new C4 { V = i });
            w.Set(e, new C5 { V = i });
            w.Set(e, new C6 { V = i });
            w.Set(e, new C7 { V = i });
        }
        Assert.Throws<StructuralChangeDuringIterationException>(() =>
            w.ForEach((Entity e, ref Pos _, ref Vel _, ref Doomed _, ref C3 _,
                       ref C4 _, ref C5 _, ref C6 _, ref C7 _) => w.Despawn(e)));
    }

    // ---- still legal: everything that moves no row ----

    [Fact]
    public void WritingComponentsInsideForEachIsAllowed()
    {
        World w = WorldWith(4);
        w.ForEach((Entity e, ref Pos p, ref Vel v) =>
        {
            p.X += v.Dx;                       // the ref parameters
            w.Set(e, new Vel { Dx = 100 });    // an OVERWRITING Set: the component is already there, no move
        });
        var seen = new List<int>();
        w.ForEach((Entity _, ref Vel v) => seen.Add(v.Dx));
        Assert.Equal(new[] { 100, 100, 100, 100 }, seen);
    }

    // The engine's own serial callers (Sharding's owner scans, the replication capture walks) read OTHER entities
    // through the world from inside a ForEach. That is not structural and must keep working, or the guard would be
    // a breaking change dressed as a fix.
    [Fact]
    public void ReadingOtherEntitiesInsideForEachIsAllowed()
    {
        World w = WorldWith(4);
        int sum = 0;
        w.ForEach((Entity e, ref Pos _) =>
        {
            if (w.Has<Vel>(e) && w.TryGet(e, out Vel v)) sum += v.Dx;
            sum += w.Get<Pos>(e).X;
        });
        Assert.Equal(12, sum);   // (0+1+2+3) twice
    }

    [Fact]
    public void ANestedReadOnlyForEachIsAllowed()
    {
        World w = WorldWith(3);
        int pairs = 0;
        w.ForEach((Entity _, ref Pos _) => w.ForEach((Entity _, ref Pos _) => pairs++));
        Assert.Equal(9, pairs);
    }

    // The documented escape hatch, and the reason forbidding the direct form costs callers nothing.
    [Fact]
    public void DeferredDespawnViaCommandsIsAllowedAndReachesEveryEntity()
    {
        World w = WorldWith(6);
        w.ForEach((Entity e, ref Pos _) => w.Commands.Despawn(e));
        w.Commands.Playback(w);

        int left = 0;
        w.ForEach((Entity _, ref Pos _) => left++);
        Assert.Equal(0, left);
    }

    /// <summary>
    /// The measured shape of the corruption, kept as the regression case. Eight entities numbered 0..7 in one
    /// archetype, an action that despawns every multiple of three and adds 100 to the rest. Against the unguarded
    /// loop the survivors came out 101, 102, 104, 105 and <b>7</b>: entity 7 was swap-removed down into row 0 when
    /// entity 0 despawned, was then visited again at its now-dead row 7, and its <c>+100</c> landed in that dead
    /// slot instead of its live row. Nothing threw, and the lost write is only visible if you already know to look
    /// for it. The same pass now refuses at the first despawn.
    /// </summary>
    [Fact]
    public void TheWriteLosingShapeIsRefused()
    {
        var w = new World();
        var all = new List<Entity>();
        for (int i = 0; i < 8; i++) { Entity e = w.Spawn(); w.Set(e, new Pos { X = i }); all.Add(e); }

        Assert.Throws<StructuralChangeDuringIterationException>(() =>
            w.ForEach((Entity e, ref Pos p) => { if (p.X % 3 == 0) w.Despawn(e); else p.X += 100; }));

        // Deferred, the same rule reaches every entity and loses no write.
        w.ForEach((Entity e, ref Pos p) => { if (p.X % 3 == 0) w.Commands.Despawn(e); else p.X += 100; });
        w.Commands.Playback(w);

        var survivors = new List<int>();
        w.ForEach((Entity _, ref Pos p) => survivors.Add(p.X));
        survivors.Sort();
        Assert.Equal(new[] { 101, 102, 104, 105, 107 }, survivors);
    }

    [Fact]
    public void DeferredComponentAddViaCommandsIsAllowed()
    {
        World w = WorldWith(4);
        w.ForEach((Entity e, ref Pos _) => w.Commands.Set(e, new Doomed { V = 7 }));
        w.Commands.Playback(w);

        int tagged = 0;
        w.ForEach((Entity _, ref Doomed d) => { if (d.V == 7) tagged++; });
        Assert.Equal(4, tagged);
    }

    // A change made BETWEEN two iterations is normal use, not a violation: each call takes a fresh snapshot.
    [Fact]
    public void AStructuralChangeBetweenTwoForEachCallsIsFine()
    {
        World w = WorldWith(3);
        var doomed = new List<Entity>();
        w.ForEach((Entity e, ref Pos _) => doomed.Add(e));
        foreach (Entity e in doomed) w.Despawn(e);

        int left = 0;
        w.ForEach((Entity _, ref Pos _) => left++);
        Assert.Equal(0, left);
    }
}
