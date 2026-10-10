using System.Linq;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc;

internal static class BoundFaceContextFixtures
{
    internal static (MapScopedSurfaces View, MapSpaceFootprint Footprint) EightByEight()
    {
        var set = new MapSurfaceSet();
        set.Refs.Add(MixedResolutionFixtures.Surface("floor8", 1, MapSurfaceRole.SupportFloor));
        set.Refs.Add(MixedResolutionFixtures.Surface("ceil8", 2, MapSurfaceRole.Ceiling));
        MapSurfacePatch floor = MixedResolutionFixtures.Patch(new("floor8", 0, 0), 8, 8, (_, _) => 0);
        MapSurfacePatch ceiling = MixedResolutionFixtures.Patch(new("ceil8", 0, 0), 16, 16, (_, _) => 600);
        set.Patches.Add(floor.Key, floor);
        set.Patches.Add(ceiling.Key, ceiling);
        int[] cells = Enumerable.Range(0, 8).SelectMany(z => Enumerable.Range(0, 8).Select(x => z * 64 + x)).ToArray();
        MixedResolutionFixtures.Room(floor, "room8", "room8-cells", MapSpaceKind.Exterior,
            floor.Key, cells, "floor8", "ceil8");
        MixedResolutionFixtures.RequireValid(set);
        return MixedResolutionFixtures.ViewWithFootprint(set);
    }
}
