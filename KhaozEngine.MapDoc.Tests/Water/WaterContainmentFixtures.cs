using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Primitives;
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

    /// <summary>Two exterior spaces under body <c>ponds</c> at <c>SurfaceY</c> 0 over x 10 to 14 and world z -14 to
    /// -10: <c>near</c> on <c>near-floor</c> at -1 m there, and <see cref="FarPond"/> on <c>far-floor</c> for x 40 to 44
    /// and world z -4 to 0, anchored there. Both floors hold one patch in slot (0, 0), so a scope over the near pond
    /// meets only that slot and finds the far patch in it but not among its cells, and no record near the first pond
    /// names <c>far-floor</c>. No relation joins the two. Unvalidated.</summary>
    internal static MapDocument PondsApart()
    {
        var set = new MapSurfaceSet();
        Surface(set, "near-floor", MapSurfaceRole.SupportFloor, Frame, 10, 10, 14, 14, (_, _) => -100);
        Surface(set, "far-floor", MapSurfaceRole.SupportFloor, Frame, 40, 0, 44, 4, (_, _) => -100);
        Footprints(set, "near", MapSpaceKind.Exterior, Frame, 10, 10, 14, 14, Floor("near-floor"), OpenTop, Slot0("near-floor"));
        Footprints(set, FarPond.Id, MapSpaceKind.Exterior, Frame, 40, 0, 44, 4, Floor("far-floor"), OpenTop, FarPond.Anchor);
        var water = new MapWaterSet();
        water.Media.Add(new MapWaterMediumDoc(Medium, MapWaterMediumKind.Water, 1f));
        water.Bodies.Add(Body("ponds", 0f, Rect(10, 10, 14, 14), Cover("near", "near-floor")) with
        {
            Covers = new[] { Cover("near", "near-floor"), FarPond },
        });
        return Document(set, water);
    }

    /// <summary>The far space of <see cref="PondsApart"/>.</summary>
    internal static MapRecordRef FarPond => new("far", Slot0("far-floor"));

    /// <summary>Acquires <paramref name="doc"/> from its stored copy over only the near pond of
    /// <see cref="PondsApart"/>, x 10 to 14 and world z -14 to -10, with every role and no filter.</summary>
    internal static MapScopedSurfaces NearPondView(MapDocument doc) => ScopeFixtures.Acquire(StoredSource(doc),
        new MapSurfaceScope(WorldFrame.Origin, new Vector2(10, -14), new Vector2(14, -10), null, null, ScopeFixtures.AllRoles, null,
            new MapQueryLimits()));

    /// <summary>Adds to <paramref name="doc"/>, far away in slot (10, 0) of a new surface <c>stray-floor</c>, an
    /// uncovered space <c>stray</c>, an alias <see cref="StrayAlias"/> of <see cref="StrayOwner"/>, a space anchored in a
    /// patch the document does not hold, and a portal <see cref="StrayPortal"/> from the alias to <c>stray</c>. The
    /// portal carries records only, no geometry. Unvalidated.</summary>
    internal static MapDocument WithStrayRelations(MapDocument doc)
    {
        Surface(doc.Surfaces, StrayPatch.SurfaceId, MapSurfaceRole.SupportFloor, Frame, 640, 0, 644, 4, (_, _) => -100);
        List<MapTopologyRecord> records = doc.Surfaces.Patches[StrayPatch].Records;
        records.Add(new MapSpaceDoc("stray", MapSpaceKind.Exterior, null, null, Array.Empty<string>(),
            Array.Empty<MapBoundaryRef>(), Array.Empty<MapBoundaryRef>(), Array.Empty<MapRecordRef>()));
        records.Add(new MapSpaceDoc(StrayAlias.Id, MapSpaceKind.Exterior, null, StrayOwner, Array.Empty<string>(),
            Array.Empty<MapBoundaryRef>(), new[] { new MapBoundaryRef(StrayPortal, MapSide.Front) }, Array.Empty<MapRecordRef>()));
        records.Add(new MapCavePortal(StrayPortal.Id, StrayAlias, new("stray", StrayPatch), Array.Empty<MapLatticeVertex>(),
            new MapRecordRef("stray-bottom", StrayPatch), null));
        return doc;
    }

    /// <summary>The alias of <see cref="WithStrayRelations"/>, whose owner is unavailable.</summary>
    internal static MapRecordRef StrayAlias => new("stray-alias", StrayPatch);

    /// <summary>The portal of <see cref="WithStrayRelations"/>, from the stray alias to an uncovered space.</summary>
    internal static MapRecordRef StrayPortal => new("stray-portal", StrayPatch);

    /// <summary>The unavailable owner of <see cref="StrayAlias"/>.</summary>
    internal static MapRecordRef StrayOwner => new("stray-owner", new MapPatchKey("ghost-floor", 10, 0));

    static MapPatchKey StrayPatch => new("stray-floor", 10, 0);

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
