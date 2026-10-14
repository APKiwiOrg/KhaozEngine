using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

internal static class MapInteriorCreationFixtures
{
    internal static PrefabFixture Empty()
    {
        var f = PrefabFixtures.Create();
        f.Document.Prefabs = new(1, 1, [], []);
        f.Document.BuildingIntent = new(1, [], [], [], [])
        { Storeys = new[] { new MapStoreyDoc(1, "ground-floor", "Ground floor", null) } };
        return f;
    }
    internal static MapPrismDomain Domain() => new("new-volume", 1,
        new("new-room", new("exterior", 0, 0)), new("exterior", new("exterior", 0, 0)), "", "ground-floor",
        new(MapLatticeFrame.ImportedMetreCentimetre, new[] { new MapWaterRect(4, 4, 8, 8) }, []), 0, 3);
}
