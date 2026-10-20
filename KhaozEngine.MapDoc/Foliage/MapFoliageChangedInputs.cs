using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Bounds = KhaozEngine.MapDoc.Foliage.MapFoliageGenerationContext.Bounds;
using Budget = KhaozEngine.MapDoc.Foliage.MapFoliageDependents.Budget;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>The XZ bounds of every input that changed between two captured contexts, each for every layer or for one
/// layer. All marks a change whose extent is not bounded here, such as a different closure, which selects every existing
/// batch. Geometry and clearance owners are compared only when the contexts do not share one expansion. Exception masks,
/// compatibility rows and owner correspondences are foliage members, so they are always compared.</summary>
internal sealed class MapFoliageChangedInputs
{
    readonly List<(string? Layer, Bounds Rect)> _rects = new();
    readonly List<Bounds> _patches = new();
    bool _all;

    internal bool Any(string layerId) => _all || _rects.Any(r => r.Layer is null || string.Equals(r.Layer, layerId, StringComparison.Ordinal));

    internal static MapFoliageChangedInputs Between(MapFoliageGenerationContext a, MapFoliageGenerationContext b, bool foliageOnly)
    {
        var changes = new MapFoliageChangedInputs();
        try
        {
            if (!foliageOnly && !ReferenceEquals(a.Resources, b.Resources))
            {
                if (!string.Equals(a.Assets.Hash, b.Assets.Hash, StringComparison.Ordinal)) { changes._all = true; return changes; }
                MapEffectiveContext ea = Effective(a.Resources), eb = Effective(b.Resources);
                if (!string.Equals(ea.Digest, eb.Digest, StringComparison.Ordinal)) Surfaces(ea, eb, changes._patches);
                changes._rects.AddRange(changes._patches.Select(p => ((string?)null, p)));
                changes.Placements(a, b);
            }
            changes.Masks(a, b);
        }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException or MapDocumentException or KeyNotFoundException)
        { changes._all = true; }
        return changes;
    }

    /// <summary>Adds the localized member changes of one layer: every surface whose compatibility row changed, with the
    /// owners seated near it, and every owner whose correspondence changed, by its old and new proxies.</summary>
    internal void AddLayerMembers(MapFoliageGenerationContext a, MapFoliageGenerationContext b, MapFoliageLayer before, MapFoliageLayer after)
    {
        try
        {
            var ordinalsA = before.CompatibilityOrdinals.ToDictionary(r => r.SurfaceId, r => r.Ordinal, StringComparer.Ordinal);
            var ordinalsB = after.CompatibilityOrdinals.ToDictionary(r => r.SurfaceId, r => r.Ordinal, StringComparer.Ordinal);
            var surfaces = ordinalsA.Keys.Union(ordinalsB.Keys, StringComparer.Ordinal)
                .Where(s => ordinalsA.TryGetValue(s, out int x) != ordinalsB.TryGetValue(s, out int y) || x != y).ToHashSet(StringComparer.Ordinal);
            var supportsA = before.OwnerSupports.GroupBy(o => o.Owner).ToDictionary(g => g.Key, g => g.ToArray());
            var supportsB = after.OwnerSupports.GroupBy(o => o.Owner).ToDictionary(g => g.Key, g => g.ToArray());
            var owners = supportsA.Keys.Union(supportsB.Keys)
                .Where(o => !(supportsA.GetValueOrDefault(o) ?? []).SequenceEqual(supportsB.GetValueOrDefault(o) ?? [])).ToHashSet();
            foreach (MapFoliageGenerationContext context in new[] { a, b })
            {
                var seats = new List<Bounds>();
                MapEffectiveContext effective = Effective(context.Resources);
                foreach (MapSurfacePatch patch in effective.Surfaces.Patches.Values.Where(p => surfaces.Contains(p.Key.SurfaceId)))
                    Patch(effective, patch, seats);
                foreach (Bounds rect in seats) _rects.Add((before.Id, rect));
                foreach (var (placement, proxy) in context.OwnerProxies())
                {
                    var owner = MapFoliageOwners.World(MapPrefabChildKind.Placement, placement.PlacementId);
                    if (owners.Contains(owner) || SeatMeets(context, owner, seats)) _rects.Add((before.Id, proxy));
                }
                foreach (var (layer, mask, proxy) in context.ClearanceOwners.MaskProxies())
                    if (string.Equals(layer, before.Id, StringComparison.Ordinal) && (owners.Contains(mask.Mask.Owner) || SeatMeets(context, mask.Mask.Owner, seats)))
                        _rects.Add((before.Id, proxy));
            }
        }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException or MapDocumentException or KeyNotFoundException)
        { _all = true; }
    }

    static MapEffectiveContext Effective(MapEffectiveResources resources) =>
        (resources.SurfaceSource as IMapEffectiveSurfaceContext)?.EffectiveContext
        ?? throw new MapDocumentException("foliage finalization requires captured effective surfaces");

    // Patches present on one side only, or whose digest, surface metadata or pose differ, with every record they hold.
    static void Surfaces(MapEffectiveContext a, MapEffectiveContext b, List<Bounds> rects)
    {
        var refsA = a.Surfaces.Refs.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var refsB = b.Surfaces.Refs.ToDictionary(s => s.Id, StringComparer.Ordinal);
        foreach (MapPatchKey key in a.Surfaces.Patches.Keys.Union(b.Surfaces.Patches.Keys))
        {
            a.Surfaces.Patches.TryGetValue(key, out MapSurfacePatch? pa);
            b.Surfaces.Patches.TryGetValue(key, out MapSurfacePatch? pb);
            string pose = MapEffectiveContext.Key(MapPrefabChildKind.Surface, key.SurfaceId);
            if (pa is not null && pb is not null && SameSurface(refsA.GetValueOrDefault(key.SurfaceId), refsB.GetValueOrDefault(key.SurfaceId)) &&
                a.Transforms.GetValueOrDefault(pose) == b.Transforms.GetValueOrDefault(pose) &&
                string.Equals(MapSurfaceSemantics.PatchDigest(pa), MapSurfaceSemantics.PatchDigest(pb), StringComparison.Ordinal)) continue;
            if (pa is not null) Patch(a, pa, rects);
            if (pb is not null) Patch(b, pb, rects);
        }
        // A posed record can move without any patch digest changing.
        string prefix = MapEffectiveContext.Key(MapPrefabChildKind.Record, "");
        foreach (string key in a.Transforms.Keys.Union(b.Transforms.Keys, StringComparer.Ordinal))
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal) || a.Transforms.GetValueOrDefault(key) == b.Transforms.GetValueOrDefault(key)) continue;
            string id = key[prefix.Length..];
            foreach (MapEffectiveContext context in new[] { a, b })
                foreach (MapSurfacePatch patch in context.Surfaces.Patches.Values)
                    foreach (MapTopologyRecord record in patch.Records.Where(r => string.Equals(r.Id, id, StringComparison.Ordinal)))
                        Record(context, record, rects);
        }
    }

    static bool SameSurface(MapSurfaceRef? a, MapSurfaceRef? b) =>
        a is not null && b is not null && a with { SemanticSha256 = "" } == b with { SemanticSha256 = "" };

    // Acquisition selects whole slots whose cells meet the scope widened by one cell, so a changed patch reaches every
    // scope that meets its slot widened by one cell, through the surface's effective pose.
    static void Patch(MapEffectiveContext context, MapSurfacePatch patch, List<Bounds> rects)
    {
        long x0 = checked(patch.Key.SlotX * 64 - 1), z0 = checked(patch.Key.SlotZ * 64 - 1), x1 = checked(x0 + 66), z1 = checked(z0 + 66);
        rects.Add(Bounds.Of(MapEffectiveGeometry.Bounds(new[] { MapLatticeAddress.Corner(x0, z0), MapLatticeAddress.Corner(x1, z0),
            MapLatticeAddress.Corner(x1, z1), MapLatticeAddress.Corner(x0, z1) }
            .Select(address => MapEffectiveGeometry.Vertex(context, new(patch.Key.SurfaceId, address))))));
        foreach (MapTopologyRecord record in patch.Records) Record(context, record, rects);
    }

    // A prism can reach beyond its anchor patch. Other records lie on their anchor, which the patch already covers.
    static void Record(MapEffectiveContext context, MapTopologyRecord record, List<Bounds> rects)
    {
        if (record is not MapPrismDomain prism) return;
        var points = prism.Footprint.Rects.SelectMany(r => new[] { MapLatticeAddress.Corner(r.MinX, r.MinZ),
            MapLatticeAddress.Corner(r.MaxX, r.MinZ), MapLatticeAddress.Corner(r.MaxX, r.MaxZ), MapLatticeAddress.Corner(r.MinX, r.MaxZ) })
            .Concat(prism.Footprint.Rings.SelectMany(r => r.Vertices))
            .Select(address =>
            {
                var local = prism.Footprint.Frame.WorldXz(address);
                var world = context.Point(MapPrefabChildKind.Record, prism.Id, new(local.X, default, local.Z));
                return new MapExactXz(world.X, world.Z);
            }).ToArray();
        if (points.Length != 0) rects.Add(Bounds.Of(MapEffectiveGeometry.Bounds(points)));
    }

    // Clearance placements whose placement, pose, proxy or released seat differ. An owner whose seat scope meets a changed
    // patch counts as changed, since its exceptional seat reads support there.
    void Placements(MapFoliageGenerationContext a, MapFoliageGenerationContext b)
    {
        var ownersA = a.OwnerProxies().ToDictionary(o => o.Placement.PlacementId, StringComparer.Ordinal);
        var ownersB = b.OwnerProxies().ToDictionary(o => o.Placement.PlacementId, StringComparer.Ordinal);
        foreach (string id in ownersA.Keys.Union(ownersB.Keys, StringComparer.Ordinal))
        {
            bool hasA = ownersA.TryGetValue(id, out var oa), hasB = ownersB.TryGetValue(id, out var ob);
            var owner = MapFoliageOwners.World(MapPrefabChildKind.Placement, id);
            bool same = hasA && hasB && oa.Proxy == ob.Proxy && SamePlacement(oa.Placement, ob.Placement) &&
                a.Resources.Transforms.GetValueOrDefault(MapEffectiveContext.Key(MapPrefabChildKind.Placement, id)) ==
                    b.Resources.Transforms.GetValueOrDefault(MapEffectiveContext.Key(MapPrefabChildKind.Placement, id)) &&
                a.ClearanceOwners.SeatIdentity(owner) == b.ClearanceOwners.SeatIdentity(owner) &&
                !SeatMeets(a, owner, _patches) && !SeatMeets(b, owner, _patches);
            if (same) continue;
            if (hasA) _rects.Add((null, oa.Proxy));
            if (hasB) _rects.Add((null, ob.Proxy));
        }
    }

    // Exception masks whose definition, owner pose, proxy or seat differ, each for its own layer, old and new footprints.
    void Masks(MapFoliageGenerationContext a, MapFoliageGenerationContext b)
    {
        var masksA = a.ClearanceOwners.MaskProxies().ToDictionary(m => (m.LayerId, m.Mask.Mask.Id));
        var masksB = b.ClearanceOwners.MaskProxies().ToDictionary(m => (m.LayerId, m.Mask.Mask.Id));
        foreach (var key in masksA.Keys.Union(masksB.Keys))
        {
            bool hasA = masksA.TryGetValue(key, out var ma), hasB = masksB.TryGetValue(key, out var mb);
            bool same = hasA && hasB && ma.Proxy == mb.Proxy && ma.Mask.Pose == mb.Mask.Pose && ma.Mask.Mask == mb.Mask.Mask &&
                a.ClearanceOwners.SeatIdentity(ma.Mask.Mask.Owner) == b.ClearanceOwners.SeatIdentity(mb.Mask.Mask.Owner) &&
                !SeatMeets(a, ma.Mask.Mask.Owner, _patches) && !SeatMeets(b, mb.Mask.Mask.Owner, _patches);
            if (same) continue;
            if (hasA) _rects.Add((key.LayerId, ma.Proxy));
            if (hasB) _rects.Add((key.LayerId, mb.Proxy));
        }
    }

    static bool SamePlacement(MapResolvedPlacement a, MapResolvedPlacement b) =>
        string.Equals(a.AssetId, b.AssetId, StringComparison.Ordinal) && string.Equals(a.Kind, b.Kind, StringComparison.Ordinal) &&
        a.NumericId == b.NumericId && a.Transform == b.Transform && a.Tags.SequenceEqual(b.Tags, StringComparer.Ordinal);

    // An exceptional seat acquires support within the owners' seat reach of the owner's origin.
    static bool SeatMeets(MapFoliageGenerationContext context, MapPrefabReference owner, List<Bounds> patches)
    {
        if (patches.Count == 0) return false;
        if (owner.Kind == MapPrefabChildKind.Marker && !context.Resources.EffectiveMarkers.Any(m => m.Id == owner.Id)) return false;
        MapEffectiveTransform pose = MapFoliageOwners.Pose(context.Resources, owner);
        double reach = MapFoliageOwners.SeatReach + 0.01;
        var seat = new Bounds(pose.X - reach, pose.Z - reach, pose.X + reach, pose.Z + reach);
        return patches.Any(p => p.Meets(seat));
    }

    /// <summary>Adds every existing batch of <paramref name="layerId"/> on the context's side whose preparation scope
    /// meets a changed input of every layer or of that layer. Each changed rectangle visits only the batches a widened
    /// rectangle can reach.</summary>
    internal void Select(MapFoliageGenerationContext context, string layerId, HashSet<MapFoliageChunkKey> present,
        HashSet<MapFoliageChunkKey> selected, Budget budget)
    {
        if (_all)
        {
            budget.Scan(present.Count);
            selected.UnionWith(present);
            return;
        }
        MapFoliageLayer layer = context.Foliage!.Layers.First(l => string.Equals(l.Id, layerId, StringComparison.Ordinal));
        double pad = Pad(context, layer);
        double span = (double)MapFoliageChunk.Size * layer.CellSize;
        foreach (var (target, rect) in _rects)
        {
            if (target is not null && !string.Equals(target, layerId, StringComparison.Ordinal)) continue;
            double x0 = Math.Floor((rect.MinX - pad - layer.OriginX) / span), x1 = Math.Floor((rect.MaxX + pad - layer.OriginX) / span);
            double z0 = Math.Floor((rect.MinZ - pad - layer.OriginZ) / span), z1 = Math.Floor((rect.MaxZ + pad - layer.OriginZ) / span);
            IEnumerable<MapFoliageChunkKey> candidates;
            double range = (x1 - x0 + 1) * (z1 - z0 + 1);
            if (!double.IsFinite(range) || range > present.Count) candidates = present;
            else
            {
                var keys = new List<MapFoliageChunkKey>();
                for (double z = z0; z <= z1; z++)
                    for (double x = x0; x <= x1; x++)
                        if (x >= int.MinValue && x <= int.MaxValue && z >= int.MinValue && z <= int.MaxValue) keys.Add(new((int)x, (int)z));
                candidates = keys;
            }
            foreach (MapFoliageChunkKey key in candidates)
            {
                budget.Scan();
                if (selected.Contains(key) || !present.Contains(key)) continue;
                if (Meets(context, layer, key, rect)) selected.Add(key);
            }
        }
    }

    // The widest halo any batch scope of this layer can take: three of the largest support cells, the clearance read
    // reach over every support floor and the scope's own margin, plus a margin for single precision rounding.
    static double Pad(MapFoliageGenerationContext context, MapFoliageLayer layer)
    {
        var floors = context.Resources.SurfaceSource.Surfaces.Where(s => s.Role == MapSurfaceRole.SupportFloor)
            .Select(s => (Surface: s, Pose: context.Resources.Transforms.TryGetValue(MapEffectiveContext.Key(MapPrefabChildKind.Surface, s.Id),
                out MapEffectiveTransform t) ? t : (MapEffectiveTransform?)null)).ToArray();
        double cell = floors.Length == 0 ? 0 : floors.Max(f => f.Surface.Frame.CellUnitMetres.Exact().ToDouble() * (f.Pose?.Scale ?? 1));
        return 3 * cell + MapFoliageSupport.ReadReach(layer, floors) + 0.02 + 1e-6 * Math.Abs(layer.OriginX) + 1e-6 * Math.Abs(layer.OriginZ);
    }

    static bool Meets(MapFoliageGenerationContext context, MapFoliageLayer layer, MapFoliageChunkKey key, Bounds rect)
    {
        try
        {
            MapSurfaceScope scope = MapFoliageSupport.Scope(layer, key, context.SupportFloorsFor(layer, key), context.Resources.Transforms);
            return Bounds.Of(scope.WorldRectangle()).Meets(rect);
        }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException or ArgumentException) { return true; }
    }
}
