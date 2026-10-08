using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Storage;

namespace KhaozEngine.Tests.MapDoc;

internal static class PrecisionFixtures
{
    internal static MapDocument Probe(long slotX, long slotZ, int baseCm)
    {
        var set = new MapSurfaceSet();
        set.Refs.Add(new("probe", new(new(1, 3), new(1, 100), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0),
            MapSurfaceRole.SupportFloor, MapPresencePolicy.Native, null, null, ""));
        MapSurfacePatch patch = MixedResolutionFixtures.Patch(new("probe", slotX, slotZ), 4, 4,
            (x, z) => baseCm + 10 * x + 3 * z);
        AddExterior(patch);
        set.Patches.Add(patch.Key, patch);
        return Document(set);
    }

    internal static MapDocument Deck()
    {
        var set = new MapSurfaceSet();
        set.Refs.Add(MixedResolutionFixtures.Surface("deck", 1, MapSurfaceRole.SupportFloor));
        MapSurfacePatch patch = MixedResolutionFixtures.Patch(new("deck", 0, 0), 2, 2, (_, _) => 0);
        AddExterior(patch);
        set.Patches.Add(patch.Key, patch);
        return Document(set);
    }

    internal static MapCompiledPatch Compile(MapDocument doc)
    {
        MapSurfacePatch patch = doc.Surfaces.Patches.Values.Single();
        return MapSurfaceCompiler.Compile(doc.Surfaces.Refs.Single(s => s.Id == patch.Key.SurfaceId), patch);
    }

    internal static MapExactValue ExactQueryHeight(MapDocument doc, WorldFrame frame, Vector3 local)
    {
        MapExactXz world = new MapFramePoint(frame, local).ExactWorldXz();
        MapSurfacePatch patch = doc.Surfaces.Patches.Values.Single();
        MapSurfaceRef surface = doc.Surfaces.Refs.Single(s => s.Id == patch.Key.SurfaceId);
        return MapSurfaceCompiler.ExactHeight(surface, patch, world.X, world.Z)
            ?? throw new InvalidOperationException("precision fixture point has no canonical height");
    }

    internal static IReadOnlyList<(double X, double Y, double Z)> ReferenceInFrame(
        MapCompiledPatch deck, MapTransform placement, WorldFrame frame)
    {
        double x = MapExactValue.FromSingle(placement.Position.X).ToDouble() - (long)frame.X * (long)WorldFrame.Grid;
        double y = MapExactValue.FromSingle(placement.Position.Y).ToDouble();
        double z = MapExactValue.FromSingle(placement.Position.Z).ToDouble() - (long)frame.Z * (long)WorldFrame.Grid;
        double yaw = MapExactValue.FromSingle(placement.YawRadians).ToDouble();
        double scale = MapExactValue.FromSingle(placement.Scale).ToDouble();
        double c = Math.Cos(yaw), s = Math.Sin(yaw);
        return deck.ExactVertices.Select(vertex =>
        {
            double localX = vertex.X.ToDouble() * scale, localY = vertex.Y.ToDouble() * scale,
                localZ = vertex.Z.ToDouble() * scale;
            return (X: x + c * localX + s * localZ, Y: y + localY, Z: z - s * localX + c * localZ);
        }).ToArray();
    }

    internal static MapSupportResult Select(MapDocument doc, WorldFrame frame, Vector3 local, float up, float down)
    {
        MapScopedSurfaces scoped = ScopeFixtures.Acquire(MapDocumentSurfaceSource.Capture(doc),
            ScopeFixtures.Around(frame, local.X, local.Z, half: 2));
        if (scoped.Status != MapAcquireStatus.Complete)
            throw new InvalidOperationException("precision fixture acquisition failed: " + scoped.Detail);
        return new MapSupportQuery(scoped).Select(new(new MapFramePoint(frame, local), null, null, null, up, down, null));
    }

    internal static MapCompiledStrip Strip(long slot)
    {
        var set = new MapSurfaceSet();
        CaveFixtures.AddSurface(set, "strip", MapSurfaceRole.SupportFloor, 0, 0, 2, 1, (_, _) => 0);
        MapSurfacePatch patch = CaveFixtures.Patch(set, "strip");
        set.Patches.Remove(patch.Key);
        patch.Key = new("strip", slot, slot);
        set.Patches.Add(patch.Key, patch);
        set.Refs[0] = set.Refs[0] with
        {
            Frame = new(new(1, 3), new(1, 100), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0),
        };
        AddExterior(patch);
        MapBoundaryChain lower = Chain("strip-lower", new[] { 0, 100, 50 });
        MapBoundaryChain upper = Chain("strip-upper", new[] { 200, 400, 350 });
        var strip = new MapWallStrip("wall", new(lower.Id, patch.Key), new(upper.Id, patch.Key), MapStripFacing.TwoSided, 1);
        patch.Records.Add(lower);
        patch.Records.Add(upper);
        patch.Records.Add(strip);
        MapDocument doc = Document(set);
        MapScopedSurfaces view = CaveFixtures.View(doc);
        return MapWallStripCompiler.Compile(strip, MapBoundaryGeometry.ResolveChain(lower, view),
            MapBoundaryGeometry.ResolveChain(upper, view));

        MapBoundaryChain Chain(string id, int[] heights) => new(id, MapChainKind.Authored, null,
            heights.Select((height, x) => new MapChainVertex(new("strip", patch.CornerAddress(x, 0)), height)).ToArray());
    }

    static void AddExterior(MapSurfacePatch patch)
    {
        patch.Records.Add(new MapSpaceDoc("air", MapSpaceKind.Exterior, null, null, Array.Empty<string>(),
            Array.Empty<MapBoundaryRef>(), Array.Empty<MapBoundaryRef>(), Array.Empty<MapRecordRef>()));
        int[] cells = Enumerable.Range(0, patch.Depth).SelectMany(z => Enumerable.Range(0, patch.Width)
            .Select(x => (z + patch.CellMinZ) * MapPatchKey.SlotCells + x + patch.CellMinX)).ToArray();
        patch.Records.Add(new MapSpaceFootprint("air-cells", new("air", patch.Key), patch.Key, cells,
            CaveFixtures.Floor(patch.Key.SurfaceId), CaveFixtures.OpenTop));
    }

    static MapDocument Document(MapSurfaceSet set)
    {
        MapDocument doc = CaveFixtures.Document(set);
        doc.PlayableBounds = new()
        {
            MinX = doc.Bounds.MinX,
            MinZ = doc.Bounds.MinZ,
            MaxX = doc.Bounds.MaxX,
            MaxZ = doc.Bounds.MaxZ,
        };
        MapBoundDocumentValidation.Validate(doc, FormatFourFixtures.Assets());
        IReadOnlyList<string> findings = MapSpaceCoverageValidator.Validate(CaveFixtures.View(doc));
        if (findings.Count != 0)
            throw new InvalidOperationException("precision fixture coverage validation failed: " + string.Join(", ", findings));
        return doc;
    }
}
