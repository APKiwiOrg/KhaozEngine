using System;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Allocation-free before-work sizing of the captured membership product.</summary>
internal readonly record struct MapMembershipPreparation(long Bytes, long Faces, int Cells, int Records,
    int Footprints, int Prisms, int Parts)
{
    internal static MapSceneStatus Measure(MapScopedScene scene, out MapMembershipPreparation demand, out string? detail)
    {
        var view = scene.SurfaceView;
        demand = default; detail = null;
        if (!scene.Witness.CompleteForScope || view.Status != MapAcquireStatus.Complete)
        { detail = "Membership preparation requires complete scoped geometry."; return MapSceneStatus.Invalid; }
        {
            int footprints = 0, prisms = 0;
            Int128 polygonVertices = 0;
            Int128 openingFaces = 0, chainVertices = 0, identityBytes = 0;
            for (int i = 0; i < view.Witness.Records.Count; i++)
            {
                if (!view.TryRecord(view.Witness.Records[i], out var record, out _, borrow: true)) continue;
                identityBytes = checked(identityBytes + 4096 + 16L * record!.Id.Length);
                if (record is MapBoundaryChain chain) chainVertices = checked(chainVertices + chain.Vertices.Count);
                if (record is MapCavePortal portal) polygonVertices = checked(polygonVertices + portal.Interval.Count);
                if (record is MapHorizontalOpening opening) openingFaces = checked(openingFaces + 4L * opening.SlotCells.Count);
                if (record is MapSpaceFootprint footprint) { footprints++; polygonVertices = checked(polygonVertices + 4L * footprint.SlotCells.Count); }
                if (record is MapPrismDomain prism)
                {
                    prisms++;
                    if (prism.PayloadVersion != 1 || !float.IsFinite(prism.LowerY) || !float.IsFinite(prism.UpperY) || prism.LowerY >= prism.UpperY)
                    { detail = "Invalid prepared prism payload."; return MapSceneStatus.Invalid; }
                    polygonVertices = checked(polygonVertices + 4L * prism.Footprint.Rects.Count);
                    for (int r = 0; r < prism.Footprint.Rings.Count; r++)
                        polygonVertices = checked(polygonVertices + prism.Footprint.Rings[r].Vertices.Count);
                }
            }
            long partCount = Math.Max(1, (long)footprints * ((long)prisms + 1));
            // Distinct prisms can alias the same owner. Each such group multiplies the possible parent paths.
            // With unique owners, every prism contributes at most one interval per root footprint.
            for (int i = 0; i < view.Witness.Records.Count; i++)
            {
                if (!view.TryRecord(view.Witness.Records[i], out var record, out _, borrow: true) || record is not MapPrismDomain prism) continue;
                string? owner = Owner(view, prism.Space);
                if (owner is null) { detail = "Invalid prepared prism owner."; return MapSceneStatus.Invalid; }
                int aliases = 0; bool earlier = false;
                for (int j = 0; j < view.Witness.Records.Count; j++)
                    if (view.TryRecord(view.Witness.Records[j], out var peer, out _, borrow: true) && peer is MapPrismDomain other && Owner(view, other.Space) == owner)
                    { aliases++; earlier |= j < i; }
                if (earlier || aliases < 2) continue;
                if (partCount > int.MaxValue / aliases) return Capacity(out detail);
                partCount *= aliases;
            }
            if (partCount > int.MaxValue) return Capacity(out detail);
            int parts = (int)partCount;
            Int128 faces = openingFaces; int cells = 0;
            if (footprints != 0 || prisms != 0 || HasIndoor(view) || HasPortal(view))
                for (int p = 0; p < view.Witness.Present.Count; p++)
                {
                    var key = view.Witness.Present[p].Key;
                    if (!view.TryAcquiredPatch(key, out var patch, out _) || patch is null) continue;
                    var surface = Surface(view, key.SurfaceId);
                    if (surface.Role is not (MapSurfaceRole.SupportFloor or MapSurfaceRole.Ceiling)) continue;
                    long nextCells = (long)cells + patch.Width * patch.Depth;
                    if (nextCells > int.MaxValue) return Capacity(out detail);
                    cells = (int)nextCells;
                    long patchFaces = 0;
                    for (int z = 0; z < patch.Depth; z++)
                        for (int x = 0; x < patch.Width; x++)
                        {
                            if (!MapMembershipPreflight.CellValid(surface, patch.Cells[z * patch.Width + x]))
                            { detail = "Invalid acquired membership cell."; return MapSceneStatus.Invalid; }
                            long physical = MapSurfaceCompiler.CountFaces(surface, patch, (z + patch.CellMinZ) * 64 + x + patch.CellMinX);
                            patchFaces += physical;
                            if (patchFaces > MapSurfaceCompiler.MaxFacesPerPatch) return Capacity(out detail);
                            // Raised indoor bounds keep two additional exact triangle sets. A transformed legacy
                            // fallback keeps four affine chart triangles even though it has no physical faces.
                            faces = checked(faces + physical * (surface.IndoorSpan is null ? 1 : 3) +
                                (MapSurfaceCompiler.Fallback(surface, patch.Cells[z * patch.Width + x]) ? 4 : 0));
                        }
                }
            // Only the borrowed-input header is known before admitting sparse discovery. Geometry,
            // associations and temporary phase capacities are reserved by their construction owners.
            Int128 bytes = checked(4096 + MapMembershipExact.StorageBytes +
                (Int128)view.Surfaces.Count * 512 + identityBytes +
                (Int128)view.Witness.Records.Count * view.Witness.Records.Count * 16 +
                MapMembershipPointScratch.Bytes(parts, prisms) + IntentBytes(scene.CapturedBuildingIntent));
            if (bytes > long.MaxValue || faces > long.MaxValue) return Capacity(out detail);
            demand = new((long)bytes, 0, cells == 0 ? 0 : 1, view.Witness.Records.Count, footprints, prisms, parts);
            return MapSceneStatus.CompleteForScope;
        }
    }
    static string? Owner(MapScopedSurfaces view, MapRecordRef reference)
    {
        for (int i = 0; i <= view.Witness.Records.Count; i++)
        {
            if (!view.TryRecord(reference, out var record, out _, borrow: true) || record is not MapSpaceDoc space) return null;
            if (space.AliasOf is null) return space.Id;
            reference = space.AliasOf;
        }
        return null;
    }
    static MapSceneStatus Capacity(out string? detail)
    {
        detail = "Membership preparation capacity cannot be admitted.";
        return MapSceneStatus.CapacityExceeded;
    }
    static Int128 IntentBytes(MapBuildingIntent intent)
    {
        Int128 bytes = checked(intent.Roofs.Count * 512L + intent.RoofLinks.Count * 512L);
        for (int i = 0; i < intent.RoofGroups.Count; i++) bytes = checked(bytes + 512 + intent.RoofGroups[i].Rooms.Count * 64L);
        for (int i = 0; i < intent.RoofLinks.Count; i++)
        {
            var link = intent.RoofLinks[i]; bytes = checked(bytes + link.Rooms.Count * 64L);
            for (int g = 0; g < link.Groups.Count; g++)
                for (int j = 0; j < intent.RoofGroups.Count; j++)
                    if (intent.RoofGroups[j].Id == link.Groups[g].Id) bytes = checked(bytes + intent.RoofGroups[j].Rooms.Count * 64L);
        }
        return bytes;
    }
    static bool HasPortal(MapScopedSurfaces view)
    {
        for (int i = 0; i < view.Witness.Records.Count; i++)
            if (view.TryRecord(view.Witness.Records[i], out var record, out _, borrow: true) && record is MapCavePortal) return true;
        return false;
    }
    static bool HasIndoor(MapScopedSurfaces view)
    {
        for (int i = 0; i < view.Surfaces.Count; i++) if (view.Surfaces[i].IndoorSpan is not null) return true;
        return false;
    }
    internal static MapSurfaceRef Surface(MapScopedSurfaces view, string id)
    {
        for (int i = 0; i < view.Surfaces.Count; i++) if (view.Surfaces[i].Id == id) return view.Surfaces[i];
        throw new MapDocumentException("missing geometry: prepared surface");
    }
}
