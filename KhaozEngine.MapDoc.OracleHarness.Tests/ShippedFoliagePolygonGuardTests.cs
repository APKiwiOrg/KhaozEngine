using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
public sealed class ShippedFoliagePolygonGuardTests
{
    [Theory]
    [InlineData(MapFoliageClearanceKind.Solid)]
    [InlineData(MapFoliageClearanceKind.RoofAbove)]
    public void MatchingExtentsWithDifferentPolygon_RefusesWithoutReadingDensity(MapFoliageClearanceKind kind)
    {
        var archetype = new TileObjectArchetype { Id = "invented-square", CollisionKind = TileCollisionKind.Solid, IsRoof = true };
        var obj = new TileObject { Id = 1, ArchetypeId = archetype.Id };
        var footprint = new TileRect(0, 0, 1, 1);
        float half = MathF.Sqrt(0.5f) * 0.5f;
        var ids = new HashSet<string>();

        ShippedFoliageCorrespondence.RequireRule(kind, new(-0.5f, -0.5f, 0.5f, 0.5f),
            new(0.5, 0, -0.5, 0, 1), footprint, obj, archetype, ids, "control");
        Assert.Contains("control", ids);
        // The 45 degree square is a diamond inside the source square. Its extents match within the existing 0.0001 m.
        Assert.InRange(Math.Abs(half * Math.Sqrt(2) - 0.5), 0, 0.0001);
        Assert.Throws<InvalidDataException>(() => ShippedFoliageCorrespondence.RequireRule(kind,
            new(-half, -half, half, half), new(0.5, 0, -0.5, Math.PI / 4, 1), footprint, obj, archetype, ids, "diamond"));
        Assert.DoesNotContain("diamond", ids);
    }
}
