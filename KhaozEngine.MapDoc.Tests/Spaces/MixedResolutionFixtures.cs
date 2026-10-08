using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc;

internal static class MixedResolutionFixtures
{
    internal static MapDocument Document(MapSurfaceSet set)
    {
        MapSurfaceSet surfaces = set.Clone();
        var digests = surfaces.Patches.Select(p => new KeyValuePair<MapPatchKey, string>(
            p.Key, MapSurfaceSemantics.PatchDigest(p.Value))).ToArray();
        for (int i = 0; i < surfaces.Refs.Count; i++)
        {
            MapSurfaceRef surface = surfaces.Refs[i];
            surfaces.Refs[i] = surface with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface, digests.Where(p => p.Key.SurfaceId == surface.Id)),
            };
        }
        var document = new MapDocument
        {
            Id = "mixed-resolution-synthetic",
            ResolverIdentity = new(1, 2),
            SupportRecipe = MapSupportRecipe.AuthoredBindingsV2,
            NativeAssets = NativeAssetFixtures.Valid().Roots.ToList(),
            Bounds = new() { MinX = -32128, MinZ = -32128, MaxX = 32128, MaxZ = 32128 },
            // Native bound validation requires playable bounds, so fixture documents can open in the editor and resolver.
            PlayableBounds = new() { MinX = -32128, MinZ = -32128, MaxX = 32128, MaxZ = 32128 },
            TileSize = 64,
            Surfaces = surfaces,
        };
        IReadOnlyList<string> errors = MapDocumentValidator.Validate(document, MapDocRegistry.CreateDefault());
        if (errors.Count != 0)
            throw new InvalidOperationException("mixed-resolution document validation failed: " + string.Join(", ", errors));
        return document;
    }

    internal static (MapScopedSurfaces View, MapSpaceFootprint Footprint) HalfUnderThird(bool far)
    {
        MapSurfaceSet set = HalfThirdSet(far);
        RequireValid(set);
        return ViewWithFootprint(set);
    }

    internal static (MapScopedSurfaces View, MapSpaceFootprint Footprint) SixtyFourthUnderMetre()
    {
        var set = new MapSurfaceSet();
        set.Refs.Add(Surface("fine64", 64, MapSurfaceRole.SupportFloor));
        set.Refs.Add(Surface("metre", 1, MapSurfaceRole.Ceiling));
        MapSurfacePatch floor = Patch(new("fine64", 0, 0), 64, 64, (_, _) => 0);
        MapSurfacePatch ceiling = Patch(new("metre", 0, 0), 1, 1, (_, _) => 300);
        set.Patches.Add(floor.Key, floor);
        set.Patches.Add(ceiling.Key, ceiling);
        Room(floor, "slab64", "slab64-cells", MapSpaceKind.Exterior, ceiling.Key, new[] { 0 }, "fine64", "metre");
        RequireValid(set);
        return ViewWithFootprint(set);
    }

    internal static MapScopedSurfaces RidgeUnderValley(int ridge, int valley)
    {
        MapSurfaceSet set = RidgeValleySet(ridge, valley);
        RequireValid(set);
        return MapScopedSurfaces.CompleteView(Document(set).Surfaces);
    }

    internal static MapScopedSurfaces ClosedRidgeCave(bool subdivided)
    {
        MapSurfaceSet set = RidgeValleySet(1000, 1800);
        MapSurfacePatch floor = set.Patches[new("half", 0, 0)];
        MapSurfacePatch ceiling = set.Patches[new("third", 0, 0)];
        if (subdivided)
        {
            SubdivideBoundary(floor, 3);
            SubdivideBoundary(ceiling, 2);
        }
        CloseRoom(set, floor, ceiling, "box", 2, 3, subdivided ? 3 : 1, subdivided ? 2 : 1);
        RequireValid(set);
        return MapScopedSurfaces.CompleteView(Document(set).Surfaces);
    }

    internal static MapScopedSurfaces FinePeakUnderCoarseRoof(int peakFineUnits)
    {
        var set = new MapSurfaceSet();
        set.Refs.Add(Surface("peak", 3, MapSurfaceRole.SupportFloor));
        set.Refs.Add(Surface("lid", 1, MapSurfaceRole.Ceiling));
        MapSurfacePatch floor = Patch(new("peak", 0, 0), 3, 3,
            (x, z) => x == 1 && z == 1 ? peakFineUnits : 0);
        MapSurfacePatch ceiling = Patch(new("lid", 0, 0), 1, 1, (_, _) => 300);
        SubdivideBoundary(ceiling, 3);
        set.Patches.Add(floor.Key, floor);
        set.Patches.Add(ceiling.Key, ceiling);
        Room(floor, "box", "box-cells", MapSpaceKind.Cave, ceiling.Key, new[] { 0 }, "peak", "lid");
        CloseRoom(set, floor, ceiling, "box", 3, 1, 1, 3);
        RequireValid(set);
        return MapScopedSurfaces.CompleteView(Document(set).Surfaces);
    }

    internal static MapDocument Converted()
    {
        MapSurfaceSet set = MapFinePatchConversion.Convert(ConversionFixtures.WithCaveCeiling(),
            ConversionFixtures.Request(3, false)).Candidate;
        MapSurfacePatch anchor = set.Patches[new("coarse", 0, 0)];
        MapSurfacePatch floor = set.Patches[new("coarse-fine-1", 0, 0)];
        MapSurfacePatch ceiling = set.Patches[new("roof", 0, 0)];
        CloseRoom(set, floor, ceiling, "room", 3, 1, 1, 3, anchor);
        RequireValid(set);
        return Document(set);
    }

    static MapSurfaceSet HalfThirdSet(bool far)
    {
        long s = far ? 1000 : 0, t = far ? 1500 : 0;
        var set = new MapSurfaceSet();
        set.Refs.Add(Surface("half", 2, MapSurfaceRole.SupportFloor));
        set.Refs.Add(Surface("third", 3, MapSurfaceRole.Ceiling));
        MapSurfacePatch floor = Patch(new("half", s, s), 2, 2, (_, _) => 0);
        MapSurfacePatch ceiling = Patch(new("third", t, t), 3, 3, (_, _) => 300);
        set.Patches.Add(floor.Key, floor);
        set.Patches.Add(ceiling.Key, ceiling);
        Room(floor, "box", "box-cells", MapSpaceKind.Exterior, ceiling.Key,
            new[] { 0, 1, 2, 64, 65, 66, 128, 129, 130 }, "half", "third");
        return set;
    }

    static MapSurfaceSet RidgeValleySet(int ridge, int valley)
    {
        MapSurfaceSet set = HalfThirdSet(false);
        MapSurfacePatch floor = set.Patches[new("half", 0, 0)];
        MapSurfacePatch ceiling = set.Patches[new("third", 0, 0)];
        for (int z = 0; z <= floor.Depth; z++)
            floor.Heights[z * (floor.Width + 1) + 1] = ridge;
        for (int z = 0; z <= ceiling.Depth; z++)
            for (int x = 0; x <= ceiling.Width; x++)
                ceiling.Heights[z * (ceiling.Width + 1) + x] = z == 1 ? valley : 2700;
        return set;
    }

    internal static MapSurfaceRef Surface(string id, int denominator, MapSurfaceRole role)
        => new(id, new(new(1, denominator), new(1, 100 * denominator),
            MapRowDirection.PositiveZ, MapHeightDatum.WorldY0), role, MapPresencePolicy.Native, null, null, "");

    internal static MapSurfacePatch Patch(MapPatchKey key, int width, int depth, Func<int, int, int> height)
    {
        var patch = new MapSurfacePatch
        {
            Key = key,
            Width = width,
            Depth = depth,
            Heights = new int[(width + 1) * (depth + 1)],
            Cells = Enumerable.Repeat(new MapSurfaceCell(1, 0, MapOverlayCut.Full, 0,
                MapCellFlags.None, MapCellTopology.Auto), width * depth).ToArray(),
            Presence = new ulong[(width * depth + 63) / 64],
        };
        for (int z = 0; z <= depth; z++)
            for (int x = 0; x <= width; x++)
                patch.Heights[z * (width + 1) + x] = height(x, z);
        for (int z = 0; z < depth; z++)
            for (int x = 0; x < width; x++)
                patch.SetPresent(x, z, true);
        return patch;
    }

    internal static void Room(MapSurfacePatch anchor, string spaceId, string footprintId, MapSpaceKind kind,
        MapPatchKey lattice, IReadOnlyList<int> cells, string lower, string upper)
    {
        anchor.Records.Add(new MapSpaceDoc(spaceId, kind, null, null, Array.Empty<string>(),
            Array.Empty<MapBoundaryRef>(), Array.Empty<MapBoundaryRef>(), Array.Empty<MapRecordRef>()));
        anchor.Records.Add(new MapSpaceFootprint(footprintId, new(spaceId, anchor.Key), lattice, cells,
            new(MapBoundKind.SupportFloor, lower, null), new(MapBoundKind.Ceiling, upper, null)));
    }

    internal static (MapScopedSurfaces View, MapSpaceFootprint Footprint) ViewWithFootprint(MapSurfaceSet set)
    {
        MapScopedSurfaces view = MapScopedSurfaces.CompleteView(Document(set).Surfaces);
        MapSpaceFootprint footprint = set.AllRecords().OfType<MapSpaceFootprint>().Single();
        return (view, footprint);
    }

    static void SubdivideBoundary(MapSurfacePatch patch, int segments)
    {
        for (int x = 0; x < patch.Width; x++)
        {
            patch.EdgeSubdivisions.Add(new(x, 0, MapCellEdge.South, segments));
            patch.EdgeSubdivisions.Add(new(x, patch.Depth - 1, MapCellEdge.North, segments));
        }
        for (int z = 0; z < patch.Depth; z++)
        {
            patch.EdgeSubdivisions.Add(new(0, z, MapCellEdge.West, segments));
            patch.EdgeSubdivisions.Add(new(patch.Width - 1, z, MapCellEdge.East, segments));
        }
    }

    static void CloseRoom(MapSurfaceSet set, MapSurfacePatch floor, MapSurfacePatch ceiling, string spaceId,
        int lowerCells, int upperCells, int lowerSegments, int upperSegments, MapSurfacePatch? anchor = null)
    {
        anchor ??= floor;
        var walls = new List<MapBoundaryRef>();
        foreach (MapCellEdge edge in Enum.GetValues<MapCellEdge>())
        {
            string id = spaceId + "-" + edge;
            MapBoundaryChain lower = EdgeChain(id + "-lower", floor, edge, lowerCells, lowerSegments);
            MapBoundaryChain upper = EdgeChain(id + "-upper", ceiling, edge, upperCells, upperSegments);
            var strip = new MapWallStrip(id + "-wall", new(lower.Id, anchor.Key),
                new(upper.Id, anchor.Key), MapStripFacing.Front, 1);
            anchor.Records.Add(lower);
            anchor.Records.Add(upper);
            anchor.Records.Add(strip);
            walls.Add(new(new(strip.Id, anchor.Key), MapSide.Front));
        }
        int index = anchor.Records.FindIndex(r => r.Id == spaceId);
        var space = (MapSpaceDoc)anchor.Records[index];
        anchor.Records[index] = space with { Kind = MapSpaceKind.Cave, Walls = walls.ToArray() };
    }

    static MapBoundaryChain EdgeChain(string id, MapSurfacePatch patch, MapCellEdge edge, int cells, int segments)
    {
        MapLatticeAddress first = patch.CornerAddress(0, 0);
        long minX = first.X, minZ = first.Z;
        int steps = cells * segments;
        var vertices = new MapChainVertex[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            long x = edge switch
            {
                MapCellEdge.South or MapCellEdge.North => minX * segments + i,
                MapCellEdge.East => (minX + cells) * segments,
                _ => minX * segments,
            };
            long z = edge switch
            {
                MapCellEdge.East or MapCellEdge.West => minZ * segments + i,
                MapCellEdge.North => (minZ + cells) * segments,
                _ => minZ * segments,
            };
            vertices[i] = new(new(patch.Key.SurfaceId, MapLatticeAddress.Create(x, z, segments)), null);
        }
        return new(id, MapChainKind.SurfaceEdge, patch.Key, vertices);
    }

    internal static void RequireValid(MapSurfaceSet set)
    {
        foreach (MapSurfacePatch patch in set.Patches.Values)
        {
            IReadOnlyList<string> local = patch.ValidateLocal();
            if (local.Count != 0)
                throw new InvalidOperationException("mixed-resolution fixture local validation failed: " + string.Join(", ", local));
        }
        IReadOnlyList<string> references = MapTopologyReferenceValidator.Validate(set.Refs, set.Patches.Values.ToArray());
        if (references.Count != 0)
            throw new InvalidOperationException("mixed-resolution fixture reference validation failed: " + string.Join(", ", references));
        foreach (MapSpaceFootprint footprint in set.AllRecords().OfType<MapSpaceFootprint>())
        {
            if (!set.TryGetRecord(footprint.Space, out MapTopologyRecord? record) || record is not MapSpaceDoc space)
                throw new InvalidOperationException("mixed-resolution fixture space is unresolved");
            MapSurfaceRef? lower = set.Refs.SingleOrDefault(s => s.Id == footprint.Lower.SurfaceId);
            if (MapLegacyExteriorRecipe.Check(footprint, space, lower) is { } failure)
                throw new InvalidOperationException("mixed-resolution fixture " + failure);
        }
        if (!set.AllRecords().OfType<MapBoundaryChain>().Any()) return;
        MapScopedSurfaces view = MapScopedSurfaces.CompleteView(set);
        foreach (MapBoundaryChain chain in set.AllRecords().OfType<MapBoundaryChain>())
        {
            MapChainResolution resolution = MapBoundaryGeometry.ResolveChain(chain, view);
            if (resolution.Status != MapResolveStatus.Resolved)
                throw new InvalidOperationException("mixed-resolution fixture chain validation failed: " + resolution.Detail);
        }
    }
}
