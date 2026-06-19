using System.Linq;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests.Ecs;

[ComponentId("pos")]
public struct VrPosition : IComponent { public float X; public float Y; }

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
}
