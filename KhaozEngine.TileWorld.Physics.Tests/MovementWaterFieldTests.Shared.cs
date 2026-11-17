using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

// The water field fixture, linked into KhaozEngine.TileWorld.Physics.FloatingPoint.Tests without the tests.
public partial class MovementWaterFieldTests
{
    internal static readonly MovementQueryIdentity Identity = new("tile-test", 1, "snapshot");
    internal static readonly MovementSpaceKey Space = new("water-world", "ordinary");

    internal static TileWorldDocument WaterWorld()
    {
        var doc = TileWorldPhysicsTestData.FlatWorld(new RegionCoord(0, -1), new RegionCoord(0, 0));
        doc.SetUnderlay(0, -1, 0, TileWorldPhysicsTestData.Water);
        // Keep the measured world SW/NE corners and minimum-Z tangent edge deep. The opposite rim
        // supplies the level while the box-oracle paths remain clear of the actual drawn bed.
        doc.SetCornerHeightCm(0, -1, 0, 2);
        doc.SetCornerHeightCm(1, -1, 0, -200);
        doc.SetCornerHeightCm(0, 0, 0, -200);
        doc.SetCornerHeightCm(1, 0, 0, -200);
        doc.SetCornerHeightCm(0, 1, 0, -200);
        doc.SetCornerHeightCm(1, 1, 0, -200);
        return doc;
    }

    internal static TileMediumSampler Field(TileWorldDocument doc) =>
        TileWorldColliders.Build(doc, TileWorldPhysicsTestData.Catalogs(), "water-world").Medium;

    internal static TileMediumSampler SingleWaterTile(int tileX, int tileZ, float tileSize)
    {
        var region = RegionCoord.Of(tileX, tileZ);
        var doc = TileWorldPhysicsTestData.FlatWorld(region);
        doc.TileSize = tileSize;
        // Rounded world axes can select a neighboring lattice cell. Its captured bed must agree
        // with the -4 m analytic ground used by the ordinary-frame fixture too.
        for (int z = region.OriginZ; z < region.OriginZ + TileRegion.Size; z++)
            for (int x = region.OriginX; x < region.OriginX + TileRegion.Size; x++)
                doc.SetCornerHeightCm(x, z, 0, -400);
        doc.SetUnderlay(tileX, tileZ, 0, TileWorldPhysicsTestData.Water);
        doc.SetCornerHeightCm(tileX, tileZ, 0, -400);
        doc.SetCornerHeightCm(tileX + 1, tileZ, 0, 2);
        return new(doc, TileWorldPhysicsTestData.Catalogs(), new[] { region }, "water-world");
    }
}
