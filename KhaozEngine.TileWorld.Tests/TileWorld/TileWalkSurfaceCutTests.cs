using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.TileWorld;
using Xunit;
using static KhaozEngine.Tests.TileWorld.TileWalkSurfaceTestData;

namespace KhaozEngine.Tests.TileWorld;

public sealed class TileWalkSurfaceCutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CutWalkSurfaceAllowsLowerCandidate(bool reverse)
    {
        TileWorldDocument doc = CarvedWorld();
        TileWorldCatalogs catalogs = Catalogs();
        TileObject stepped = doc.AddObject("stepped", BridgeX, BridgeZ, 0, 0);
        // Keep both surfaces at literal world heights despite the carved anchor.
        catalogs.Archetype("stepped")!.WalkSurfaces =
            [new TileWalkSurface { Height = 1.8f }, new TileWalkSurface { Height = 2f }];
        if (reverse) catalogs.Archetype("stepped")!.WalkSurfaces!.Reverse();
        var origin = new Vector3(CentreX, 5f, -20.5f);
        InteriorHeightField field = InteriorHeightFieldBuilder.Build(
            [new InteriorVolume
            {
                Outline = [new(20f, -23f), new(23f, -23f), new(23f, -20f), new(20f, -20f)],
                FloorY = 1.1f, CeilingY = 2f,
            }], new InteriorWindow(new Vector2(20f, -23f), 0.25f, 12), new InteriorEncoding(0f, 0.01f));
        var candidates = new List<float>();
        TileHit hit = Assert.IsType<TileHit>(TileWalkSurfaces.Raycast(doc, catalogs, 0, origin,
            -Vector3.UnitY, 10f, o => o.Id == stepped.Id, (o, point) =>
            {
                Assert.Equal(stepped.Id, o.Id);
                candidates.Add(point.Y);
                return !field.Contains(point);
            }));
        Assert.Equal(1f, hit.Point.Y, 4);
        Assert.Contains(candidates, y => System.MathF.Abs(y - 1.2f) < 0.0001f);
        TileHit unfiltered = Assert.IsType<TileHit>(TileWalkSurfaces.Raycast(doc, catalogs, 0, origin,
            -Vector3.UnitY, 10f));
        Assert.Equal(1.2f, unfiltered.Point.Y, 4);
        Assert.Null(TileWalkSurfaces.Raycast(doc, catalogs, 0, origin, -Vector3.UnitY, 10f,
            include: _ => false, acceptPoint: (_, _) => true));
        Assert.Null(TileWalkSurfaces.Raycast(doc, catalogs, 0, origin, -Vector3.UnitY, 10f,
            acceptPoint: (_, _) => false));
    }
}
