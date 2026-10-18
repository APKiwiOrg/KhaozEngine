using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Markers;

public sealed class MapMarkerEffectTests
{
    const string Expected = """{"oldBounds":[-30,0,-30,20,5,20],"newBounds":[1,0,3,30,5,30],"patches":[],"spaceIds":[],"dependencyIds":[],"markerIds":["archetype","binding","create","delete","enable","move","rename"],"markerNameChanges":[{"markerId":"create","before":null,"after":"create-name"},{"markerId":"delete","before":"delete-name","after":null},{"markerId":"rename","before":"old","after":"new"}],"digestChanges":[],"invalidates":16}""";

    [Fact]
    public void MarkerEffects_DescribeIsPureAndOrdered()
    {
        var before = new[]
        {
            Point("move", 1, 2, 3), Point("enable", 10, 0, 10), Bound("binding", "s1"), Point("rename", 40, 0, 40) with { Name = "old" },
            Point("archetype", 50, 0, 50) with { ArchetypeId = "guard" }, Point("delete", -30, 1, -30), Point("same", 60, 0, 60),
        };
        var after = new[]
        {
            Point("same", 60, 0, 60), Point("create", 30, 1, 30), Point("archetype", 50, 0, 50) with { ArchetypeId = "keeper" },
            Point("rename", 40, 0, 40) with { Name = "new" }, Bound("binding", "s2"), Point("enable", 10, 0, 10) with { Enabled = false },
            Point("move", 1, 4, 3),
        };
        string[] beforeIds = before.Select(m => m.Id).ToArray(), afterIds = after.Select(m => m.Id).ToArray();

        MapNativeEditEffects effects = MapMarkerEffects.Describe(before, after);

        Assert.Equal(Expected, effects.Describe());
        // Input order is not semantic and the inputs are not mutated.
        Assert.Equal(Expected, MapMarkerEffects.Describe(before.Reverse().ToArray(), after.Reverse().ToArray()).Describe());
        Assert.Equal(beforeIds, before.Select(m => m.Id));
        Assert.Equal(afterIds, after.Select(m => m.Id));
        Assert.Equal(MapNativeInvalidation.Residency, effects.Invalidates);
        Assert.Equal(new[] { new MapMarkerNameChange("rename", "new", "old"), new MapMarkerNameChange("create", "create-name", null),
            new MapMarkerNameChange("delete", null, "delete-name") }.OrderBy(c => c.MarkerId, StringComparer.Ordinal),
            MapMarkerEffects.Describe(after, before).MarkerNameChanges.OrderBy(c => c.MarkerId, StringComparer.Ordinal));

        // A create and a delete each carry exactly one box. Metadata only carries IDs and rename rows.
        var created = MapMarkerEffects.Describe([], [Point("create", 30, 1, 30)]);
        Assert.Null(created.OldBounds);
        Assert.Equal(new MapBox3(30, 1, 30, 30, 1, 30), created.NewBounds);
        var deleted = MapMarkerEffects.Describe([Point("delete", -30, 1, -30)], []);
        Assert.Equal(new MapBox3(-30, 1, -30, -30, 1, -30), deleted.OldBounds);
        Assert.Null(deleted.NewBounds);
        var renamed = MapMarkerEffects.Describe([before[3]], [after[3]]);
        Assert.Equal("""{"oldBounds":null,"newBounds":null,"patches":[],"spaceIds":[],"dependencyIds":[],"markerIds":["rename"],"markerNameChanges":[{"markerId":"rename","before":"old","after":"new"}],"digestChanges":[],"invalidates":0}""",
            renamed.Describe());
        var archetype = MapMarkerEffects.Describe([before[4]], [after[2]]);
        Assert.Equal(new[] { "archetype" }, archetype.MarkerIds);
        Assert.Empty(archetype.MarkerNameChanges);
        Assert.Equal(MapNativeInvalidation.None, archetype.Invalidates);
        Assert.Null(archetype.OldBounds);
        // A binding change at equal height is spatial, so it names both points.
        var binding = MapMarkerEffects.Describe([before[2]], [after[4]]);
        Assert.Equal(new MapBox3(20, 5, 20, 20, 5, 20), binding.OldBounds);
        Assert.Equal(binding.OldBounds, binding.NewBounds);
        Assert.Equal(MapNativeInvalidation.Residency, binding.Invalidates);
        // Same values have empty effects, and a released description without rename rows keeps its bytes.
        var same = MapMarkerEffects.Describe(before, before.ToArray());
        Assert.Empty(same.MarkerIds);
        Assert.Equal("""{"oldBounds":null,"newBounds":null,"patches":[],"spaceIds":[],"dependencyIds":[],"digestChanges":[],"invalidates":0}""", same.Describe());
    }

    static MapEffectiveMarker Point(string id, float x, float y, float z) =>
        new(2, id, id + "-name", MarkerFixtures.Exact(x, y, z), 0.25f, "guard-role", true, new[] { "first" }, null)
        { HeightProvenance = new(MapMarkerHeightMode.Explicit, null, null, null, null, null) };

    static MapEffectiveMarker Bound(string id, string surface) => Point(id, 20, 5, 20) with
    { HeightProvenance = new(MapMarkerHeightMode.Support, MarkerFixtures.SurfaceBinding(surface), null, null, null, 5f) };
}
