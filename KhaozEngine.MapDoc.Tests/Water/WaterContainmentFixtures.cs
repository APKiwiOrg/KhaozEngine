using System;
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
    const string Sinkhole = "sinkhole-floor";
    const string SinkFloor = "sink-floor";

    /// <summary>An exterior <c>sea</c> over <c>sinkhole-floor</c> at -10 m for x 0 to 8 and world z -4 to 0, whose
    /// cells x 4 to 6 are absent and form the horizontal opening <c>sinkhole-opening</c> at
    /// <paramref name="openingY"/> into the cave <c>sink</c> below, floored by <c>sink-floor</c> at -30 m and roofed
    /// by the opening plane. The corners at x 4, 5 and 6 sit at <paramref name="openingY"/>, so cells x 3 and x 6
    /// slope. One <c>MapVerticalLink</c> joins the two spaces. Every space, footprint, wall and link record anchors in
    /// <c>sink-floor</c>, so unloading <c>sinkhole-floor</c>, the opening's owner, leaves both covers readable. Body
    /// <c>ocean</c> at <c>SurfaceY</c> 0 spans x 0 to 8 and covers <c>sea</c> and <c>sink</c>.</summary>
    internal static MapDocument SinkholeCave(float openingY)
    {
        int opening = (int)MathF.Round(openingY * 100);
        var set = new MapSurfaceSet();
        Surface(set, Sinkhole, MapSurfaceRole.SupportFloor, Frame, 0, 0, 8, 4, (x, _) => x is >= 4 and <= 6 ? opening : -1000);
        Surface(set, SinkFloor, MapSurfaceRole.SupportFloor, Frame, 4, 0, 6, 4, (_, _) => -3000);

        MapSurfacePatch floor = set.Patches[Slot0(Sinkhole)];
        int[] holeCells = Enumerable.Range(0, 4).SelectMany(z => new[] { 4, 5 }.Select(x => z * MapPatchKey.SlotCells + x)).ToArray();
        foreach (int cell in holeCells)
            floor.SetPresent(cell % MapPatchKey.SlotCells - floor.CellMinX, cell / MapPatchKey.SlotCells - floor.CellMinZ, false);
        floor.Records.Add(new MapHorizontalOpening("sinkhole-opening", floor.Key, holeCells));

        MapSurfacePatch anchor = set.Patches[Slot0(SinkFloor)];
        MapRecordRef sea = new("sea", anchor.Key), sink = new("sink", anchor.Key), link = new("sinkhole-link", anchor.Key);
        var openingBound = new MapBoundRef(MapBoundKind.HorizontalOpening, null, new("sinkhole-opening", floor.Key));
        int[] seaCells = Enumerable.Range(0, 4)
            .SelectMany(z => Enumerable.Range(0, 8).Where(x => x is < 4 or > 5).Select(x => z * MapPatchKey.SlotCells + x)).ToArray();
        anchor.Records.Add(Space("sea", MapSpaceKind.Exterior, link));
        anchor.Records.Add(new MapSpaceFootprint("sea-cells", sea, floor.Key, seaCells, Floor(Sinkhole), OpenTop));
        anchor.Records.Add(new MapSpaceFootprint("sea-opening-cells", sea, floor.Key, holeCells, openingBound, OpenTop));
        anchor.Records.Add(Space("sink", MapSpaceKind.Cave, link));
        anchor.Records.Add(new MapSpaceFootprint("sink-cells", sink, anchor.Key, holeCells, Floor(SinkFloor), openingBound));
        CloseWalls(set, "sink", SinkFloor, (id, x0, z0, x1, z1) => Authored(id, SinkFloor, x0, z0, x1, z1, opening), 4, 0, 6, 4);
        anchor.Records.Add(new MapVerticalLink(link.Id, sea, sink, new[] { openingBound.Opening! },
            Array.Empty<MapRecordRef>(), Array.Empty<MapRecordRef>()));

        var water = new MapWaterSet();
        water.Media.Add(new MapWaterMediumDoc(Medium, MapWaterMediumKind.Water, 1f));
        water.Bodies.Add(new MapWaterBodyDoc("ocean", 0f, Medium, Material,
            new MapWaterDomainDoc(Frame, new[] { Rect(0, 0, 8, 4) }, Array.Empty<MapWaterRing>()),
            new[] { sea, sink }));
        return Document(set, water);

        static MapSpaceDoc Space(string id, MapSpaceKind kind, MapRecordRef link) => new(id, kind, null, null,
            Array.Empty<string>(), Array.Empty<MapBoundaryRef>(), Array.Empty<MapBoundaryRef>(), new[] { link });
    }

    /// <summary>An exterior <c>shallows</c> over <c>pond-floor</c> at -1 m for x 0 to 4 and world z -4 to 0, under body
    /// <c>pond</c> at <c>SurfaceY</c> 0. Far away in slot (10, 0) of <c>far-floor</c>, <c>shallows</c> has a second
    /// footprint <c>shallows-far-cells-10-0</c>, and <c>ghost-cells</c> is the footprint of a space <c>ghost</c>
    /// anchored in a patch the document does not hold, so its space record is unavailable. Unvalidated.</summary>
    internal static MapDocument PondWithFarFootprints()
    {
        var set = new MapSurfaceSet();
        Surface(set, "pond-floor", MapSurfaceRole.SupportFloor, Frame, 0, 0, 4, 4, (_, _) => -100);
        Surface(set, "far-floor", MapSurfaceRole.SupportFloor, Frame, 640, 0, 644, 4, (_, _) => -100);
        Footprints(set, "shallows", MapSpaceKind.Exterior, Frame, 0, 0, 4, 4, Floor("pond-floor"), OpenTop, Slot0("pond-floor"));
        FootprintCells(set, "shallows", "shallows-far", 640, 0, 644, 4, Floor("far-floor"), OpenTop, Slot0("pond-floor"));
        var water = new MapWaterSet();
        water.Media.Add(new MapWaterMediumDoc(Medium, MapWaterMediumKind.Water, 1f));
        water.Bodies.Add(Body("pond", 0f, Rect(0, 0, 4, 4), Cover("shallows", "pond-floor")));
        MapDocument doc = Document(set, water);

        var far = new MapPatchKey("far-floor", 10, 0);
        MapSpaceFootprint shallows = doc.Surfaces.Patches[far].Records.OfType<MapSpaceFootprint>().Single();
        doc.Surfaces.Patches[far].Records.Add(shallows with { Id = "ghost-cells", Space = GhostSpace });
        return doc;
    }

    /// <summary>The unavailable space of <see cref="PondWithFarFootprints"/>'s far ghost footprint.</summary>
    internal static MapRecordRef GhostSpace => new("ghost", new MapPatchKey("ghost-floor", 10, 0));

    /// <summary>An exterior <c>straddle</c> whose cells cross the 64-cell slot boundaries in both X and Z on a
    /// negative-Z lattice: x 60 to 68 over rows 62 to 66, then x 60 to 64 and x 66 to 68 over rows 66 to 68. Its
    /// footprints are split per slot and per region. Unvalidated.</summary>
    internal static MapDocument SlotStraddlingSpace()
    {
        var set = new MapSurfaceSet();
        Surface(set, "straddle-floor", MapSurfaceRole.SupportFloor, Frame, 60, 62, 68, 68, (_, _) => -100);
        var anchor = new MapPatchKey("straddle-floor", 0, 0);
        Footprints(set, "straddle", MapSpaceKind.Exterior, Frame, 60, 62, 68, 66, Floor("straddle-floor"), OpenTop, anchor);
        FootprintCells(set, "straddle", "straddle-west", 60, 66, 64, 68, Floor("straddle-floor"), OpenTop, anchor);
        FootprintCells(set, "straddle", "straddle-east", 66, 66, 68, 68, Floor("straddle-floor"), OpenTop, anchor);
        return CaveFixtures.Document(set);
    }

    /// <summary>The record refs of <see cref="SinkholeCave"/>'s spaces, their footprints over the opening, the link
    /// and the opening, plus the patch that owns the opening's geometry.</summary>
    internal static (MapRecordRef Sea, MapRecordRef Sink, MapRecordRef SeaFootprint, MapRecordRef SinkFootprint, MapRecordRef Link,
        MapRecordRef Opening, MapPatchKey OpeningOwner) SinkholeRefs(MapDocument doc)
    {
        MapRecordRef opening = RecordRef(doc, "sinkhole-opening");
        var owner = (MapHorizontalOpening)doc.Surfaces.Patches[opening.Anchor].Records.Single(r => r.Id == opening.Id);
        return (RecordRef(doc, "sea"), RecordRef(doc, "sink"), RecordRef(doc, "sea-opening-cells"), RecordRef(doc, "sink-cells"),
            RecordRef(doc, "sinkhole-link"), opening, owner.Patch);
    }

    /// <summary>True when every ref resolves to a record the view acquired.</summary>
    internal static bool RecordsAcquired(MapScopedSurfaces view, params MapRecordRef[] refs) =>
        refs.All(r => view.Witness.Records.Contains(r) && view.TryRecord(r, out MapTopologyRecord? record, out _) && record is not null);

    /// <summary>Acquires all of <paramref name="doc"/> from its stored copy with every patch of
    /// <paramref name="surfaceId"/> unloaded.</summary>
    internal static MapScopedSurfaces ViewWithUnloaded(MapDocument doc, string surfaceId) => ScopeFixtures.Acquire(
        new FilteredSurfaceSource(StoredSource(doc), doc.Surfaces.Patches.Keys.Where(k => k.SurfaceId == surfaceId).ToArray()),
        ScopeFixtures.Whole(doc));

    /// <summary>Saves <paramref name="doc"/> tiled to a fresh temporary directory and opens its stored source.</summary>
    internal static MapStoredSurfaceSource StoredSource(MapDocument doc)
    {
        string path = System.IO.Path.Combine(TempDirectory(), "tiled");
        MapDocumentFile.SaveTiled(doc, path);
        return MapStoredSurfaceSource.Open(path);
    }

    /// <summary>The resolver-2 options the native resolver tests use.</summary>
    internal static MapResolveOptions ResolveOptions() => new("headless", 1, "options", ResolverVersion: 2);

    static MapRecordRef RecordRef(MapDocument doc, string id) =>
        new(id, doc.Surfaces.Patches.Single(p => p.Value.Records.Any(r => r.Id == id)).Key);
}
