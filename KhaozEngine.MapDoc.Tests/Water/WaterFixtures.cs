using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.Tests.MapDoc.Water;

/// <summary>Bounded water fixtures. Lattices default to <see cref="MapLatticeFrame.ImportedMetreCentimetre"/>, so
/// lattice row r covers world z from -(r + 1) to -r and heights are centimetres.</summary>
internal static partial class WaterFixtures
{
    internal static MapLatticeFrame Frame => MapLatticeFrame.ImportedMetreCentimetre;
    const string Medium = "sea-water";
    const string Material = "water-material";

    /// <summary>Seabed under an open sea, with a dry cave, a cave lake and a flooded cave below it, each cave closed by
    /// walls so the world passes R2 coverage.</summary>
    internal static MapDocument OceanOverCaves()
    {
        var set = new MapSurfaceSet();
        Surface(set, "seabed", MapSurfaceRole.SupportFloor, Frame, 0, 0, 12, 4, (x, _) => SeabedHeight(x));
        Surface(set, "dry-floor", MapSurfaceRole.SupportFloor, Frame, 0, 0, 6, 4, (_, _) => -6000);
        Surface(set, "dry-roof", MapSurfaceRole.Ceiling, Frame, 0, 0, 6, 4, (_, _) => -5000);
        Surface(set, "lake-floor", MapSurfaceRole.SupportFloor, Frame, 0, 0, 6, 4, (_, _) => -4000);
        Surface(set, "lake-roof", MapSurfaceRole.Ceiling, Frame, 0, 0, 6, 4, (x, _) => -3600 + 100 * (int)Math.Min(x, 4));
        Surface(set, "flood-floor", MapSurfaceRole.SupportFloor, Frame, 6, 0, 8, 4, (_, _) => -4000);
        Surface(set, "flood-roof", MapSurfaceRole.Ceiling, Frame, 6, 0, 8, 4, (_, _) => -2500);

        Footprints(set, "sea", MapSpaceKind.Exterior, Frame, 0, 0, 12, 4, Floor("seabed"), OpenTop, Slot0("seabed"));
        Footprints(set, "dry-cave", MapSpaceKind.Cave, Frame, 0, 0, 6, 4, Floor("dry-floor"), Ceiling("dry-roof"), Slot0("dry-floor"));
        CloseWalls(set, "dry-cave", "dry-floor", "dry-roof", 0, 0, 6, 4);
        Footprints(set, "lake-cave", MapSpaceKind.Cave, Frame, 0, 0, 6, 4, Floor("lake-floor"), Ceiling("lake-roof"), Slot0("lake-floor"));
        CloseWalls(set, "lake-cave", "lake-floor", "lake-roof", 0, 0, 6, 4);
        Footprints(set, "flooded-cave", MapSpaceKind.Cave, Frame, 6, 0, 8, 4, Floor("flood-floor"), Ceiling("flood-roof"), Slot0("flood-floor"));
        CloseWalls(set, "flooded-cave", "flood-floor", "flood-roof", 6, 0, 8, 4);

        var water = new MapWaterSet();
        water.Media.Add(new MapWaterMediumDoc(Medium, MapWaterMediumKind.Water, 1f));
        water.Bodies.Add(Body("ocean", 0f, Rect(0, 0, 12, 4), Cover("sea", "seabed")));
        water.Bodies.Add(Body("cave-lake", -34f, Rect(0, 0, 6, 4), Cover("lake-cave", "lake-floor")));
        water.Bodies.Add(Body("flooded", -20f, Rect(6, 0, 8, 4), Cover("flooded-cave", "flood-floor")));
        return Document(set, water);
    }

    /// <summary><see cref="OceanOverCaves"/> plus body <c>ring</c>, one ring domain over the given addresses that
    /// covers <c>dry-cave</c>.</summary>
    internal static MapDocument WithRingBody(params MapLatticeAddress[] vertices)
    {
        MapDocument doc = OceanOverCaves();
        doc.Water!.Bodies.Add(new MapWaterBodyDoc("ring", -55f, Medium, Material,
            new MapWaterDomainDoc(Frame, Array.Empty<MapWaterRect>(), new[] { new MapWaterRing(vertices.ToArray()) }),
            new[] { Cover("dry-cave", "dry-floor") }));
        return Validated(doc);
    }

    /// <summary>The saved text of the default <see cref="WithRingBody"/> document with its first ring address
    /// damaged as named.</summary>
    internal static string CorruptAddressJson(string variant)
    {
        MapDocument doc = WithRingBody(MapLatticeAddress.Create(-1, -1, 2), MapLatticeAddress.Create(704, 64, 64),
            MapLatticeAddress.Create(11, 3, 1), MapLatticeAddress.Create(1, 10, 3));
        string text = JsonNode.Parse(MapDocumentFile.SaveText(doc))!.ToJsonString();
        const string first = "{\"x\":\"-1\",\"z\":\"-1\",\"d\":2}";
        string damaged = variant switch
        {
            "address-missing-d" => "{\"x\":\"-1\",\"z\":\"-1\"}",
            "address-unknown-member" => "{\"x\":\"-1\",\"z\":\"-1\",\"d\":2,\"w\":\"0\"}",
            "address-duplicate-member" => "{\"x\":\"-1\",\"x\":\"-1\",\"z\":\"-1\",\"d\":2}",
            "address-leading-zero" => "{\"x\":\"-01\",\"z\":\"-1\",\"d\":2}",
            "address-denominator-128" => "{\"x\":\"-1\",\"z\":\"-1\",\"d\":128}",
            _ => throw new ArgumentException("unknown address corruption", nameof(variant)),
        };
        int at = text.IndexOf(first, StringComparison.Ordinal);
        if (at < 0 || text.IndexOf(first, at + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("the first ring address must appear exactly once in the saved text");
        return string.Concat(text.AsSpan(0, at), damaged, text.AsSpan(at + first.Length));
    }

    /// <summary>The smallest valid format-5 resolver-2 document text.</summary>
    internal static string FormatFiveNativeJson() => """
        {
          "formatVersion": 5,
          "id": "format-five",
          "bounds": { "minX": -64, "minZ": -64, "maxX": 64, "maxZ": 64 },
          "playableBounds": { "minX": -64, "minZ": -64, "maxX": 64, "maxZ": 64 },
          "tileSize": 64,
          "terrain": {},
          "supportRecipe": "AuthoredBindingsV2",
          "surfaces": [],
          "nativeAssets": [],
          "numericIdHighWaterMark": "0",
          "resolverIdentity": { "payloadVersion": 1, "resolverVersion": 2 }
        }
        """;

    /// <summary>Adds a surface ref and every patch the lattice cell range touches, split across 64-cell slots.
    /// <paramref name="heightUnits"/> receives lattice corner coordinates and returns frame height units.</summary>
    internal static void Surface(MapSurfaceSet set, string id, MapSurfaceRole role, MapLatticeFrame frame,
        long cellMinX, long cellMinZ, long cellMaxX, long cellMaxZ, Func<long, long, int> heightUnits)
    {
        set.Refs.Add(new MapSurfaceRef(id, frame, role, MapPresencePolicy.Native, null, null, ""));
        foreach (SlotRange slot in Slots(cellMinX, cellMinZ, cellMaxX, cellMaxZ))
        {
            var key = new MapPatchKey(id, slot.SlotX, slot.SlotZ);
            long originX = slot.SlotX * MapPatchKey.SlotCells + slot.MinX;
            long originZ = slot.SlotZ * MapPatchKey.SlotCells + slot.MinZ;
            MapSurfacePatch patch = MixedResolutionFixtures.Patch(key, slot.Width, slot.Depth,
                (x, z) => heightUnits(originX + x, originZ + z));
            patch.CellMinX = slot.MinX;
            patch.CellMinZ = slot.MinZ;
            set.Patches.Add(key, patch);
        }
    }

    /// <summary>Adds the space record once, anchored at <paramref name="anchor"/>, and one footprint per touched
    /// slot on the lower bound's lattice, each anchored in that slot's floor patch.</summary>
    internal static void Footprints(MapSurfaceSet set, string spaceId, MapSpaceKind kind, MapLatticeFrame frame,
        long cellMinX, long cellMinZ, long cellMaxX, long cellMaxZ, MapBoundRef lower, MapBoundRef upper, MapPatchKey anchor)
    {
        string lattice = lower.SurfaceId ?? anchor.SurfaceId;
        if (set.Refs.Single(s => s.Id == lattice).Frame != frame)
            throw new InvalidOperationException($"footprint lattice '{lattice}' is not in the requested frame");
        set.Patches[anchor].Records.Add(new MapSpaceDoc(spaceId, kind, null, null, Array.Empty<string>(),
            Array.Empty<MapBoundaryRef>(), Array.Empty<MapBoundaryRef>(), Array.Empty<MapRecordRef>()));
        FootprintCells(set, spaceId, spaceId, cellMinX, cellMinZ, cellMaxX, cellMaxZ, lower, upper, anchor);
    }

    /// <summary>Adds one footprint per touched slot for an existing space, named from <paramref name="prefix"/>, so
    /// one space can own cells over more than one lower bound.</summary>
    static void FootprintCells(MapSurfaceSet set, string spaceId, string prefix, long cellMinX, long cellMinZ,
        long cellMaxX, long cellMaxZ, MapBoundRef lower, MapBoundRef upper, MapPatchKey anchor)
    {
        string lattice = lower.SurfaceId ?? anchor.SurfaceId;
        foreach (SlotRange slot in Slots(cellMinX, cellMinZ, cellMaxX, cellMaxZ))
        {
            var key = new MapPatchKey(lattice, slot.SlotX, slot.SlotZ);
            int[] cells = Enumerable.Range(slot.MinZ, slot.Depth)
                .SelectMany(z => Enumerable.Range(slot.MinX, slot.Width).Select(x => z * MapPatchKey.SlotCells + x)).ToArray();
            set.Patches[key].Records.Add(new MapSpaceFootprint($"{prefix}-cells-{slot.SlotX}-{slot.SlotZ}",
                new MapRecordRef(spaceId, anchor), key, cells, lower, upper));
        }
    }

    /// <summary>Seals refs and digests the way <see cref="CaveFixtures.Document"/> does, then attaches the water set
    /// and the roots of <see cref="Assets"/>.</summary>
    internal static MapDocument Document(MapSurfaceSet set, MapWaterSet? water)
    {
        MapDocument doc = CaveFixtures.Document(set);
        doc.NativeAssets = AssetInputs().Roots.ToList();
        doc.Water = water;
        return Validated(doc);
    }

    /// <summary>The fixture asset closure: the shared native fixture resources plus the water material.</summary>
    internal static MapAssetClosure Assets()
    {
        var (roots, source) = AssetInputs();
        return MapAssetClosure.Load(roots, source);
    }

    /// <summary>The exact XZ area of a triangle or of a set of polygons, by the shoelace sum in wide arithmetic.</summary>
    internal static MapWideRational WideArea(params MapWidePoint[] triangle) => WideArea(new IReadOnlyList<MapWidePoint>[] { triangle });

    internal static MapWideRational WideArea(IReadOnlyList<IReadOnlyList<MapWidePoint>> polygons)
    {
        MapWideRational total = default;
        foreach (IReadOnlyList<MapWidePoint> polygon in polygons)
        {
            MapWideRational twice = default;
            for (int i = 0; i < polygon.Count; i++)
            {
                MapWidePoint a = polygon[i], b = polygon[(i + 1) % polygon.Count];
                twice = twice.Add(a.X.Multiply(b.Z)).Subtract(b.X.Multiply(a.Z));
            }
            total = total.Add(twice.Sign < 0 ? default(MapWideRational).Subtract(twice) : twice);
        }
        return total.Divide(MapWideRational.From(new MapExactValue(2, 1)));
    }

    internal static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "mapdoc-water-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    static (IReadOnlyList<MapAssetRef> Roots, NativeMemoryAssetSource Source) AssetInputs()
    {
        var (roots, source) = NativeAssetFixtures.Valid();
        MapAssetRef material = source.Add(Material, Encoding.UTF8.GetBytes("bytes:" + Material));
        MapAssetRef water = source.Add("root-water", new JsonObject
        {
            ["payloadVersion"] = 1,
            ["assets"] = new JsonArray(),
            ["resources"] = new JsonArray(NativeAssetFixtures.Resource(material, "Material")),
        }.ToJsonString());
        return (roots.Append(water).ToArray(), source);
    }

    static MapDocument Validated(MapDocument doc)
    {
        IReadOnlyList<string> errors = MapDocumentValidator.Validate(doc, MapDocRegistry.CreateDefault());
        if (errors.Count != 0)
            throw new InvalidOperationException("water fixture validation failed: " + string.Join(", ", errors));
        return doc;
    }

    static int SeabedHeight(long x) => x <= 8 ? -2000 : x >= 10 ? 200 : -2000 + (int)(x - 8) * 1100;

    static MapWaterBodyDoc Body(string id, float surfaceY, MapWaterRect rect, MapRecordRef cover) =>
        new(id, surfaceY, Medium, Material, new MapWaterDomainDoc(Frame, new[] { rect }, Array.Empty<MapWaterRing>()),
            new[] { cover });

    static MapWaterRect Rect(long minX, long minZ, long maxX, long maxZ) => new(minX, minZ, maxX, maxZ);
    static MapRecordRef Cover(string spaceId, string floor) => new(spaceId, Slot0(floor));
    static MapPatchKey Slot0(string surfaceId) => new(surfaceId, 0, 0);
    static MapBoundRef Floor(string surfaceId) => new(MapBoundKind.SupportFloor, surfaceId, null);
    static MapBoundRef Ceiling(string surfaceId) => new(MapBoundKind.Ceiling, surfaceId, null);
    static MapBoundRef OpenTop => new(MapBoundKind.OpenTop, null, null);

    /// <summary>Closes a slot-0 cave rectangle with wall strips between floor and roof edges, leaving the west edge
    /// open when <paramref name="west"/> is false. Each strip faces the interior, so its front side follows the
    /// frame's row direction.</summary>
    static void CloseWalls(MapSurfaceSet set, string spaceId, string floor, string roof, int minX, int minZ, int maxX, int maxZ,
        bool west = true) =>
        CloseWalls(set, spaceId, floor, (id, x0, z0, x1, z1) => Edge(id, roof, x0, z0, x1, z1), minX, minZ, maxX, maxZ, west);

    /// <summary>Closes a slot-0 cave rectangle as above, with each strip's upper chain built by
    /// <paramref name="roofChain"/> from its id and lattice end corners.</summary>
    static void CloseWalls(MapSurfaceSet set, string spaceId, string floor, Func<string, int, int, int, int, MapBoundaryChain> roofChain,
        int minX, int minZ, int maxX, int maxZ, bool west = true)
    {
        if (minX < 0 || minZ < 0 || maxX > MapPatchKey.SlotCells || maxZ > MapPatchKey.SlotCells)
            throw new InvalidOperationException("fixture walls are limited to slot 0");
        MapSurfacePatch anchor = set.Patches[Slot0(floor)];
        int rows = set.Refs.Single(s => s.Id == floor).Frame.RowDirection == MapRowDirection.NegativeZ ? -1 : 1;
        var walls = new List<MapBoundaryRef>();
        foreach (var (name, x0, z0, x1, z1, inX, inZ) in new[]
        {
            ("west", minX, minZ, minX, maxZ, 1, 0), ("east", maxX, minZ, maxX, maxZ, -1, 0),
            ("south", minX, minZ, maxX, minZ, 0, 1), ("north", minX, maxZ, maxX, maxZ, 0, -1),
        })
        {
            if (!west && name == "west") continue;
            string id = $"{spaceId}-{name}";
            anchor.Records.Add(Edge(id + "-lower", floor, x0, z0, x1, z1));
            anchor.Records.Add(roofChain(id + "-upper", x0, z0, x1, z1));
            // The front normal is (dz, 0, -dx) in world XZ, and world Z is lattice Z times the row sign.
            bool front = rows * (Math.Sign(z1 - z0) * inX - Math.Sign(x1 - x0) * inZ) > 0;
            anchor.Records.Add(new MapWallStrip(id, new(id + "-lower", anchor.Key), new(id + "-upper", anchor.Key),
                front ? MapStripFacing.Front : MapStripFacing.Back, 1));
            walls.Add(new MapBoundaryRef(new(id, anchor.Key), front ? MapSide.Front : MapSide.Back));
        }
        int index = anchor.Records.FindIndex(r => r.Id == spaceId);
        anchor.Records[index] = ((MapSpaceDoc)anchor.Records[index]) with { Walls = walls.ToArray() };
    }

    static MapBoundaryChain Edge(string id, string surface, int x0, int z0, int x1, int z1)
    {
        int steps = Math.Max(Math.Abs(x1 - x0), Math.Abs(z1 - z0));
        MapChainVertex[] vertices = Enumerable.Range(0, steps + 1).Select(i => new MapChainVertex(new MapLatticeVertex(surface,
            MapLatticeAddress.Corner(x0 + Math.Sign(x1 - x0) * i, z0 + Math.Sign(z1 - z0) * i)), null)).ToArray();
        return new(id, MapChainKind.SurfaceEdge, Slot0(surface), vertices);
    }

    readonly record struct SlotRange(long SlotX, long SlotZ, int MinX, int MinZ, int Width, int Depth);

    static IEnumerable<SlotRange> Slots(long cellMinX, long cellMinZ, long cellMaxX, long cellMaxZ)
    {
        if (cellMinX >= cellMaxX || cellMinZ >= cellMaxZ) throw new ArgumentException("empty fixture cell range");
        const long size = MapPatchKey.SlotCells;
        for (long sz = FloorSlot(cellMinZ); sz * size < cellMaxZ; sz++)
            for (long sx = FloorSlot(cellMinX); sx * size < cellMaxX; sx++)
            {
                long minX = Math.Max(cellMinX, sx * size) - sx * size, maxX = Math.Min(cellMaxX, (sx + 1) * size) - sx * size;
                long minZ = Math.Max(cellMinZ, sz * size) - sz * size, maxZ = Math.Min(cellMaxZ, (sz + 1) * size) - sz * size;
                yield return new(sx, sz, (int)minX, (int)minZ, (int)(maxX - minX), (int)(maxZ - minZ));
            }
    }

    static long FloorSlot(long cell) => cell >= 0 ? cell / MapPatchKey.SlotCells : -((-cell + MapPatchKey.SlotCells - 1) / MapPatchKey.SlotCells);
}
