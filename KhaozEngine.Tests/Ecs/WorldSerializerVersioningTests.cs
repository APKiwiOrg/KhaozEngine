using System;
using System.Linq;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests.Ecs;

[ComponentId("pos")]
public struct VrPosition : IComponent { public float X; public float Y; }

// INTENTIONAL COLLISION: DupKeyA and DupKeyB both carry [ComponentId("dup")] to exercise the
// duplicate-key guard in WorldSerializer's constructor. As a side effect, any call to
// WorldSerializer.FromAssemblyOf<T>() where T lives in this test assembly will throw, because
// the scan finds both structs and hits the collision. Future tests that need FromAssemblyOf must
// either pass explicit types to the constructor or scan the engine assembly (e.g.
// FromAssemblyOf<KhaozEngine.Ecs.Parent>()), NOT the whole test assembly.
[ComponentId("dup")]
public struct DupKeyA : IComponent { public int Value; }

[ComponentId("dup")]
public struct DupKeyB : IComponent { public int Value; }

public class WorldSerializerVersioningTests
{
    private static WorldSerializer Ser() => new(typeof(VrPosition));

    [Fact]
    public void UnknownFutureVersionThrowsTyped()
    {
        // A document claiming a FormatVersion far beyond what this build writes.
        string json = "{\"FormatVersion\":9999,\"NextId\":0,\"FreeIds\":[],\"Entities\":[]}";
        var ex = Assert.Throws<UnsupportedSaveVersionException>(() => Ser().Load(json));
        Assert.Equal(9999, ex.FoundVersion);
        Assert.Equal(WorldSerializer.CurrentFormatVersion, ex.MaxSupportedVersion);
    }

    [Fact]
    public void ComponentIdOverridesTypeFullNameInSavedOutput()
    {
        var w = new World();
        w.Set(w.Spawn(), new VrPosition { X = 3, Y = 4 });

        string json = Ser().Save(w);

        Assert.Contains("\"pos\"", json);
        Assert.DoesNotContain(typeof(VrPosition).FullName!, json);
    }

    [Fact]
    public void RoundTripsWithComponentId()
    {
        var w = new World();
        var e = w.Spawn();
        w.Set(e, new VrPosition { X = 7, Y = 9 });

        World loaded = Ser().Load(Ser().Save(w));

        var got = loaded.Query().With<VrPosition>().Entities()
            .Select(x => loaded.Get<VrPosition>(x)).Single();
        Assert.Equal(7, got.X);
        Assert.Equal(9, got.Y);
    }

    [Fact]
    public void DuplicateComponentKeyThrowsArgumentException()
    {
        // DupKeyA and DupKeyB both carry [ComponentId("dup")]: the second registration must throw.
        Assert.Throws<ArgumentException>(() => new WorldSerializer(typeof(DupKeyA), typeof(DupKeyB)));
    }

    [Fact]
    public void RegisteringSameTypeTwiceIsIdempotent()
    {
        // Idempotent re-registration of the identical type must NOT throw.
        _ = new WorldSerializer(typeof(VrPosition), typeof(VrPosition));
    }
}
