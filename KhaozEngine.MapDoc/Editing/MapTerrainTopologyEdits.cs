using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Editing;

/// <summary>Explicit ownership and anchor changes, including every record referrer.</summary>
internal static class MapTerrainTopologyEdits
{
    internal static void Reassign(MapSurfaceSet set, MapReassignCornerOwner edit)
    {
        ArgumentNullException.ThrowIfNull(edit.Changes);
        var seen = new HashSet<(MapPatchKey, int, int)>();
        foreach (MapCornerOwnerChange change in edit.Changes)
        {
            if (!seen.Add((change.Dependent, change.CornerX, change.CornerZ)))
                throw new MapDocumentException("duplicate corner owner change");
            MapSurfacePatch patch = MapTerrainEdits.Patch(set, change.Dependent);
            MapLatticeAddress address = patch.CornerAddress(change.CornerX, change.CornerZ);
            int index = patch.CornerDependencies.FindIndex(d => d.CornerX == change.CornerX && d.CornerZ == change.CornerZ);
            MapVertexOwner? old = index < 0 ? null : patch.CornerDependencies[index].Owner;
            if (old != change.OldOwner) throw new MapDocumentException("corner owner change has a stale old owner");
            ArgumentNullException.ThrowIfNull(change.NewOwner);
            bool owns = change.NewOwner.Patch == patch.Key;
            if (owns && change.NewOwner.Address != address) throw new MapDocumentException("corner owner has the wrong self address");
            if (index >= 0) patch.CornerDependencies.RemoveAt(index);
            if (!owns) patch.CornerDependencies.Add(new(change.CornerX, change.CornerZ, change.NewOwner));
        }
    }

    internal static void Replace(MapSurfaceSet set, MapReplaceTopology edit)
    {
        ArgumentNullException.ThrowIfNull(edit.UpsertPatches);
        ArgumentNullException.ThrowIfNull(edit.RemovePatches);
        ArgumentNullException.ThrowIfNull(edit.UpsertSurfaces);
        ArgumentNullException.ThrowIfNull(edit.RemoveSurfaces);
        if (edit.UpsertPatches.Select(p => p.Key).Distinct().Count() != edit.UpsertPatches.Count ||
            edit.RemovePatches.Distinct().Count() != edit.RemovePatches.Count ||
            edit.UpsertPatches.Any(p => edit.RemovePatches.Contains(p.Key)))
            throw new MapDocumentException("duplicate or conflicting topology patch writes");
        if (edit.UpsertSurfaces.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != edit.UpsertSurfaces.Count ||
            edit.RemoveSurfaces.Distinct(StringComparer.Ordinal).Count() != edit.RemoveSurfaces.Count ||
            edit.UpsertSurfaces.Any(s => edit.RemoveSurfaces.Contains(s.Id, StringComparer.Ordinal)))
            throw new MapDocumentException("duplicate or conflicting topology surface writes");

        MapSurfaceSet before = set.Clone();
        foreach (MapPatchKey key in edit.RemovePatches)
        {
            MapSurfacePatch removed = MapTerrainEdits.Patch(set, key);
            if (removed.Records.OrderBy(r => r.Id, StringComparer.Ordinal).FirstOrDefault() is { } record)
                throw new MapDocumentException($"anchor patch '{key}' still carries record '{record.Id}', reanchor it first");
            set.Patches.Remove(key);
        }
        foreach (string id in edit.RemoveSurfaces)
            if (set.Refs.RemoveAll(s => s.Id == id) == 0) throw new MapDocumentException($"missing surface '{id}'");
        foreach (MapSurfaceRef surface in edit.UpsertSurfaces)
        {
            MapSurfaceRef copy = surface with
            {
                IndoorSpan = surface.IndoorSpan is { } span ? span with { DomainTags = span.DomainTags.ToArray() } : null,
            };
            int index = set.Refs.FindIndex(s => s.Id == copy.Id);
            if (index < 0) set.Refs.Add(copy);
            else set.Refs[index] = copy;
        }
        // Existing ownership wins even when a lower-key patch is added. New corners use creation order by key.
        foreach (MapSurfacePatch supplied in edit.UpsertPatches.OrderBy(p => p.Key))
        {
            MapSurfacePatch patch = supplied.Clone();
            MapTerrainEdits.RequireValid(patch.ValidateLocal());
            for (int z = 0; z <= patch.Depth; z++)
                for (int x = 0; x <= patch.Width; x++)
                {
                    MapLatticeAddress address = patch.CornerAddress(x, z);
                    MapVertexOwner? owner = Owner(before, patch.Key.SurfaceId, address)
                        ?? Owner(set, patch.Key.SurfaceId, address);
                    if (owner is null) continue;
                    patch.CornerDependencies.RemoveAll(d => d.CornerX == x && d.CornerZ == z);
                    if (owner.Patch != patch.Key) patch.CornerDependencies.Add(new(x, z, owner));
                }
            set.Patches[patch.Key] = patch;
        }
    }

    static MapVertexOwner? Owner(MapSurfaceSet set, string surfaceId, MapLatticeAddress address)
    {
        MapVertexOwner? owner = null;
        foreach (MapSurfacePatch incident in set.Patches.Values.Where(p => p.Key.SurfaceId == surfaceId))
        {
            MapLatticeAddress start = incident.CornerAddress(0, 0);
            Int128 x = (Int128)address.X - start.X, z = (Int128)address.Z - start.Z;
            if (x < 0 || x > incident.Width || z < 0 || z > incident.Depth) continue;
            MapCornerDependency? dependency = incident.CornerDependencies.Find(d => d.CornerX == (int)x && d.CornerZ == (int)z);
            MapVertexOwner current = dependency?.Owner ?? new(incident.Key, address);
            if (owner is not null && owner != current) throw new MapDocumentException("ambiguous existing corner owner");
            owner = current;
        }
        return owner;
    }

    internal static void Reanchor(MapSurfaceSet set, MapReanchorRecords edit)
    {
        ArgumentNullException.ThrowIfNull(edit.Moves);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapRecordAnchorMove move in edit.Moves)
        {
            if (!seen.Add(move.RecordId)) throw new MapDocumentException("duplicate record anchor move");
            MapSurfacePatch from = MapTerrainEdits.Patch(set, move.From), to = MapTerrainEdits.Patch(set, move.To);
            int index = from.Records.FindIndex(r => r.Id == move.RecordId);
            if (index < 0) throw new MapDocumentException($"anchor '{move.From}' does not carry record '{move.RecordId}'");
            if (move.From == move.To) continue;
            if (to.Records.Any(r => r.Id == move.RecordId)) throw new MapDocumentException($"duplicate anchor for record '{move.RecordId}'");
            MapTopologyRecord record = from.Records[index];
            from.Records.RemoveAt(index);
            to.Records.Add(record);
            var old = new MapRecordRef(move.RecordId, move.From);
            var replacement = new MapRecordRef(move.RecordId, move.To);
            MapRecordRef Ref(MapRecordRef reference) => reference == old ? replacement : reference;
            MapRecordRef? Optional(MapRecordRef? reference) => reference is null ? null : Ref(reference);
            foreach (MapSurfacePatch patch in set.Patches.Values)
                for (int i = 0; i < patch.Records.Count; i++) patch.Records[i] = Rewrite(patch.Records[i], Ref, Optional);
            for (int i = 0; i < set.Refs.Count; i++)
                if (set.Refs[i].IndoorSpan is { } span && span.ParentSpace == old)
                    set.Refs[i] = set.Refs[i] with { IndoorSpan = span with { ParentSpace = replacement } };
        }
    }

    static MapTopologyRecord Rewrite(MapTopologyRecord record, Func<MapRecordRef, MapRecordRef> reference,
        Func<MapRecordRef?, MapRecordRef?> optional) => record switch
        {
            MapWallStrip strip => strip with { LowerChain = reference(strip.LowerChain), UpperChain = reference(strip.UpperChain) },
            MapCavePortal portal => portal with
            {
                FromSpace = reference(portal.FromSpace),
                ToSpace = reference(portal.ToSpace),
                BandBottom = reference(portal.BandBottom),
                BandTop = optional(portal.BandTop),
            },
            MapVerticalLink link => link with
            {
                UpperSpace = reference(link.UpperSpace),
                LowerSpace = reference(link.LowerSpace),
                Openings = link.Openings.Select(reference).ToArray(),
                Portals = link.Portals.Select(reference).ToArray(),
                GeometryOwners = link.GeometryOwners.Select(reference).ToArray(),
            },
            MapSpaceDoc space => space with
            {
                Parent = optional(space.Parent),
                AliasOf = optional(space.AliasOf),
                Walls = space.Walls.Select(w => w with { Record = reference(w.Record) }).ToArray(),
                Portals = space.Portals.Select(p => p with { Record = reference(p.Record) }).ToArray(),
                Links = space.Links.Select(reference).ToArray(),
            },
            MapSpaceFootprint footprint => footprint with
            {
                Space = reference(footprint.Space),
                Lower = footprint.Lower with { Opening = optional(footprint.Lower.Opening) },
                Upper = footprint.Upper with { Opening = optional(footprint.Upper.Opening) },
            },
            _ => record,
        };
}
