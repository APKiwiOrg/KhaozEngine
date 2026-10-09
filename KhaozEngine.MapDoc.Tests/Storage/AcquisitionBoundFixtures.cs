using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.MapDoc.Storage;

internal static class AcquisitionBoundFixtures
{
    internal static MapSurfaceScope TouchScope => ScopeFixtures.Around(WorldFrame.Origin, 31.8f, 0.2f, 0.1f)
        with
    { Roles = new[] { MapSurfaceRole.Ceiling } };
    internal static MapSurfaceScope MicroScope => ScopeFixtures.Around(WorldFrame.Origin, 0.5f, 0.5f, 0.25f)
        with
    { Roles = new[] { MapSurfaceRole.Ceiling } };
    internal static MapSurfaceScope AcquiredScope => ScopeFixtures.Around(WorldFrame.Origin, 0.5f, 0.5f, 0.25f,
        new MapQueryLimits(MaxCandidatePatches: 2));
    internal static MapSurfaceScope AdversarialScope => ScopeFixtures.Around(WorldFrame.Origin, 5.5f, 0.5f, 0.25f)
        with
    { Roles = new[] { MapSurfaceRole.Ceiling } };

    internal static MapDocument BoundaryTouch()
    {
        MapDocument doc = Document();
        Surface(doc, "half", MapSurfaceRole.SupportFloor, new(1, 2), new(1, 200));
        Surface(doc, "third", MapSurfaceRole.Ceiling, new(1, 3), new(1, 300));
        Add(doc, Patch(new("half", 0, 0), 0, 60, 0, 4, 4));
        Add(doc, Patch(new("half", 1, 0), 0, 0, 0, 4, 4));
        MapSurfacePatch top = Patch(new("third", 1, 0), 900, 30, 0, 2, 1);
        Room(top, "ledge", "ledge-cells", 31, "half", "third");
        Add(doc, top);
        return doc;
    }

    internal static MapDocument MicroBound() => Pair("unit", new(1, 1), "micro", new(1, 1_000_000), "slab", "slab-cells", 0, 1, 4);
    internal static MapDocument AcquiredBounds() => Pair("top", new(1, 1), "low", new(1, 1), "porch", "porch-cells", 0, 1, 1);
    internal static MapDocument AdversarialUnits() => Pair("odd", new(int.MaxValue, int.MaxValue - 1), "fine", new(1, int.MaxValue), "strange", "strange-cells", 5, 8, 1);

    static MapDocument Pair(string topId, MapRational topUnit, string lowId, MapRational lowUnit,
        string roomId, string footprintId, int cell, int width, int lowWidth)
    {
        MapDocument doc = Document();
        Surface(doc, topId, MapSurfaceRole.Ceiling, topUnit);
        Surface(doc, lowId, MapSurfaceRole.SupportFloor, lowUnit);
        MapSurfacePatch top = Patch(new(topId, 0, 0), 300, 0, 0, width, 1);
        Room(top, roomId, footprintId, cell, lowId, topId);
        Add(doc, top);
        Add(doc, Patch(new(lowId, 0, 0), 0, 0, 0, lowWidth, lowWidth));
        return doc;
    }

    internal static MapDocument Document()
    {
        MapDocument doc = SurfaceStorageFixtures.FlatPatches(0);
        doc.Surfaces.Refs.Clear();
        return doc;
    }
    internal static void Surface(MapDocument doc, string id, MapSurfaceRole role, MapRational? cell = null, MapRational? height = null) =>
        doc.Surfaces.Refs.Add(new(id, new(cell ?? new(1, 1), height ?? new(1, 100), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0),
            role, MapPresencePolicy.Native, null, null, new string('0', 64)));
    internal static void Add(MapDocument doc, MapSurfacePatch patch) => doc.Surfaces.Patches.Add(patch.Key, patch);
    internal static MapSpaceDoc Space(string id) => new(id, MapSpaceKind.Exterior, null, null,
        Array.Empty<string>(), Array.Empty<MapBoundaryRef>(), Array.Empty<MapBoundaryRef>(), Array.Empty<MapRecordRef>());
    internal static void Room(MapSurfacePatch patch, string roomId, string footprintId, int cell, string lower, string upper)
    {
        patch.Records.Add(Space(roomId));
        patch.Records.Add(new MapSpaceFootprint(footprintId, new(roomId, patch.Key), patch.Key, new[] { cell },
            new(MapBoundKind.SupportFloor, lower, null), new(MapBoundKind.Ceiling, upper, null)));
    }
    internal static MapSurfacePatch Patch(MapPatchKey key, int height, int minX = 0, int minZ = 0, int width = 1, int depth = 1)
    {
        int count = width * depth;
        var patch = new MapSurfacePatch
        {
            Key = key,
            CellMinX = minX,
            CellMinZ = minZ,
            Width = width,
            Depth = depth,
            Heights = Enumerable.Repeat(height, (width + 1) * (depth + 1)).ToArray(),
            Cells = Enumerable.Repeat(new MapSurfaceCell(1, 0, MapOverlayCut.Full, 0, MapCellFlags.None, MapCellTopology.Auto), count).ToArray(),
            Presence = Enumerable.Repeat(ulong.MaxValue, (count + 63) / 64).ToArray(),
        };
        if (count % 64 != 0) patch.Presence[^1] = (1UL << (count % 64)) - 1;
        return patch;
    }
}
