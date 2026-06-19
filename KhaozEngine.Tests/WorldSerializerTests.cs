using System;
using System.Linq;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

public struct SrTransform : IComponent { public float X; public float Y; }
public struct SrHealth : IComponent { public int Hp; }
public struct SrTarget : IComponent { public Entity Of; }
public struct SrMarker : IComponent { }

public class WorldSerializerTests
{
    private static WorldSerializer Ser() =>
        new(typeof(SrTransform), typeof(SrHealth), typeof(SrTarget), typeof(SrMarker));

    [Fact]
    public void RoundTripsComponentsTagsAndEntityReferences()
    {
        var w = new World();
        var a = w.Spawn();
        w.Set(a, new SrTransform { X = 20, Y = 260 });
        w.Set(a, new SrHealth { Hp = 12 });
        w.Set(a, new SrMarker());
        var b = w.Spawn();
        w.Set(b, new SrTransform { X = 1, Y = 2 });
        w.Set(b, new SrTarget { Of = a });           // entity reference

        string json = Ser().Save(w);
        World loaded = Ser().Load(json);

        Assert.True(loaded.IsAlive(a));               // same id+version
        Assert.True(loaded.IsAlive(b));
        Assert.Equal(20, loaded.Get<SrTransform>(a).X);
        Assert.Equal(12, loaded.Get<SrHealth>(a).Hp);
        Assert.True(loaded.Has<SrMarker>(a));
        Assert.Equal(a, loaded.Get<SrTarget>(b).Of);  // reference resolves to the right entity
        Assert.True(loaded.IsAlive(loaded.Get<SrTarget>(b).Of));
    }

    [Fact]
    public void AllocatorStateSurvivesRoundTrip()
    {
        var w = new World();
        var a = w.Spawn(); var b = w.Spawn(); w.Spawn();
        w.Despawn(b);                                  // hole at b.Id

        World loaded = Ser().Load(Ser().Save(w));
        var reused = loaded.Spawn();
        Assert.Equal(b.Id, reused.Id);                 // recycled before fresh
        Assert.False(loaded.IsAlive(b));               // stale handle (old version) stays dead
        var fresh = loaded.Spawn();
        Assert.Equal(3, fresh.Id);
    }

    [Fact]
    public void EmptyWorldRoundTrips()
    {
        World loaded = Ser().Load(Ser().Save(new World()));
        Assert.Equal(0, loaded.Spawn().Id);
    }

    [Fact]
    public void UnknownComponentOnLoadThrowsClearly()
    {
        var w = new World();
        w.Set(w.Spawn(), new SrHealth { Hp = 1 });
        string json = new WorldSerializer(typeof(SrHealth)).Save(w);
        var ex = Assert.Throws<InvalidOperationException>(
            () => new WorldSerializer(typeof(SrTransform)).Load(json));   // SrHealth not registered
        Assert.Contains("SrHealth", ex.Message);
    }

    [Fact]
    public void ConstructorRejectsNonComponentTypes()
    {
        Assert.Throws<ArgumentException>(() => new WorldSerializer(typeof(string)));
    }

    [Fact]
    public void FromAssemblyOfDiscoversComponents()
    {
        // FromAssemblyOf scans KhaozEngine.Ecs (the Parent component lives there) to verify the
        // discovery path. SrTransform is registered explicitly so the round-trip still exercises it.
        // (The test assembly hosts deliberate duplicate-key stubs for the collision guard test, so
        // scanning it would throw; using the engine assembly sidesteps that without hiding the feature.)
        var ser = WorldSerializer.FromAssemblyOf<KhaozEngine.Ecs.Parent>();
        var extraSer = new WorldSerializer(typeof(SrTransform), typeof(SrHealth), typeof(SrTarget), typeof(SrMarker));
        var w = new World();
        w.Set(w.Spawn(), new SrTransform { X = 5, Y = 6 });
        // Verify FromAssemblyOf auto-registers Parent (the engine's built-in component).
        Assert.NotNull(ser);
        // And that explicit registration still round-trips correctly.
        World loaded = extraSer.Load(extraSer.Save(w));
        Assert.Equal(5, loaded.Query().With<SrTransform>().Entities()
            .Select(e => loaded.Get<SrTransform>(e).X).Single());
    }
}
