using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Storage;

namespace KhaozEngine.Tests.MapDoc;

internal static class SupportFixtures
{
    internal static readonly MapResolveOptions V2 = new("headless", 1, "options", ResolverVersion: 2);
    static readonly string[] EqualSlabFindings =
    {
        "coincident: footprint 'slab-a-cells' cell 0 with 'slab-b-cells' cell 0",
    };

    internal static MapDocument EightStacked()
    {
        var set = new MapSurfaceSet();
        for (int k = 0; k < 8; k++)
        {
            string floorId = "floor-" + k, ceilingId = "ceiling-" + k, spaceId = "level-" + k;
            CaveFixtures.AddSurface(set, floorId, MapSurfaceRole.SupportFloor, 0, 0, 2, 2, (_, _) => k * 1000);
            CaveFixtures.AddSurface(set, ceilingId, MapSurfaceRole.Ceiling, 0, 0, 2, 2, (_, _) => k * 1000 + 500);
            MapSurfacePatch floor = CaveFixtures.Patch(set, floorId);
            string wallId = spaceId + "-wall";
            MapBoundaryChain lower = PerimeterChain(wallId + "-lower", floorId);
            MapBoundaryChain upper = PerimeterChain(wallId + "-upper", ceilingId);
            floor.Records.Add(lower);
            floor.Records.Add(upper);
            floor.Records.Add(new MapWallStrip(wallId, new(lower.Id, floor.Key), new(upper.Id, floor.Key), MapStripFacing.Front, 1));
            floor.Records.Add(Space(spaceId, MapSpaceKind.Cave, new[] { new MapBoundaryRef(new(wallId, floor.Key), MapSide.Front) }));
            floor.Records.Add(new MapSpaceFootprint(spaceId + "-cells", new(spaceId, floor.Key), floor.Key,
                new[] { 0, 1, 64, 65 }, CaveFixtures.Floor(floorId), CaveFixtures.Ceiling(ceilingId)));
        }
        // One closed perimeter strip per level keeps all 40 records within the default 64-read acquisition budget.
        return ValidatedDocument(CaveFixtures.Document(set), Array.Empty<string>());
    }

    internal static MapDocument EqualSlabs(bool secondIsPaint)
    {
        var set = new MapSurfaceSet();
        CaveFixtures.AddSurface(set, "slab-a", MapSurfaceRole.SupportFloor, 0, 0, 1, 1, (_, _) => 0);
        CaveFixtures.AddSurface(set, "slab-b", secondIsPaint ? MapSurfaceRole.PaintOverride : MapSurfaceRole.SupportFloor,
            0, 0, 1, 1, (_, _) => 0);
        if (secondIsPaint)
        {
            int index = set.Refs.FindIndex(s => s.Id == "slab-b");
            set.Refs[index] = set.Refs[index] with { PaintTargetSurfaceId = "slab-a" };
        }
        MapSurfacePatch first = CaveFixtures.Patch(set, "slab-a");
        first.Records.Add(Space("room", MapSpaceKind.Exterior, Array.Empty<MapBoundaryRef>()));
        foreach (string id in secondIsPaint ? new[] { "slab-a" } : new[] { "slab-a", "slab-b" })
        {
            MapSurfacePatch slab = CaveFixtures.Patch(set, id);
            slab.Records.Add(new MapSpaceFootprint(id + "-cells", new("room", first.Key), slab.Key,
                new[] { 0 }, CaveFixtures.Floor(id), CaveFixtures.OpenTop));
        }
        return ValidatedDocument(CaveFixtures.Document(set), secondIsPaint ? Array.Empty<string>() : EqualSlabFindings);
    }

    internal static MapDocument Step()
    {
        var set = new MapSurfaceSet();
        CaveFixtures.AddSurface(set, "low", MapSurfaceRole.SupportFloor, 0, 0, 1, 1, (_, _) => 0);
        CaveFixtures.AddSurface(set, "step", MapSurfaceRole.SupportFloor, 1, 0, 1, 1, (_, _) => 30);
        MapSurfacePatch low = CaveFixtures.Patch(set, "low"), step = CaveFixtures.Patch(set, "step");
        MapBoundaryChain lower = CaveFixtures.Edge("step-riser-lower", "low", 1, 0, 1, 1);
        MapBoundaryChain upper = CaveFixtures.Edge("step-riser-upper", "step", 1, 0, 1, 1);
        low.Records.Add(lower);
        low.Records.Add(upper);
        low.Records.Add(new MapWallStrip("step-riser", new(lower.Id, low.Key), new(upper.Id, low.Key), MapStripFacing.Front, 1));
        low.Records.Add(Space("yard", MapSpaceKind.Exterior, new[] { new MapBoundaryRef(new("step-riser", low.Key), MapSide.Front) }));
        low.Records.Add(new MapSpaceFootprint("low-cells", new("yard", low.Key), low.Key,
            new[] { 0 }, CaveFixtures.Floor("low"), CaveFixtures.OpenTop));
        step.Records.Add(new MapSpaceFootprint("step-cells", new("yard", low.Key), step.Key,
            new[] { 1 }, CaveFixtures.Floor("step"), CaveFixtures.OpenTop));
        return ValidatedDocument(CaveFixtures.Document(set), Array.Empty<string>());
    }

    internal static MapDocument LegacyFallback()
        => ValidatedDocument(LegacyExteriorFixtures.Row("tagged"), Array.Empty<string>());

    internal static MapSupportRequest Request(float x, float y, float z, float up, float down)
        => new(new MapFramePoint(WorldFrame.Origin, new(x, y, z)), null, null, null, up, down, null);

    internal static MapScopedSurfaces Acquire(MapDocument doc, MapFramePoint point, MapQueryLimits? limits = null)
        => ScopeFixtures.Acquire(MapDocumentSurfaceSource.Capture(doc),
            ScopeFixtures.Around(point.Frame, point.Local.X, point.Local.Z, half: 2, limits: limits));

    internal static MapSupportResult Select(MapDocument doc, MapSupportRequest request, MapQueryLimits? limits = null)
        => new MapSupportQuery(Acquire(doc, request.Point, limits)).Select(request);

    internal static MapFaceKey FirstFace(MapDocument doc, string surfaceId)
        => CaveFixtures.Compile(doc, surfaceId).Faces[0].Key;

    internal static MapAssetClosure Assets() => FormatFourFixtures.Assets();

    internal static MapDocument CaveWithPlacements()
    {
        MapDocument document = CaveFixtures.RampChamberShaft();
        document.PlayableBounds = new() { MinX = 0, MinZ = 0, MaxX = 8, MaxZ = 8 };
        document.Placements.AddRange(new[]
        {
            new MapPlacement { Id = "p-explicit", Kind = "scenery", AssetId = "tree", X = 1, Y = 12.25f, Z = 1 },
            new MapPlacement
            {
                Id = "p-surface", Kind = "scenery", AssetId = "tree", X = 3.5f, Z = 4.5f,
                SupportBinding = new(MapSupportBindingKind.Surface, "cave-floor", null, null, null, null),
            },
            new MapPlacement
            {
                Id = "p-space", Kind = "scenery", AssetId = "tree", X = 2.5f, Z = 6.5f,
                SupportBinding = new(MapSupportBindingKind.Space, null, "chamber", 4.2f, 1f, 1f),
            },
        });
        return ValidatedNativeDocument(document, Array.Empty<string>());
    }

    internal static MapDocument EqualSlabsWithBinding()
    {
        MapDocument document = EqualSlabs(secondIsPaint: false);
        document.PlayableBounds = new() { MinX = 0, MinZ = 0, MaxX = 1, MaxZ = 1 };
        document.Placements.Add(new MapPlacement
        {
            Id = "p-amb", Kind = "scenery", AssetId = "tree", X = 0.5f, Z = 0.5f,
            SupportBinding = new(MapSupportBindingKind.Space, null, "room", 0.2f, 1f, 1f),
        });
        return ValidatedNativeDocument(document, EqualSlabFindings);
    }

    // An unambiguous space binding, so only the missing bound can refuse it.
    internal static MapDocument CaveWithPlacementsWithoutBoundedSearch()
    {
        MapDocument document = CaveWithPlacements();
        MapPlacement placement = document.Placements.Single(p => p.Id == "p-space");
        placement.SupportBinding = placement.SupportBinding! with { SearchAbove = null };
        return ValidatedNativeDocument(document, Array.Empty<string>());
    }

    static MapSpaceDoc Space(string id, MapSpaceKind kind, IReadOnlyList<MapBoundaryRef> walls)
        => new(id, kind, null, null, Array.Empty<string>(), walls, Array.Empty<MapBoundaryRef>(), Array.Empty<MapRecordRef>());

    static MapBoundaryChain PerimeterChain(string id, string surfaceId)
    {
        var perimeter = new[] { (0, 0), (0, 1), (0, 2), (1, 2), (2, 2), (2, 1), (2, 0), (1, 0), (0, 0) };
        return new(id, MapChainKind.SurfaceEdge, CaveFixtures.Key(surfaceId),
            perimeter.Select(p => new MapChainVertex(CaveFixtures.Vertex(surfaceId, p.Item1, p.Item2), null)).ToArray());
    }

    static MapDocument ValidatedDocument(MapDocument document, IReadOnlyList<string> expectedFindings)
    {
        RequireEmpty(MapDocumentValidator.Validate(document, MapDocRegistry.CreateDefault()), "document validation");
        IReadOnlyList<string> findings = MapSpaceCoverageValidator.Validate(CaveFixtures.View(document));
        if (!findings.SequenceEqual(expectedFindings, StringComparer.Ordinal))
            throw new InvalidOperationException("support fixture coverage validation differs: " + string.Join(", ", findings));
        return document;
    }

    static MapDocument ValidatedNativeDocument(MapDocument document, IReadOnlyList<string> expectedFindings)
    {
        _ = ValidatedDocument(document, expectedFindings);
        MapBoundDocumentValidation.Validate(document, Assets());
        return document;
    }

    static void RequireEmpty(IReadOnlyList<string> errors, string operation)
    {
        if (errors.Count != 0)
            throw new InvalidOperationException("support fixture " + operation + " failed: " + string.Join(", ", errors));
    }
}
