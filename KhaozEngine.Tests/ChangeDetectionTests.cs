using System.Linq;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

file struct CdA : IComponent { public int V; }
file struct CdB : IComponent { public int V; }
file struct CdTag : IComponent { }
public struct CdSer : IComponent { public int V; }   // public, for the serializer-exempt test

public class ChangeDetectionTests
{
    [Fact]
    public void AddAndFirstSetReportAsAdded()
    {
        var w = new World();
        var e = w.Spawn();
        w.Set(e, new CdA { V = 1 });                     // first set = add
        Assert.Equal(new[] { e }, w.Added<CdA>().ToArray());
        Assert.Empty(w.Changed<CdA>());
    }

    [Fact]
    public void OverwriteAndMarkChangedReportAsChanged()
    {
        var w = new World();
        var e = w.Spawn();
        w.Set(e, new CdA { V = 1 });
        w.AdvanceTick();                                 // clear the add
        w.Set(e, new CdA { V = 2 });                     // overwrite => changed
        Assert.Equal(new[] { e }, w.Changed<CdA>().ToArray());
        Assert.Empty(w.Added<CdA>());

        w.AdvanceTick();
        ref var a = ref w.Get<CdA>(e); a.V = 3;          // ref mutation (invisible to the ECS)
        w.MarkChanged<CdA>(e);                            // ... so the caller marks it
        Assert.Equal(new[] { e }, w.Changed<CdA>().ToArray());
    }

    [Fact]
    public void MarkChangedOnMissingComponentIsNoOp()
    {
        var w = new World();
        w.MarkChanged<CdA>(w.Spawn());                   // entity has no CdA
        Assert.Empty(w.Changed<CdA>());
    }

    [Fact]
    public void RemoveReportsRemovedAndEntityStaysAlive()
    {
        var w = new World();
        var e = w.Spawn();
        w.Set(e, new CdA { V = 1 });
        w.AdvanceTick();
        w.Remove<CdA>(e);
        Assert.Equal(new[] { e }, w.Removed<CdA>().ToArray());
        Assert.True(w.IsAlive(e));
    }

    [Fact]
    public void DespawnReportsRemovedForEachComponentAndMayBeDead()
    {
        var w = new World();
        var e = w.Spawn();
        w.Set(e, new CdA { V = 1 });
        w.Set(e, new CdTag());
        w.AdvanceTick();
        w.Despawn(e);
        Assert.Contains(e, w.Removed<CdA>());
        Assert.Contains(e, w.Removed<CdTag>());
        Assert.Empty(w.Removed<CdA>().Where(w.IsAlive)); // e is dead now
    }

    [Fact]
    public void AdvanceTickClearsSetsAndBumpsTick()
    {
        var w = new World();
        var e = w.Spawn();
        w.Set(e, new CdA { V = 1 });
        ulong t0 = w.Tick;
        w.AdvanceTick();
        Assert.Equal(t0 + 1, w.Tick);
        Assert.Empty(w.Added<CdA>());                    // last tick's add cleared
    }

    [Fact]
    public void TypeIsolation()
    {
        var w = new World();
        var e = w.Spawn();
        w.Set(e, new CdB { V = 1 });
        Assert.Empty(w.Added<CdA>());
        Assert.Equal(new[] { e }, w.Added<CdB>().ToArray());
    }

    [Fact]
    public void LoadDoesNotPopulateEventSets()
    {
        var src = new World();
        src.Set(src.Spawn(), new CdSer { V = 5 });
        var ser = new WorldSerializer(typeof(CdSer));
        World loaded = ser.Load(ser.Save(src));
        Assert.Empty(loaded.Added<CdSer>());             // SetByType (load) is un-hooked
    }
}
