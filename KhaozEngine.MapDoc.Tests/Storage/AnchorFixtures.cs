using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.MapDoc.Storage;

internal static class AnchorFixtures
{
    internal static readonly WorldFrame FarFrame = new(20, 0);

    internal static string FarAnchors()
    {
        MapDocument doc = AcquisitionBoundFixtures.Document();
        AcquisitionBoundFixtures.Surface(doc, "ground", MapSurfaceRole.SupportFloor);
        AcquisitionBoundFixtures.Surface(doc, "roof", MapSurfaceRole.Ceiling);
        MapSurfacePatch origin = AcquisitionBoundFixtures.Patch(new("ground", 0, 0), 0);
        MapSurfacePatch ground = AcquisitionBoundFixtures.Patch(new("ground", 40, 0), 1000, width: 4, depth: 4);
        MapSurfacePatch roof = AcquisitionBoundFixtures.Patch(new("roof", 40, 0), 1400, minX: 2, width: 2, depth: 4);
        MapSurfacePatch remote = AcquisitionBoundFixtures.Patch(new("roof", 41, 0), 1400);
        var door = new MapRecordRef("door", ground.Key);
        origin.Records.Add(AcquisitionBoundFixtures.Space("outside") with { Portals = new[] { new MapBoundaryRef(door, MapSide.Back) } });
        origin.Records.Add(new MapSpaceFootprint("outside-origin", new("outside", origin.Key), origin.Key, new[] { 0 },
            new(MapBoundKind.SupportFloor, "ground", null), new(MapBoundKind.OpenTop, null, null)));
        ground.Records.Add(new MapSpaceDoc("annex", MapSpaceKind.Cave, null, null, Array.Empty<string>(), new[]
        {
            new MapBoundaryRef(new("annex-south", ground.Key), MapSide.Front),
            new MapBoundaryRef(new("annex-north", ground.Key), MapSide.Back),
            new MapBoundaryRef(new("annex-east", remote.Key), MapSide.Back),
        }, new[] { new MapBoundaryRef(door, MapSide.Front) }, Array.Empty<MapRecordRef>()));
        ground.Records.Add(new MapSpaceFootprint("outside-near", new("outside", origin.Key), ground.Key,
            Cells(0), new(MapBoundKind.SupportFloor, "ground", null), new(MapBoundKind.OpenTop, null, null)));
        ground.Records.Add(new MapSpaceFootprint("annex-cells", new("annex", ground.Key), ground.Key,
            Cells(2), new(MapBoundKind.SupportFloor, "ground", null), new(MapBoundKind.Ceiling, "roof", null)));
        ground.Records.Add(new MapCavePortal("door", new("annex", ground.Key), new("outside", origin.Key),
            new[] { Vertex("ground", 2562, 0), Vertex("ground", 2562, 4) }, new("door-bottom", ground.Key), new("door-top", remote.Key)));
        ground.Records.Add(Chain("door-bottom", ground.Key, 2562, 0, 2562, 4));
        remote.Records.Add(Chain("door-top", roof.Key, 2562, 0, 2562, 4));
        Wall(ground, roof, ground, "annex-south", 2562, 0, 2564, 0);
        Wall(ground, roof, ground, "annex-north", 2562, 4, 2564, 4);
        Wall(remote, remote, remote, "annex-east", 2564, 0, 2564, 4, ground.Key, roof.Key);
        foreach (MapSurfacePatch patch in new[] { origin, ground, roof, remote }) AcquisitionBoundFixtures.Add(doc, patch);
        return SurfaceStorageFixtures.SaveToTemp(doc);
    }

    static int[] Cells(int x) => Enumerable.Range(0, 4).SelectMany(z => new[] { z * 64 + x, z * 64 + x + 1 }).ToArray();
    static MapLatticeVertex Vertex(string surface, long x, long z) => new(surface, MapLatticeAddress.Corner(x, z));
    static MapBoundaryChain Chain(string id, MapPatchKey source, long x0, long z0, long x1, long z1) =>
        new(id, MapChainKind.SurfaceEdge, source, new[]
        {
            new MapChainVertex(Vertex(source.SurfaceId, x0, z0), null),
            new MapChainVertex(Vertex(source.SurfaceId, x1, z1), null),
        });
    static void Wall(MapSurfacePatch lower, MapSurfacePatch upper, MapSurfacePatch anchor, string id,
        long x0, long z0, long x1, long z1, MapPatchKey? lowerSource = null, MapPatchKey? upperSource = null)
    {
        lower.Records.Add(Chain(id + "-lower", lowerSource ?? lower.Key, x0, z0, x1, z1));
        upper.Records.Add(Chain(id + "-upper", upperSource ?? upper.Key, x0, z0, x1, z1));
        anchor.Records.Add(new MapWallStrip(id, new(id + "-lower", lower.Key), new(id + "-upper", upper.Key), MapStripFacing.TwoSided, 1));
    }
}
