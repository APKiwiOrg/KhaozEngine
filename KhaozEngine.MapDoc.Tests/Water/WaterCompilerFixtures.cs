using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Tests.MapDoc.Storage;

namespace KhaozEngine.Tests.MapDoc.Water;

internal static partial class WaterFixtures
{
    /// <summary>An exterior <paramref name="spaceId"/> over the floor <c>{spaceId}-floor</c> on the given lattice cell
    /// range, with body <paramref name="bodyId"/> at <c>SurfaceY</c> 0 over <paramref name="rects"/>, or over the whole
    /// floor when none are given.</summary>
    internal static MapDocument ExteriorWater(string spaceId, string bodyId, MapLatticeFrame frame, long cellMinX, long cellMinZ,
        long cellMaxX, long cellMaxZ, Func<long, long, int> heightUnits, params MapWaterRect[] rects)
    {
        string floor = spaceId + "-floor";
        var set = new MapSurfaceSet();
        Surface(set, floor, MapSurfaceRole.SupportFloor, frame, cellMinX, cellMinZ, cellMaxX, cellMaxZ, heightUnits);
        Footprints(set, spaceId, MapSpaceKind.Exterior, frame, cellMinX, cellMinZ, cellMaxX, cellMaxZ, Floor(floor), OpenTop, Slot0(floor));
        MapWaterRect[] domain = rects.Length != 0 ? rects : new[] { Rect(cellMinX, cellMinZ, cellMaxX, cellMaxZ) };
        var water = new MapWaterSet();
        water.Media.Add(new MapWaterMediumDoc(Medium, MapWaterMediumKind.Water, 1f));
        water.Bodies.Add(new MapWaterBodyDoc(bodyId, 0f, Medium, Material, new MapWaterDomainDoc(frame, domain, Array.Empty<MapWaterRing>()),
            new[] { Cover(spaceId, floor) }));
        return Document(set, water);
    }

    /// <summary>An exterior <c>open</c> over a flat -10 m floor on 8 m cells, a square of <paramref name="sizeMetres"/>
    /// from the origin toward negative Z across as many slots as it needs, under body <c>sea</c> over the whole
    /// square.</summary>
    internal static MapDocument OpenSea(float sizeMetres)
    {
        var frame = new MapLatticeFrame(new(8, 1), new(1, 100), MapRowDirection.NegativeZ, MapHeightDatum.WorldY0);
        long cells = (long)(sizeMetres / 8);
        return ExteriorWater("open", "sea", frame, 0, 0, cells, cells, (_, _) => -1000);
    }

    /// <summary>A 64 m flat exterior floor at -2 m under body <c>river</c>, 46 one-row rectangles
    /// <c>(i, i, i + 8, i + 1)</c> for i from 0 to 45.</summary>
    internal static MapDocument ManyRectRiver() => ExteriorWater("valley", "river", Frame, 0, 0, 64, 64, (_, _) => -200,
        Enumerable.Range(0, 46).Select(i => Rect(i, i, i + 8, i + 1)).ToArray());

    /// <summary>A flat exterior floor at -2 m over x -8 to 8 and world z -8 to 8, four slots around the origin, under
    /// body <c>sea</c> over all of it.</summary>
    internal static MapDocument SeaAcrossTheOrigin() => ExteriorWater("origin", "sea", Frame, -8, -8, 8, 8, (_, _) => -200);

    /// <summary>A 64 by 64 m cave <c>big-cave</c> at x 0 to 64 and world z -64 to 0, floor -40 m on the 1 m lattice and
    /// a flat roof at -32 m on a third-metre lattice (192 by 192 cells, 73,728 faces), under body <c>big-lake</c> at
    /// <c>SurfaceY</c> -34.</summary>
    internal static MapDocument BigCaveLake()
    {
        var set = new MapSurfaceSet();
        Surface(set, "big-floor", MapSurfaceRole.SupportFloor, Frame, 0, 0, 64, 64, (_, _) => -4000);
        Surface(set, "big-roof", MapSurfaceRole.Ceiling, ThirdMetreFrame, 0, 0, 192, 192, (_, _) => -9600);
        Footprints(set, "big-cave", MapSpaceKind.Cave, Frame, 0, 0, 64, 64, Floor("big-floor"), Ceiling("big-roof"), Slot0("big-floor"));
        CloseWalls(set, "big-cave", "big-floor", (id, x0, z0, x1, z1) => AuthoredAlong(id, "big-floor", x0, z0, x1, z1, (_, _) => -3200),
            0, 0, 64, 64);
        var water = new MapWaterSet();
        water.Media.Add(new MapWaterMediumDoc(Medium, MapWaterMediumKind.Water, 1f));
        water.Bodies.Add(Body("big-lake", -34f, Rect(0, 0, 64, 64), Cover("big-cave", "big-floor")));
        return Document(set, water);
    }

    /// <summary>A 2 by 2 cave <c>fine-cave</c> on a lattice of 1/61 m cells and 1/2,000,000,000 m height units, floor
    /// -1 m and a roof rising from -0.4999999995 m at lattice x 0 to +0.5 m at x 1, then flat. Body <c>fine-lake</c>
    /// sits at 3 * 2^-40 m, so the roof crosses its level at a coordinate whose exact denominator leaves 64 bits.</summary>
    internal static MapDocument UnrepresentableContourCave()
    {
        var frame = new MapLatticeFrame(new(1, 61), new(1, 2_000_000_000), MapRowDirection.NegativeZ, MapHeightDatum.WorldY0);
        var set = new MapSurfaceSet();
        Surface(set, "fine-floor", MapSurfaceRole.SupportFloor, frame, 0, 0, 2, 2, (_, _) => -2_000_000_000);
        Surface(set, "fine-roof", MapSurfaceRole.Ceiling, frame, 0, 0, 2, 2, (x, _) => x == 0 ? -999_999_999 : 1_000_000_000);
        Footprints(set, "fine-cave", MapSpaceKind.Cave, frame, 0, 0, 2, 2, Floor("fine-floor"), Ceiling("fine-roof"), Slot0("fine-floor"));
        CloseWalls(set, "fine-cave", "fine-floor", "fine-roof", 0, 0, 2, 2);
        var water = new MapWaterSet();
        water.Media.Add(new MapWaterMediumDoc(Medium, MapWaterMediumKind.Water, 1f));
        water.Bodies.Add(new MapWaterBodyDoc("fine-lake", MathF.ScaleB(3f, -40), Medium, Material,
            new MapWaterDomainDoc(frame, new[] { Rect(0, 0, 2, 2) }, Array.Empty<MapWaterRing>()), new[] { Cover("fine-cave", "fine-floor") }));
        return Document(set, water);
    }

    /// <summary>True when the world X at which <see cref="UnrepresentableContourCave"/>'s roof crosses its body's level
    /// along lattice row 0 narrows to <see cref="MapExactValue"/>, computed exactly with <see cref="MapWideRational"/>.</summary>
    internal static bool ContourNarrows(MapDocument doc)
    {
        MapSurfaceRef roof = doc.Surfaces.Refs.Single(s => s.Id == "fine-roof");
        MapSurfacePatch patch = doc.Surfaces.Patches[Slot0("fine-roof")];
        MapWideRational y0 = MapWideRational.From(roof.Frame.Metres(new(patch.Height(0, 0), 1)));
        MapWideRational y1 = MapWideRational.From(roof.Frame.Metres(new(patch.Height(1, 0), 1)));
        MapWideRational level = MapWideRational.From(MapExactValue.FromSingle(doc.Water!.Bodies.Single(b => b.Id == "fine-lake").SurfaceY));
        MapWideRational x = level.Subtract(y0).Divide(y1.Subtract(y0)).Multiply(MapWideRational.From(roof.Frame.CellUnitMetres.Exact()));
        return x.TryNarrow(out _);
    }

    /// <summary>The exact total XZ area of the triangles, each counted by its absolute shoelace area.</summary>
    internal static MapExactValue Area(IEnumerable<MapExactTriangle> triangles)
    {
        MapWideRational total = default;
        foreach (MapExactTriangle t in triangles) total = total.Add(NormalY(t).Abs());
        if (!total.Divide(MapWideRational.From(2)).TryNarrow(out MapExactValue area)) throw new InvalidOperationException("area does not narrow");
        return area;
    }

    /// <summary>The exact Y of <c>cross(B - A, C - A)</c>, positive when the triangle faces up.</summary>
    internal static MapWideRational NormalY(MapExactTriangle t)
    {
        MapWideRational ux = Wide(t.B.X).Subtract(Wide(t.A.X)), uz = Wide(t.B.Z).Subtract(Wide(t.A.Z));
        MapWideRational vx = Wide(t.C.X).Subtract(Wide(t.A.X)), vz = Wide(t.C.Z).Subtract(Wide(t.A.Z));
        return uz.Multiply(vx).Subtract(ux.Multiply(vz));
    }

    /// <summary>Acquires all of <paramref name="doc"/> from its stored copy with every patch of
    /// <paramref name="surfaceId"/> certified known empty, while every space record stays anchored in its floor.</summary>
    internal static MapScopedSurfaces ViewWithKnownEmpty(MapDocument doc, string surfaceId) => ScopeFixtures.Acquire(
        new FilteredSurfaceSource(StoredSource(doc), Array.Empty<MapPatchKey>(), doc.Surfaces.Patches.Keys.Where(k => k.SurfaceId == surfaceId).ToArray()),
        ScopeFixtures.Whole(doc));

    /// <summary>An authored chain along a slot-0 lattice edge of <paramref name="surface"/> whose vertex heights come from
    /// <paramref name="heightUnits"/> at each lattice corner.</summary>
    static MapBoundaryChain AuthoredAlong(string id, string surface, int x0, int z0, int x1, int z1, Func<long, long, int> heightUnits)
    {
        int steps = Math.Max(Math.Abs(x1 - x0), Math.Abs(z1 - z0));
        return new(id, MapChainKind.Authored, null, Enumerable.Range(0, steps + 1).Select(i =>
        {
            int x = x0 + Math.Sign(x1 - x0) * i, z = z0 + Math.Sign(z1 - z0) * i;
            return new MapChainVertex(new MapLatticeVertex(surface, MapLatticeAddress.Corner(x, z)), heightUnits(x, z));
        }).ToArray());
    }

    /// <summary>Sets every corner height of <paramref name="surfaceId"/> from <paramref name="heightUnits"/> at lattice
    /// corner coordinates, then reseals the surface refs.</summary>
    static void SetHeights(MapDocument doc, string surfaceId, Func<long, long, int> heightUnits)
    {
        foreach (var (key, patch) in doc.Surfaces.Patches.Where(p => p.Key.SurfaceId == surfaceId))
        {
            long originX = key.SlotX * MapPatchKey.SlotCells + patch.CellMinX, originZ = key.SlotZ * MapPatchKey.SlotCells + patch.CellMinZ;
            for (int z = 0; z <= patch.Depth; z++)
                for (int x = 0; x <= patch.Width; x++)
                    patch.Heights[z * (patch.Width + 1) + x] = heightUnits(originX + x, originZ + z);
        }
        Reseal(doc);
    }

    /// <summary>Clears presence on the given slot-0 cells of <paramref name="surfaceId"/>, then reseals the surface refs.</summary>
    static void ClearPresence(MapDocument doc, string surfaceId, params (int X, int Z)[] cells)
    {
        MapSurfacePatch patch = doc.Surfaces.Patches[Slot0(surfaceId)];
        foreach (var (x, z) in cells) patch.SetPresent(x - patch.CellMinX, z - patch.CellMinZ, false);
        Reseal(doc);
    }

    /// <summary>Replaces the height unit of <paramref name="surfaceId"/>'s frame, leaving its integer heights and patch
    /// digests unchanged, then reseals the surface refs.</summary>
    static void SetHeightUnit(MapDocument doc, string surfaceId, MapRational heightUnit)
    {
        int at = doc.Surfaces.Refs.FindIndex(s => s.Id == surfaceId);
        doc.Surfaces.Refs[at] = doc.Surfaces.Refs[at] with { Frame = doc.Surfaces.Refs[at].Frame with { HeightUnitMetres = heightUnit } };
        Reseal(doc);
    }

    /// <summary>Recomputes every surface ref's semantic digest from its patches, as <see cref="Document"/> seals them.</summary>
    static void Reseal(MapDocument doc)
    {
        var digests = doc.Surfaces.Patches.Select(p => new KeyValuePair<MapPatchKey, string>(p.Key, MapSurfaceSemantics.PatchDigest(p.Value))).ToArray();
        for (int i = 0; i < doc.Surfaces.Refs.Count; i++)
        {
            MapSurfaceRef surface = doc.Surfaces.Refs[i];
            doc.Surfaces.Refs[i] = surface with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface, digests.Where(p => p.Key.SurfaceId == surface.Id)),
            };
        }
    }

    static MapWideRational Wide(MapExactValue value) => MapWideRational.From(value);
}
