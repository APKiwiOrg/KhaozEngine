using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Storage;

namespace KhaozEngine.Tests.MapDoc;

internal static class CaveFixtures
{
    internal static MapDocument RampChamberShaft()
    {
        var set = new MapSurfaceSet();
        AddSurface(set, "outer", MapSurfaceRole.SupportFloor, 0, 0, 8, 8, (_, _) => 1000,
            (x, z) => z != 2 || x < 2 || x > 5);
        AddSurface(set, "cave-floor", MapSurfaceRole.SupportFloor, 2, 2, 4, 6,
            (_, z) => z switch { 2 => 1000, 3 => 800, 4 => 600, _ => 400 },
            (x, z) => !IsShaftCell(x, z));
        AddSurface(set, "cave-ceiling", MapSurfaceRole.Ceiling, 2, 3, 4, 5, (_, _) => 900);
        AddSurface(set, "deep-floor", MapSurfaceRole.SupportFloor, 2, 5, 4, 3, (_, _) => -2000);
        AddSurface(set, "deep-ceiling", MapSurfaceRole.Ceiling, 2, 5, 4, 3, (_, _) => -1500,
            (x, z) => !IsShaftCell(x, z));

        MapSurfacePatch anchor = Patch(set, "cave-floor");
        var rimPairs = Enumerable.Range(2, 5).Select(x =>
            (First: Vertex("outer", x, 2), Second: Vertex("cave-floor", x, 2))).ToArray();
        anchor.Records.Add(new MapSurfaceSeam("rim-seam",
            new(Key("outer"), rimPairs[0].First, rimPairs[^1].First),
            new(anchor.Key, rimPairs[0].Second, rimPairs[^1].Second), rimPairs));
        AddPortal(set, "mouth", "chamber", "outside", 2, 3, 6, 3, "cave-floor", "cave-ceiling");
        AddStrip(set, "mouth-return", "cave-ceiling", "outer", 2, 3, 6, 3, MapStripFacing.Front);
        AddStrip(set, "ramp-west", "cave-floor", "outer", 2, 2, 2, 3, MapStripFacing.Front);
        AddStrip(set, "ramp-east", "cave-floor", "outer", 6, 2, 6, 3, MapStripFacing.Back);
        AddStrip(set, "west-wall", "cave-floor", "cave-ceiling", 2, 3, 2, 8, MapStripFacing.Front);
        AddStrip(set, "east-wall", "cave-floor", "cave-ceiling", 6, 3, 6, 8, MapStripFacing.Back);
        AddStrip(set, "back-wall", "cave-floor", "cave-ceiling", 2, 8, 6, 8, MapStripFacing.Front);
        anchor.Records.Add(new MapHorizontalOpening("shaft-top", anchor.Key, ShaftCells()));
        Patch(set, "deep-ceiling").Records.Add(new MapHorizontalOpening("shaft-bottom", Key("deep-ceiling"), ShaftCells()));
        AddPerimeter(set, "shaft", "deep-ceiling", "cave-floor", 3, 5, 5, 7);
        AddPerimeter(set, "deep", "deep-floor", "deep-ceiling", 2, 5, 6, 8);
        anchor.Records.Add(new MapVerticalLink("shaft-link", Ref("chamber"), Ref("deep"),
            new[] { Ref("shaft-top"), Ref("shaft-bottom") }, Array.Empty<MapRecordRef>(),
            new[] { Ref("shaft-west"), Ref("shaft-east"), Ref("shaft-south"), Ref("shaft-north") }));

        Patch(set, "outer").Records.Add(Space("outside", MapSpaceKind.Exterior,
            new[] { Side("mouth-return", MapSide.Front), Side("ramp-west", MapSide.Front), Side("ramp-east", MapSide.Back) },
            new[] { Side("mouth", MapSide.Back) }));
        Patch(set, "outer").Records.Add(Footprint("outside-outer", "outside", "outer",
            Cells(0, 0, 8, 8, (x, z) => z != 2 || x < 2 || x > 5), Floor("outer"), OpenTop));
        anchor.Records.Add(Footprint("outside-ramp", "outside", "cave-floor", Cells(2, 2, 6, 3), Floor("cave-floor"), OpenTop));
        anchor.Records.Add(Space("chamber", MapSpaceKind.Cave,
            new[] { Side("west-wall", MapSide.Front), Side("east-wall", MapSide.Back), Side("back-wall", MapSide.Front) },
            new[] { Side("mouth", MapSide.Front) }, new[] { Ref("shaft-link") }));
        anchor.Records.Add(Footprint("chamber-cells", "chamber", "cave-floor",
            Cells(2, 3, 6, 8, (x, z) => !IsShaftCell(x, z)), Floor("cave-floor"), Ceiling("cave-ceiling")));
        anchor.Records.Add(Footprint("chamber-shaft-cells", "chamber", "cave-floor", ShaftCells(),
            Opening("shaft-top"), Ceiling("cave-ceiling")));
        anchor.Records.Add(Space("shaft", MapSpaceKind.Cave, PerimeterSides("shaft")));
        anchor.Records.Add(Footprint("shaft-cells", "shaft", "cave-floor", ShaftCells(), Opening("shaft-bottom"), Opening("shaft-top")));
        MapSurfacePatch deep = Patch(set, "deep-floor");
        deep.Records.Add(Space("deep", MapSpaceKind.Cave, PerimeterSides("deep"), links: new[] { Ref("shaft-link") }));
        deep.Records.Add(Footprint("deep-cells", "deep", "deep-floor",
            Cells(2, 5, 6, 8, (x, z) => !IsShaftCell(x, z)), Floor("deep-floor"), Ceiling("deep-ceiling")));
        deep.Records.Add(Footprint("deep-shaft-cells", "deep", "deep-floor", ShaftCells(), Floor("deep-floor"), Opening("shaft-bottom")));
        return Document(set);
    }

    internal static MapDocument ShaftJoinedToLowerChamber()
    {
        MapDocument doc = RampChamberShaft();
        MapSurfaceSet set = doc.Surfaces;
        MapSurfacePatch anchor = Patch(set, "cave-floor");
        MapSurfacePatch deep = Patch(set, "deep-floor");
        Replace(anchor, Record<MapSpaceFootprint>(doc, "shaft-cells") with { Lower = Floor("deep-floor") });
        Patch(set, "deep-floor").Records.RemoveAll(r => r.Id == "deep-shaft-cells");
        Patch(set, "deep-ceiling").Records.RemoveAll(r => r.Id == "shaft-bottom");
        Replace(anchor, Edge("shaft-south-lower", "deep-floor", 3, 5, 5, 5));
        deep.Records.RemoveAll(r => r.Id == "deep-south");
        anchor.Records.RemoveAll(r => r.Id is "deep-south-lower" or "deep-south-upper");
        foreach (var segment in new[] { (Id: "deep-south-w", MinX: 2, MaxX: 3), (Id: "deep-south-e", MinX: 5, MaxX: 6) })
        {
            anchor.Records.Add(Edge(segment.Id + "-lower", "deep-floor", segment.MinX, 5, segment.MaxX, 5));
            anchor.Records.Add(Edge(segment.Id + "-upper", "deep-ceiling", segment.MinX, 5, segment.MaxX, 5));
            deep.Records.Add(new MapWallStrip(segment.Id, Ref(segment.Id + "-lower"), Ref(segment.Id + "-upper"), MapStripFacing.Back, 1));
        }
        AddPortal(set, "shaft-portal-w", "shaft", "deep", 3, 5, 3, 7, "deep-floor", "deep-ceiling");
        AddPortal(set, "shaft-portal-e", "shaft", "deep", 5, 5, 5, 7, "deep-floor", "deep-ceiling");
        AddPortal(set, "shaft-portal-n", "shaft", "deep", 3, 7, 5, 7, "deep-floor", "deep-ceiling");
        string[] portals = { "shaft-portal-w", "shaft-portal-e", "shaft-portal-n" };
        Replace(anchor, Record<MapSpaceDoc>(doc, "shaft") with
        {
            Portals = portals.Select(id => Side(id, MapSide.Front)).ToArray(),
        });
        Replace(Patch(set, "deep-floor"), Record<MapSpaceDoc>(doc, "deep") with
        {
            Walls = new[]
            {
                Side("deep-west", MapSide.Front), Side("deep-east", MapSide.Back),
                new MapBoundaryRef(new("deep-south-w", deep.Key), MapSide.Back),
                new MapBoundaryRef(new("deep-south-e", deep.Key), MapSide.Back), Side("deep-north", MapSide.Front),
            },
            Portals = portals.Select(id => Side(id, MapSide.Back)).ToArray(),
        });
        Replace(anchor, Record<MapVerticalLink>(doc, "shaft-link") with
        {
            Openings = new[] { Ref("shaft-top") },
            Portals = portals.Select(Ref).ToArray(),
        });
        return Document(set);
    }

    internal static MapDocument WithoutOpening(string id)
    {
        if (id != "shaft-top") throw new ArgumentException("unknown cave opening fixture", nameof(id));
        MapDocument doc = RampChamberShaft();
        MapSurfacePatch anchor = Patch(doc.Surfaces, "cave-floor");
        anchor.Records.RemoveAll(r => r.Id == id);
        Replace(anchor, Record<MapSpaceFootprint>(doc, "chamber-shaft-cells") with { Lower = Floor("cave-floor") });
        return Document(doc.Surfaces, new[]
        {
            "missing record 'shaft-top' in 'shaft-cells'",
            "missing record 'shaft-top' in 'shaft-link'",
        });
    }

    internal static MapDocument WithPeerChamber(string? aliasOf)
    {
        MapDocument doc = RampChamberShaft();
        MapSurfacePatch anchor = Patch(doc.Surfaces, "cave-floor");
        anchor.Records.Add(Space("chamber-copy", MapSpaceKind.Cave, Array.Empty<MapBoundaryRef>()) with
        {
            AliasOf = aliasOf is null ? null : Ref(aliasOf),
        });
        anchor.Records.Add(Record<MapSpaceFootprint>(doc, "chamber-cells") with
        {
            Id = "chamber-copy-cells",
            Space = new("chamber-copy", anchor.Key),
        });
        return Document(doc.Surfaces);
    }

    internal static MapDocument IndoorRow()
    {
        var set = new MapSurfaceSet();
        var key = Key("plane-0");
        set.Refs.Add(new("plane-0", MapLatticeFrame.ImportedMetreCentimetre, MapSurfaceRole.SupportFloor,
            MapPresencePolicy.LegacyTileWorld, null, new("indoor-0", new("world", key), 0, 450, new[] { "indoor" }), ""));
        MapSurfacePatch patch = MixedResolutionFixtures.Patch(key, 2, 1, (_, _) => 0);
        patch.Cells[0] = patch.Cells[0] with { Flags = MapCellFlags.Indoor };
        patch.Records.Add(new MapSpaceDoc("world", MapSpaceKind.Exterior, null, null, Array.Empty<string>(),
            Array.Empty<MapBoundaryRef>(), Array.Empty<MapBoundaryRef>(), Array.Empty<MapRecordRef>()));
        patch.Records.Add(new MapSpaceFootprint("world-cells", new("world", key), key, new[] { 0, 1 }, Floor("plane-0"), OpenTop));
        set.Patches.Add(key, patch);
        return Document(set);
    }

    internal static MapScopedSurfaces View(MapDocument doc) => MapScopedSurfaces.CompleteView(doc.Surfaces);

    internal static MapCompiledPatch Compile(MapDocument doc, string surfaceId) => MapSurfaceCompiler.Compile(
        doc.Surfaces.Refs.Single(s => s.Id == surfaceId), doc.Surfaces.Patches[Key(surfaceId)]);

    internal static T Record<T>(MapDocument doc, string id) where T : MapTopologyRecord
        => (T)doc.Surfaces.AllRecords().Single(r => r.Id == id);

    internal static MapMembershipResult Query(MapDocument doc, float x, float y, float z, MapQueryLimits? limits = null)
        => Query(MapDocumentSurfaceSource.Capture(doc), x, y, z, limits);

    internal static MapMembershipResult Query(IMapSurfaceSource source, float x, float y, float z, MapQueryLimits? limits = null)
    {
        MapScopedSurfaces scoped = ScopeFixtures.Acquire(source, ScopeFixtures.Around(WorldFrame.Origin, x, z, half: 2, limits: limits));
        return new MapSpaceMembership(scoped).Query(new MapFramePoint(WorldFrame.Origin, new(x, y, z)));
    }

    internal static MapDocument Document(MapSurfaceSet set, IReadOnlyList<string>? expectedReferences = null,
        string? invalidStrip = null)
    {
        foreach (MapSurfacePatch patch in set.Patches.Values)
            RequireEmpty(patch.ValidateLocal(), "local validation");
        IReadOnlyList<string> references = MapTopologyReferenceValidator.Validate(set.Refs, set.Patches.Values.ToArray());
        if (!references.Order(StringComparer.Ordinal).SequenceEqual((expectedReferences ?? Array.Empty<string>()).Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("occupied-space fixture reference validation differs: " + string.Join(", ", references));
        MapScopedSurfaces view = MapScopedSurfaces.CompleteView(set);
        foreach (MapSurfaceSeam seam in set.AllRecords().OfType<MapSurfaceSeam>())
            RequireEmpty(MapSeamValidator.Validate(seam, view), "seam validation");
        foreach (MapSurfacePatch patch in set.Patches.Values)
            RequireEmpty(MapSeamValidator.ValidateCornerDependencies(patch, view), "corner validation");
        foreach (MapBoundaryChain chain in set.AllRecords().OfType<MapBoundaryChain>())
        {
            MapChainResolution resolution = MapBoundaryGeometry.ResolveChain(chain, view);
            if (resolution.Status != MapResolveStatus.Resolved)
                throw new InvalidOperationException("occupied-space fixture chain validation failed: " + resolution.Detail);
        }
        foreach (MapWallStrip strip in set.AllRecords().OfType<MapWallStrip>())
        {
            var lower = (MapBoundaryChain)set.AllRecords().Single(r => r.Id == strip.LowerChain.Id);
            var upper = (MapBoundaryChain)set.AllRecords().Single(r => r.Id == strip.UpperChain.Id);
            MapChainResolution lowerPoints = MapBoundaryGeometry.ResolveChain(lower, view);
            MapChainResolution upperPoints = MapBoundaryGeometry.ResolveChain(upper, view);
            if (strip.Id == invalidStrip)
            {
                try { _ = MapWallStripCompiler.Compile(strip, lowerPoints, upperPoints); }
                catch (MapDocumentException error) when (error.Message == "wall strip vertex sequence mismatch") { continue; }
                throw new InvalidOperationException("occupied-space fixture expected a vertex sequence refusal");
            }
            _ = MapWallStripCompiler.Compile(strip, lowerPoints, upperPoints);
        }
        foreach (MapHorizontalOpening opening in set.AllRecords().OfType<MapHorizontalOpening>())
            _ = MapOpeningBoundary.Compile(set.Refs.Single(s => s.Id == opening.Patch.SurfaceId), set.Patches[opening.Patch], opening);
        return MixedResolutionFixtures.Document(set);
    }

    internal static void AddSurface(MapSurfaceSet set, string id, MapSurfaceRole role,
        int minX, int minZ, int width, int depth, Func<int, int, int> height, Func<int, int, bool>? present = null)
    {
        set.Refs.Add(MixedResolutionFixtures.Surface(id, 1, role));
        MapSurfacePatch patch = MixedResolutionFixtures.Patch(Key(id), width, depth, (x, z) => height(x + minX, z + minZ));
        patch.CellMinX = minX;
        patch.CellMinZ = minZ;
        if (present is not null)
            for (int z = 0; z < depth; z++)
                for (int x = 0; x < width; x++)
                    patch.SetPresent(x, z, present(x + minX, z + minZ));
        set.Patches.Add(patch.Key, patch);
    }

    internal static MapBoundaryChain Edge(string id, string surface, int x0, int z0, int x1, int z1)
        => new(id, MapChainKind.SurfaceEdge, Key(surface), Vertices(surface, x0, z0, x1, z1).Select(v => new MapChainVertex(v, null)).ToArray());

    internal static MapLatticeVertex[] Vertices(string surface, int x0, int z0, int x1, int z1)
    {
        int steps = Math.Max(Math.Abs(x1 - x0), Math.Abs(z1 - z0));
        return Enumerable.Range(0, steps + 1).Select(i => Vertex(surface,
            x0 + Math.Sign(x1 - x0) * i, z0 + Math.Sign(z1 - z0) * i)).ToArray();
    }

    internal static int[] Cells(int minX, int minZ, int maxX, int maxZ, Func<int, int, bool>? include = null)
        => Enumerable.Range(minZ, maxZ - minZ).SelectMany(z => Enumerable.Range(minX, maxX - minX)
            .Where(x => include is null || include(x, z)).Select(x => z * 64 + x)).ToArray();

    internal static MapPatchKey Key(string surface) => new(surface, 0, 0);
    internal static MapLatticeVertex Vertex(string surface, int x, int z) => new(surface, MapLatticeAddress.Corner(x, z));
    internal static MapSurfacePatch Patch(MapSurfaceSet set, string surface) => set.Patches[Key(surface)];
    internal static MapBoundRef Floor(string surface) => new(MapBoundKind.SupportFloor, surface, null);
    internal static MapBoundRef Ceiling(string surface) => new(MapBoundKind.Ceiling, surface, null);
    internal static MapBoundRef OpenTop => new(MapBoundKind.OpenTop, null, null);

    static MapBoundRef Opening(string id) => new(MapBoundKind.HorizontalOpening, null, Ref(id));
    static bool IsShaftCell(int x, int z) => x is >= 3 and <= 4 && z is >= 5 and <= 6;
    static int[] ShaftCells() => Cells(3, 5, 5, 7);
    static MapRecordRef Ref(string id) => new(id, Key(id switch
    {
        "outside" or "outside-outer" => "outer",
        "shaft-bottom" => "deep-ceiling",
        "deep" or "deep-cells" or "deep-shaft-cells" or "deep-west" or "deep-east" or "deep-south" or "deep-north" => "deep-floor",
        _ => "cave-floor",
    }));
    static MapBoundaryRef Side(string id, MapSide side) => new(Ref(id), side);
    static MapBoundaryRef[] PerimeterSides(string prefix) => new[]
    {
        Side(prefix + "-west", MapSide.Front), Side(prefix + "-east", MapSide.Back),
        Side(prefix + "-south", MapSide.Back), Side(prefix + "-north", MapSide.Front),
    };
    static MapSpaceDoc Space(string id, MapSpaceKind kind, IReadOnlyList<MapBoundaryRef> walls,
        IReadOnlyList<MapBoundaryRef>? portals = null, IReadOnlyList<MapRecordRef>? links = null)
        => new(id, kind, null, null, Array.Empty<string>(), walls, portals ?? Array.Empty<MapBoundaryRef>(), links ?? Array.Empty<MapRecordRef>());
    static MapSpaceFootprint Footprint(string id, string space, string lattice, IReadOnlyList<int> cells, MapBoundRef lower, MapBoundRef upper)
        => new(id, Ref(space), Key(lattice), cells, lower, upper);
    static void Replace(MapSurfacePatch anchor, MapTopologyRecord record)
        => anchor.Records[anchor.Records.FindIndex(r => r.Id == record.Id)] = record;

    static void AddStrip(MapSurfaceSet set, string id, string lower, string upper,
        int x0, int z0, int x1, int z1, MapStripFacing facing)
    {
        MapSurfacePatch anchor = Patch(set, "cave-floor");
        anchor.Records.Add(Edge(id + "-lower", lower, x0, z0, x1, z1));
        anchor.Records.Add(Edge(id + "-upper", upper, x0, z0, x1, z1));
        Patch(set, Ref(id).Anchor.SurfaceId).Records.Add(new MapWallStrip(id, Ref(id + "-lower"), Ref(id + "-upper"), facing, 1));
    }

    static void AddPerimeter(MapSurfaceSet set, string prefix, string lower, string upper, int minX, int minZ, int maxX, int maxZ)
    {
        AddStrip(set, prefix + "-west", lower, upper, minX, minZ, minX, maxZ, MapStripFacing.Front);
        AddStrip(set, prefix + "-east", lower, upper, maxX, minZ, maxX, maxZ, MapStripFacing.Back);
        AddStrip(set, prefix + "-south", lower, upper, minX, minZ, maxX, minZ, MapStripFacing.Back);
        AddStrip(set, prefix + "-north", lower, upper, minX, maxZ, maxX, maxZ, MapStripFacing.Front);
    }

    static void AddPortal(MapSurfaceSet set, string id, string from, string to,
        int x0, int z0, int x1, int z1, string lower, string upper)
    {
        MapSurfacePatch anchor = Patch(set, "cave-floor");
        anchor.Records.Add(Edge(id + "-bottom", lower, x0, z0, x1, z1));
        anchor.Records.Add(Edge(id + "-top", upper, x0, z0, x1, z1));
        anchor.Records.Add(new MapCavePortal(id, Ref(from), Ref(to), Vertices("cave-floor", x0, z0, x1, z1),
            Ref(id + "-bottom"), Ref(id + "-top")));
    }

    static void RequireEmpty(IReadOnlyList<string> errors, string operation)
    {
        if (errors.Count != 0)
            throw new InvalidOperationException("occupied-space fixture " + operation + " failed: " + string.Join(", ", errors));
    }

    internal static MapDocument ClosedMouth()
    {
        MapSurfaceSet set = RampChamberShaft().Surfaces;
        MapSurfacePatch outer = Patch(set, "outer");
        for (int z = 0; z < outer.Depth; z++)
            for (int x = 0; x < outer.Width; x++) outer.SetPresent(x, z, true);
        Replace(outer, ((MapSpaceDoc)outer.Records.Single(r => r.Id == "outside")) with
        {
            Walls = Array.Empty<MapBoundaryRef>(),
            Portals = Array.Empty<MapBoundaryRef>(),
        });
        Replace(outer, ((MapSpaceFootprint)outer.Records.Single(r => r.Id == "outside-outer")) with
        {
            SlotCells = Cells(0, 0, 8, 8),
        });
        MapSurfacePatch ramp = Patch(set, "cave-floor");
        var floor = new MapSurfacePatch
        {
            Key = ramp.Key,
            CellMinX = ramp.CellMinX,
            CellMinZ = 3,
            Width = ramp.Width,
            Depth = ramp.Depth - 1,
            Heights = ramp.Heights.Skip(ramp.Width + 1).ToArray(),
            Cells = ramp.Cells.Skip(ramp.Width).ToArray(),
            Presence = new[] { ramp.Presence[0] >> ramp.Width },
        };
        floor.Records.AddRange(ramp.Records.Where(r => r.Id is not ("outside-ramp" or "rim-seam" or "mouth") &&
            !r.Id.StartsWith("ramp-west", StringComparison.Ordinal) &&
            !r.Id.StartsWith("ramp-east", StringComparison.Ordinal) &&
            !r.Id.StartsWith("mouth-", StringComparison.Ordinal)));
        set.Patches[floor.Key] = floor;
        MapSpaceDoc chamber = (MapSpaceDoc)floor.Records.Single(r => r.Id == "chamber");
        Replace(floor, chamber with
        {
            Walls = chamber.Walls.Append(Side("mouth-seal", MapSide.Back)).ToArray(),
            Portals = Array.Empty<MapBoundaryRef>(),
        });
        AddStrip(set, "mouth-seal", "cave-floor", "cave-ceiling", 2, 3, 6, 3, MapStripFacing.Back);
        MapDocument doc = Document(set);
        RequireEmpty(MapSpaceCoverageValidator.Validate(View(doc)), "coverage validation");
        return doc;
    }

    internal static KhaozEngine.MapDoc.Editing.MapReplaceTopology OpenMouthEdit()
    {
        MapSurfaceSet set = RampChamberShaft().Surfaces;
        return new(new[] { Patch(set, "outer"), Patch(set, "cave-floor") }, Array.Empty<MapPatchKey>(),
            Array.Empty<MapSurfaceRef>(), Array.Empty<string>());
    }

    internal static KhaozEngine.MapDoc.Editing.MapReplaceTopology CeilingCrossingEdit()
    {
        MapSurfacePatch ceiling = Patch(RampChamberShaft().Surfaces, "cave-ceiling");
        for (int x = 0; x <= ceiling.Width; x++) ceiling.Heights[x] = 700;
        return new(new[] { ceiling }, Array.Empty<MapPatchKey>(), Array.Empty<MapSurfaceRef>(), Array.Empty<string>());
    }
}
