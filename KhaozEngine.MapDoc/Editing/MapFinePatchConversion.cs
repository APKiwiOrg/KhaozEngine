using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Editing;

/// <summary>Creates an isolated candidate with exact refinement or an explicit authored replacement.</summary>
public static class MapFinePatchConversion
{
    public static MapConversionResult Convert(MapSurfaceSet surfaces, MapFinePatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        ArgumentNullException.ThrowIfNull(request);
        try { return ConvertCore(surfaces, request); }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException)
        {
            throw new MapDocumentException(MapDocumentRefusal.GeometryNotRepresentable, "fine-patch conversion is not representable", error)
            { SurfaceId = request.SourceSurfaceId, OtherSurfaceId = request.FineSurfaceId, Operation = nameof(Convert) };
        }
    }

    static MapConversionResult ConvertCore(MapSurfaceSet surfaces, MapFinePatchRequest request)
    {
        if (request.Subdivision is < 2 or > 64) throw new MapDocumentException(MapDocumentRefusal.ValueOutsideRange, "subdivision must be 2 to 64")
        { FieldPath = "Subdivision", ActualInteger = request.Subdivision, MinimumInteger = 2, MaximumInteger = 64 };
        if (string.IsNullOrWhiteSpace(request.SourceSurfaceId) || string.IsNullOrWhiteSpace(request.FineSurfaceId) ||
            request.SourceSurfaceId == request.FineSurfaceId)
        {
            bool missingSource = string.IsNullOrWhiteSpace(request.SourceSurfaceId);
            bool missingFine = string.IsNullOrWhiteSpace(request.FineSurfaceId);
            throw new MapDocumentException(missingSource || missingFine ? MapDocumentRefusal.SchemaValueRequired
                : MapDocumentRefusal.AuthoredValueMismatch, "distinct surface ids are required")
            {
                FieldPath = missingSource ? "SourceSurfaceId" : "FineSurfaceId",
                SurfaceId = request.SourceSurfaceId,
                OtherSurfaceId = request.FineSurfaceId
            };
        }
        ArgumentNullException.ThrowIfNull(request.Regions);
        int regionCount = request.Regions.Count;
        if (regionCount == 0) throw new MapDocumentException(MapDocumentRefusal.TerrainConversionRegionsMissing, "conversion regions are required")
        { FieldPath = "Regions", ActualCount = regionCount };
        for (int i = 0; i < request.Regions.Count; i++)
        {
            MapCellRect region = request.Regions[i];
            if (region.MinX >= region.MaxXExclusive || region.MinZ >= region.MaxZExclusive)
                throw new MapDocumentException(MapDocumentRefusal.BoundsInvalid, "conversion regions must be nonempty")
                { FieldPath = "Regions", Index = i };
            for (int j = 0; j < i; j++)
                if (region.Overlaps(request.Regions[j])) throw new MapDocumentException(MapDocumentRefusal.TerrainConversionRegionsOverlap, "conversion regions must be disjoint")
                { FieldPath = "Regions", Index = i, OtherIndex = j };
        }
        MapSurfaceSet candidate = surfaces.Clone();
        RequireReferences(candidate);
        MapScopedSurfaces originalView = MapScopedSurfaces.CompleteView(candidate);
        foreach (MapSurfacePatch patch in candidate.Patches.Values.Where(p => p.CornerDependencies.Count != 0))
            RequireValid(MapSeamValidator.ValidateCornerDependencyFindings(patch, originalView));
        MapSurfaceRef source = candidate.Refs.SingleOrDefault(s => s.Id == request.SourceSurfaceId)
            ?? throw new MapDocumentException(MapDocumentRefusal.ReferenceMissing, $"missing source surface '{request.SourceSurfaceId}'")
            { SurfaceId = request.SourceSurfaceId, FieldPath = "SourceSurfaceId" };
        if (source.Role == MapSurfaceRole.PaintOverride) throw new MapDocumentException(MapDocumentRefusal.SurfacePaintPhysicalCompileUnsupported, "conversion requires a physical surface")
        { SurfaceId = source.Id, FieldPath = "Role" };
        if (candidate.Refs.Any(s => s.Id == request.FineSurfaceId))
            throw new MapDocumentException(MapDocumentRefusal.IdentityDuplicate, $"fine surface '{request.FineSurfaceId}' already exists")
            { SurfaceId = request.FineSurfaceId, SubjectKind = "surface" };
        var fine = source with
        {
            Id = request.FineSurfaceId,
            PresencePolicy = MapPresencePolicy.Native,
            SemanticSha256 = "",
            IndoorSpan = source.IndoorSpan is { } span ? span with
            {
                LowerOffsetUnits = ScaleHeight(span.LowerOffsetUnits, request.Subdivision),
                UpperOffsetUnits = ScaleHeight(span.UpperOffsetUnits, request.Subdivision),
            } : null,
            Frame = source.Frame with
            {
                CellUnitMetres = DivideUnit(source.Frame.CellUnitMetres, request.Subdivision),
                HeightUnitMetres = DivideUnit(source.Frame.HeightUnitMetres, request.Subdivision),
            },
        };
        var cells = new List<MapConversionCellGeometry>();
        foreach (MapCellRect region in request.Regions)
            for (long z = region.MinZ; z < region.MaxZExclusive; z++)
                for (long x = region.MinX; x < region.MaxXExclusive; x++)
                {
                    if (!TryCell(candidate, source.Id, x, z, out MapSurfacePatch? patch, out _, out _))
                        throw new MapDocumentException(MapDocumentRefusal.TerrainCellMissing, $"missing source cell ({x}, {z})")
                        { SurfaceId = source.Id, Address = MapLatticeAddress.Corner(x, z) };
                    cells.Add(new(source, patch!, x, z));
                }
        cells.Sort((a, b) => a.CellZ != b.CellZ ? a.CellZ.CompareTo(b.CellZ) : a.CellX.CompareTo(b.CellX));
        MapCellConversion[] classifications = cells.Select(c => MapConversionClassifier.Classify(c, request.Subdivision)).ToArray();
        IReadOnlyList<MapFootprintRetarget> footprints = MapConversionFootprints.Plan(candidate, source, request);
        RefuseBoundaryRecords(candidate, source.Id, request.Regions);
        if (!request.AcceptAuthoredDifferences)
        {
            MapCellConversion[] refused = classifications.Where(c =>
                c.Class is MapCellConversionClass.UnsupportedEncoding or MapCellConversionClass.NotRepresentable ||
                source.PresencePolicy == MapPresencePolicy.LegacyTileWorld).ToArray();
            if (refused.Length != 0)
            {
                MapDocumentFinding[] findings = refused.Select(c => c.Finding!).ToArray();
                throw MapDocumentRefusalFactory.FromFinding(findings[0], "authored difference: " + string.Join(", ",
                    refused.Select(c => $"cell ({c.CellX}, {c.CellZ}) ({c.Reason ?? "LegacyTileWorld arithmetic policy"})")), findings: findings);
            }
        }
        IReadOnlyList<MapSurfacePatch> patches = MapConversionFinePatches.Build(cells, classifications, request);
        IReadOnlyList<MapCornerOwnerChange> retargets = MapConversionOwners.Plan(candidate, source, fine, cells, patches, request);

        // Classification, record, footprint and existing owner checks precede the first candidate mutation.
        var writes = new SortedSet<MapPatchKey>();
        var records = new SortedSet<string>(StringComparer.Ordinal);
        var spaces = new SortedSet<string>(StringComparer.Ordinal);
        var owners = new List<MapCornerOwnerChange>();
        candidate.Refs.Add(fine);
        foreach (MapSurfacePatch patch in patches)
        {
            candidate.Patches.Add(patch.Key, patch);
            writes.Add(patch.Key);
        }
        foreach (MapConversionCellGeometry cell in cells)
        {
            cell.Patch.SetPresent(cell.X, cell.Z, false);
            cell.Patch.EdgeSubdivisions.RemoveAll(e => e.CellX == cell.X && e.CellZ == cell.Z);
            writes.Add(cell.Patch.Key);
        }
        IReadOnlyList<(long X, long Z)> rim = MapConversionRim.Apply(candidate, source, cells, request, writes, records, owners);
        foreach (MapCornerOwnerChange change in retargets)
        {
            MapSurfacePatch dependent = candidate.Patches[change.Dependent];
            int index = dependent.CornerDependencies.FindIndex(d => d.CornerX == change.CornerX && d.CornerZ == change.CornerZ);
            dependent.CornerDependencies[index] = new(change.CornerX, change.CornerZ, change.NewOwner);
            writes.Add(dependent.Key);
            owners.Add(change);
        }
        foreach (MapFootprintRetarget plan in footprints)
        {
            MapSurfacePatch anchor = candidate.Patches[plan.Anchor];
            int index = anchor.Records.FindIndex(r => r.Id == plan.Original.Id);
            anchor.Records[index] = plan.Remaining;
            records.Add(plan.Remaining.Id);
            if (plan.Added is { } added) { anchor.Records.Add(added); records.Add(added.Id); }
            spaces.Add(plan.Original.Space.Id);
            writes.Add(anchor.Key);
        }
        RequireReferences(candidate);
        MapScopedSurfaces view = MapScopedSurfaces.CompleteView(candidate);
        foreach (MapSurfaceSeam seam in candidate.AllRecords().OfType<MapSurfaceSeam>().Where(s => records.Contains(s.Id)))
            RequireValid(MapSeamValidator.ValidateFindings(seam, view));
        foreach (MapSurfacePatch patch in candidate.Patches.Values.Where(p => p.CornerDependencies.Count != 0))
            RequireValid(MapSeamValidator.ValidateCornerDependencyFindings(patch, view));
        foreach (MapSurfacePatch patch in patches)
        {
            _ = MapSurfacePatchCodec.Encode(patch, out var encoding);
            using var consumedEncoding = encoding;
            var compiled = MapSurfaceCompiler.Compile(fine, patch);
            compiled.PreparationOwnership?.Dispose();
        }
        IReadOnlyList<MapAuthoredDifference> differences = MapConversionDifferences.Collect(surfaces, candidate,
            source, fine, cells, classifications, rim, request.Subdivision);
        ushort[] materials = patches.SelectMany(p => p.Cells).SelectMany(c => new[] { c.Underlay, c.Overlay })
            .Where(m => m != 0).Distinct().Order().ToArray();
        string[] surfaceIds = writes.Select(p => p.SurfaceId).Append(source.Id).Append(fine.Id)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var writeSet = new MapNativeWriteSet(Array.AsReadOnly(writes.ToArray()), Array.AsReadOnly(surfaceIds),
            Array.AsReadOnly(records.ToArray()), Array.AsReadOnly(spaces.ToArray()), owners.AsReadOnly(),
            Array.AsReadOnly(materials), false);
        return new(candidate, Array.AsReadOnly(classifications), differences, writeSet);
    }

    static MapRational DivideUnit(MapRational unit, int k)
    {
        MapExactValue value = unit.Exact().Divide(new(k, 1));
        if (value.Numerator > int.MaxValue || value.Denominator > int.MaxValue)
            throw new MapDocumentException(MapDocumentRefusal.GeometryNotRepresentable, "fine lattice unit is not representable")
            { Numerator = unit.Numerator, Denominator = unit.Denominator, ActualInteger = k, Operation = nameof(DivideUnit) };
        return new((int)value.Numerator, (int)value.Denominator);
    }

    static int ScaleHeight(int height, int k)
    {
        long value = (long)height * k;
        if (value < int.MinValue || value > int.MaxValue) throw new MapDocumentException(MapDocumentRefusal.GeometryNotRepresentable, "fine height offset overflow")
        { ActualInteger = value, MinimumInteger = int.MinValue, MaximumInteger = int.MaxValue, Operation = nameof(ScaleHeight) };
        return (int)value;
    }

    internal static bool TryCell(MapSurfaceSet surfaces, string id, long x, long z,
        out MapSurfacePatch? patch, out int localX, out int localZ)
    {
        MapPatchKey key = MapPatchKey.ForCell(id, x, z);
        localX = localZ = 0;
        if (!surfaces.Patches.TryGetValue(key, out patch)) return false;
        long px = checked(x - key.SlotX * 64 - patch.CellMinX), pz = checked(z - key.SlotZ * 64 - patch.CellMinZ);
        if (px < 0 || px >= patch.Width || pz < 0 || pz >= patch.Depth) return false;
        localX = (int)px;
        localZ = (int)pz;
        return true;
    }

    static void RequireReferences(MapSurfaceSet surfaces) =>
        RequireValid(MapTopologyReferenceValidator.ValidateFindings(surfaces.Refs, surfaces.Patches.Values));

    static void RequireValid(IReadOnlyList<MapDocumentFinding> findings)
    {
        if (findings.Count != 0) throw MapDocumentRefusalFactory.FromFinding(findings[0], findings[0].Message, findings: findings);
    }

    static void RefuseBoundaryRecords(MapSurfaceSet surfaces, string sourceId, IReadOnlyList<MapCellRect> regions)
    {
        foreach (MapTopologyRecord record in surfaces.AllRecords().OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            bool intersects = record switch
            {
                MapBoundaryChain chain when chain.Kind == MapChainKind.SurfaceEdge && chain.SourcePatch?.SurfaceId == sourceId =>
                    Segments(chain.Vertices.Select(v => v.Vertex).ToArray()),
                MapCavePortal portal => Segments(portal.Interval),
                MapSurfaceSeam seam => Segments(new[] { seam.First.From, seam.First.To }) || Segments(new[] { seam.Second.From, seam.Second.To }),
                MapHorizontalOpening opening when opening.Patch.SurfaceId == sourceId => opening.SlotCells.Any(cell =>
                    regions.Any(r => OpeningTouches(opening.Patch, cell, r))),
                _ => false,
            };
            if (intersects) throw new MapDocumentException(MapDocumentRefusal.TerrainBoundaryRecordIntersects, $"conversion region carries boundary record '{record.Id}'")
            { RecordId = record.Id, SurfaceId = sourceId };
        }

        bool Segments(IReadOnlyList<MapLatticeVertex> vertices)
        {
            for (int i = 1; i < vertices.Count; i++)
                if (vertices[i - 1].SurfaceId == sourceId && vertices[i].SurfaceId == sourceId &&
                    regions.Any(r => SegmentTouches(vertices[i - 1].Address, vertices[i].Address, r))) return true;
            return false;
        }
    }

    static bool OpeningTouches(MapPatchKey patch, int cell, MapCellRect region)
    {
        long x = checked(patch.SlotX * 64 + cell % 64), z = checked(patch.SlotZ * 64 + cell / 64);
        long minX = Math.Max(x, region.MinX), maxX = Math.Min(checked(x + 1), region.MaxXExclusive);
        long minZ = Math.Max(z, region.MinZ), maxZ = Math.Min(checked(z + 1), region.MaxZExclusive);
        return minX <= maxX && minZ <= maxZ && (minX < maxX || minZ < maxZ);
    }

    static bool SegmentTouches(MapLatticeAddress from, MapLatticeAddress to, MapCellRect region)
    {
        MapExactValue low = default, high = new(1, 1);
        return Axis(new(from.X, from.Denominator), new(to.X, to.Denominator), region.MinX, region.MaxXExclusive) &&
            Axis(new(from.Z, from.Denominator), new(to.Z, to.Denominator), region.MinZ, region.MaxZExclusive) && low.CompareTo(high) < 0;

        bool Axis(MapExactValue a, MapExactValue b, long min, long max)
        {
            MapExactValue delta = b.Subtract(a);
            if (delta.Sign == 0) return a.CompareTo(new(min, 1)) >= 0 && a.CompareTo(new(max, 1)) <= 0;
            MapExactValue first = new MapExactValue(min, 1).Subtract(a).Divide(delta);
            MapExactValue last = new MapExactValue(max, 1).Subtract(a).Divide(delta);
            if (first.CompareTo(last) > 0) (first, last) = (last, first);
            if (first.CompareTo(low) > 0) low = first;
            if (last.CompareTo(high) < 0) high = last;
            return low.CompareTo(high) <= 0;
        }
    }
}
