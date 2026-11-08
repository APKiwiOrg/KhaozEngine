using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>Raw saves certify references and exact owner positions, never geometric seams or heights.</summary>
internal static class MapSurfaceSaveGuard
{
    internal static void Whole(MapSurfaceSet set)
    {
        IReadOnlyList<MapDocumentFinding> findings = MapTopologyReferenceValidator.ValidateFindings(set.Refs, set.Patches.Values.ToArray());
        if (findings.Count != 0) throw MapDocumentRefusalFactory.FromFinding(findings[0], findings[0].Message, findings: findings);
        try { foreach (MapSurfacePatch patch in set.Patches.Values) Positions(set, patch); }
        catch (Exception ex) when (ex is MapExactOverflowException or OverflowException)
        {
            throw new MapDocumentException(MapDocumentRefusal.GeometryNotRepresentable, "dependency position not representable", ex)
                { Operation = "SaveSurfaces", FieldPath = "CornerDependencies" };
        }
    }
    internal static void Partial(MapDocument doc, MapSurfaceStorageIndex index)
    {
        MapSurfaceSet set = doc.Surfaces;
        bool surfaceChanged = false;
        var reverseSurfaces = new List<MapSurfaceRef>(index.OriginalRefs);
        var positions = new MapSurfaceSet();
        positions.Refs.AddRange(set.Refs);
        foreach (var patch in set.Patches) positions.Patches.Add(patch.Key, patch.Value);
        foreach (MapSurfaceRef old in index.OriginalRefs)
        {
            MapSurfaceRef? current = set.Refs.FirstOrDefault(s => s.Id == old.Id);
            surfaceChanged |= current is null || current.Frame != old.Frame;
            if (current is null) positions.Refs.Add(old);
        }
        foreach (MapSurfaceRef surface in set.Refs)
        {
            MapSurfaceRef? original = index.OriginalRefs.FirstOrDefault(s => s.Id == surface.Id);
            if (original is null) reverseSurfaces.Add(surface);
            if (surface.IndoorSpan is { } span && span.ParentSpace != original?.IndoorSpan?.ParentSpace)
                ParentSpace(set, index, surface.Id, span);
        }
        var changed = new SortedSet<MapPatchKey>(index.Baselines.Keys.Where(k => !set.Patches.ContainsKey(k)));
        foreach (var pair in set.Patches)
        {
            if (!index.ByKey.TryGetValue(pair.Key, out MapSurfaceIndexEntry? entry) ||
                MapSurfaceSemantics.PatchDigest(pair.Value) != entry.SemanticSha256) changed.Add(pair.Key);
        }
        foreach (MapPatchKey key in changed)
        {
            set.Patches.TryGetValue(key, out MapSurfacePatch? current);
            index.Baselines.TryGetValue(key, out MapSurfacePatch? original);
            // The order below is part of the raw partial-save contract.
            if (original is null && index.StatusOf(key) == MapPatchStatus.Unloaded) RefuseUnloaded(key);
            if (current is not null)
            {
                foreach (MapPatchKey outgoing in MapSurfaceReferences.Patches(current))
                    if (!set.Patches.ContainsKey(outgoing))
                    {
                        if (index.StatusOf(outgoing) == MapPatchStatus.Unloaded) RefuseUnloaded(outgoing);
                        throw new MapDocumentException(MapDocumentRefusal.ReferenceMissing, $"missing referenced patch '{outgoing}'")
                            { Patch = outgoing, ExpectedPatch = key, SubjectKind = "patch" };
                    }
                Positions(positions, current);
            }
            MapSurfacePatch extent = original ?? current!;
            // Original coverage stays authoritative until the later structural refusal.
            MapSurfaceRef owner = original is not null
                ? index.OriginalRefs.First(s => s.Id == key.SurfaceId)
                : set.Refs.First(s => s.Id == key.SurfaceId);
            MapExactRect world = MapSurfaceRanges.World(MapSurfaceRanges.Rectangle(extent), owner.Frame);
            foreach (MapSurfaceRef surface in reverseSurfaces)
            {
                MapSlotRect slots = MapSurfaceRanges.Slots(MapSurfaceRanges.Cells(world, surface.Frame));
                foreach (MapDirectoryPageRef dir in index.Covering(surface.Id, slots))
                {
                    if (!index.DirectoryPages.TryGetValue(dir.Sha256, out var pages) ||
                        pages.Any(p => p.Covers.Overlaps(slots) && !index.IndexPages.ContainsKey(p.Sha256)))
                        throw new MapDocumentException(MapDocumentRefusal.StorageReverseKnowledgeUnknown, "unloaded: reverse dependants unknown")
                            { Patch = key, SurfaceId = surface.Id, ExpectedHash = dir.Sha256 };
                }
            }
            foreach (MapSurfaceIndexEntry entry in index.Entries)
                if (!entry.Loaded && entry.Dependencies.Contains(key))
                    throw new MapDocumentException(MapDocumentRefusal.StorageReverseDependantUnloaded, $"unloaded: reverse dependant '{entry.Key.SurfaceId}({entry.Key.SlotX}, {entry.Key.SlotZ})'")
                        { Patch = entry.Key, ExpectedPatch = key };
            if (index.ByKey.TryGetValue(key, out MapSurfaceIndexEntry? oldEntry))
                foreach (MapRecordRef incident in oldEntry.IncidentRecords)
                    if (!set.Patches.ContainsKey(incident.Anchor))
                        throw new MapDocumentException(MapDocumentRefusal.StorageReverseDependantUnloaded, $"unloaded: reverse dependant '{incident.Id}'")
                            { Patch = incident.Anchor, RecordId = incident.Id, ExpectedPatch = key };
            if (surfaceChanged) throw new MapDocumentException(MapDocumentRefusal.StoragePartialEditUnsupported, "unsupported partial edit: surface")
            { FieldPath = "Surfaces" };
            if (original is null || current is null || original.CellMinX != current.CellMinX || original.CellMinZ != current.CellMinZ ||
                original.Width != current.Width || original.Depth != current.Depth || !original.Presence.SequenceEqual(current.Presence) ||
                !original.EdgeSubdivisions.SequenceEqual(current.EdgeSubdivisions) || !original.CornerDependencies.SequenceEqual(current.CornerDependencies))
                throw new MapDocumentException(MapDocumentRefusal.StoragePartialEditUnsupported, "unsupported partial edit: structure")
                    { Patch = key, FieldPath = "Structure" };
            MapSurfacePatch beforeRecords = original.Clone(), afterRecords = current.Clone();
            // Compare canonical records without treating a permitted height or cell edit as a record edit.
            afterRecords.Heights = beforeRecords.Heights;
            afterRecords.Cells = beforeRecords.Cells;
            if (!MapSurfacePatchCodec.Encode(beforeRecords).AsSpan().SequenceEqual(MapSurfacePatchCodec.Encode(afterRecords)))
                throw new MapDocumentException(MapDocumentRefusal.StoragePartialEditUnsupported, "unsupported partial edit: records")
                    { Patch = key, FieldPath = "Records" };
        }
        if (surfaceChanged) throw new MapDocumentException(MapDocumentRefusal.StoragePartialEditUnsupported, "unsupported partial edit: surface")
            { FieldPath = "Surfaces" };
    }
    static void ParentSpace(MapSurfaceSet set, MapSurfaceStorageIndex index, string surfaceId, MapIndoorSpan span)
    {
        MapRecordRef reference = span.ParentSpace;
        if (!set.Patches.TryGetValue(reference.Anchor, out MapSurfacePatch? anchor))
        {
            if (index.StatusOf(reference.Anchor) == MapPatchStatus.Unloaded) RefuseUnloaded(reference.Anchor);
            throw new MapDocumentException(MapDocumentRefusal.ReferenceMissing, $"missing referenced patch '{reference.Anchor}'")
                { Patch = reference.Anchor, SurfaceId = surfaceId, RecordId = reference.Id, SubjectKind = "patch", FieldPath = "IndoorSpan.ParentSpace" };
        }
        string source = $"indoor span '{span.Id}' on surface '{surfaceId}'";
        MapTopologyRecord? record = anchor.Records.FirstOrDefault(r => r.Id == reference.Id);
        if (record is null) throw new MapDocumentException(MapDocumentRefusal.ReferenceMissing, $"missing record '{reference.Id}' in '{source}'")
            { Patch = reference.Anchor, RecordId = reference.Id, SurfaceId = surfaceId, SubjectKind = "record", FieldPath = "IndoorSpan.ParentSpace" };
        if (record is not MapSpaceDoc) throw new MapDocumentException(MapDocumentRefusal.ReferenceKindMismatch, $"wrong type for '{reference.Id}' in '{source}'")
            { Patch = reference.Anchor, RecordId = reference.Id, SurfaceId = surfaceId, RecordType = record.GetType(),
                ExpectedRecordType = typeof(MapSpaceDoc), FieldPath = "IndoorSpan.ParentSpace" };
    }
    static void RefuseUnloaded(MapPatchKey key) => throw new MapDocumentException(MapDocumentRefusal.StorageReferencedPatchUnloaded, $"unloaded referenced patch '{key}'")
        { Patch = key, PatchStatus = MapPatchStatus.Unloaded };
    static void Positions(MapSurfaceSet set, MapSurfacePatch dependent)
    {
        MapSurfaceRef? surface = set.Refs.FirstOrDefault(s => s.Id == dependent.Key.SurfaceId);
        if (surface is null) throw new MapDocumentException(MapDocumentRefusal.ReferenceMissing, "missing surface")
            { Patch = dependent.Key, SurfaceId = dependent.Key.SurfaceId, SubjectKind = "surface" };
        foreach (MapCornerDependency dependency in dependent.CornerDependencies)
        {
            if (!set.Patches.TryGetValue(dependency.Owner.Patch, out MapSurfacePatch? owner))
                throw new MapDocumentException(MapDocumentRefusal.ReferenceMissing, "missing dependency owner")
                    { Patch = dependent.Key, ExpectedPatch = dependency.Owner.Patch, SubjectKind = "dependency owner" };
            MapSurfaceRef? ownerSurface = set.Refs.FirstOrDefault(s => s.Id == owner.Key.SurfaceId);
            bool vertex = ownerSurface is not null && IsVertex(owner, dependency.Owner.Address);
            if (ownerSurface is null || !vertex ||
                surface.Frame.WorldXz(dependent.CornerAddress(dependency.CornerX, dependency.CornerZ)) != ownerSurface.Frame.WorldXz(dependency.Owner.Address))
                throw new MapDocumentException(ownerSurface is null ? MapDocumentRefusal.ReferenceMissing
                    : !vertex ? MapDocumentRefusal.SurfaceSeamVertexUndeclared : MapDocumentRefusal.SurfaceSeamPositionMismatch,
                    "dependency position: owner address and dependent corner disagree")
                { Patch = dependent.Key, ExpectedPatch = dependency.Owner.Patch, Address = dependency.Owner.Address,
                    CellX = dependency.CornerX, CellZ = dependency.CornerZ, SurfaceId = ownerSurface?.Id ?? owner.Key.SurfaceId,
                    SubjectKind = ownerSurface is null ? "surface" : "dependency owner" };
        }
    }
    static bool IsVertex(MapSurfacePatch patch, MapLatticeAddress address)
    {
        MapLatticeAddress min = patch.CornerAddress(0, 0);
        MapExactValue x = new MapExactValue(address.X, address.Denominator).Subtract(new(min.X, 1));
        MapExactValue z = new MapExactValue(address.Z, address.Denominator).Subtract(new(min.Z, 1));
        if (x.Sign < 0 || z.Sign < 0 || x.CompareTo(new(patch.Width, 1)) > 0 || z.CompareTo(new(patch.Depth, 1)) > 0) return false;
        if (address.Denominator == 1) return true;
        foreach (MapEdgeSubdivision edge in patch.EdgeSubdivisions)
        {
            MapExactValue along;
            bool horizontal = edge.Edge is MapCellEdge.South or MapCellEdge.North;
            if (horizontal)
            {
                if (z != new MapExactValue(edge.CellZ + (edge.Edge == MapCellEdge.North ? 1 : 0), 1)) continue;
                along = x.Subtract(new(edge.CellX, 1));
            }
            else
            {
                if (x != new MapExactValue(edge.CellX + (edge.Edge == MapCellEdge.East ? 1 : 0), 1)) continue;
                along = z.Subtract(new(edge.CellZ, 1));
            }
            if (along.Sign >= 0 && along.CompareTo(new(1, 1)) <= 0 && along.Multiply(new(edge.Segments, 1)).Denominator == 1) return true;
        }
        return false;
    }
}
