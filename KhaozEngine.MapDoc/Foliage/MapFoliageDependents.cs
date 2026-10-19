using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Bounds = KhaozEngine.MapDoc.Foliage.MapFoliageGenerationContext.Bounds;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Selects and digests the generated batches one document edit changes. A batch exists on a side when its
/// layer does, it is not provably off the raster and it reads a stored chunk. Direct batches read a changed chunk, or
/// belong to a layer whose settings changed. Dependent batches are those whose preparation acquisition, the same
/// <see cref="MapFoliageSupport.Scope"/> preparation uses, meets the old or new bounds of a changed input. Over-selection
/// only costs preparation, since a row needs a bounds or digest change. Selection, scanning and preparation are
/// budgeted, and a budget or capacity refusal throws before any row is returned.</summary>
internal static class MapFoliageDependents
{
    /// <summary>Finalization ceilings for one edit: batch sides prepared, and existing batches inspected against changed
    /// inputs. Callers use the defaults. A refusal never truncates or inflates a ceiling to pass.</summary>
    internal sealed record Limits(int MaxPreparedSides = 4096, long MaxScannedBatches = 262_144);

    internal readonly record struct Side(MapFoliageRect? Bounds, string Digest);

    /// <summary>The changed batch rows. <paramref name="foliageOnly"/> marks documents that differ only in foliage and share
    /// one expansion, so no owner or surface can have changed and no context is captured to compare them.</summary>
    internal static IReadOnlyList<MapFoliageChunkChange> Rows(MapDocument before, MapDocument after,
        Func<MapFoliageGenerationContext> oldContext, Func<MapFoliageGenerationContext> newContext, bool foliageOnly = false, Limits? limits = null)
    {
        Dictionary<string, MapFoliageLayer> a = Layers(before.Foliage), b = Layers(after.Foliage);
        if (a.Count == 0 && b.Count == 0) return Array.Empty<MapFoliageChunkChange>();
        var budget = new Budget(limits ?? new());
        var direct = MapFoliageEffects.Direct(before, after).ToLookup(r => r.LayerId, r => r.Chunk, StringComparer.Ordinal);
        Changes? changes = null;
        var rows = new List<MapFoliageChunkChange>();
        foreach (string id in a.Keys.Union(b.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            a.TryGetValue(id, out MapFoliageLayer? la);
            b.TryGetValue(id, out MapFoliageLayer? lb);
            HashSet<MapFoliageChunkKey> presentA = Present(la), presentB = Present(lb);
            var selected = new HashSet<MapFoliageChunkKey>();
            bool sameSettings = la is not null && lb is not null &&
                string.Equals(MapFoliageEffects.Settings(la), MapFoliageEffects.Settings(lb), StringComparison.Ordinal);
            if (!sameSettings) selected.UnionWith(presentA.Concat(presentB));
            else
            {
                // A changed chunk is read by its own batch and the batches one chunk before it on each axis.
                foreach (MapFoliageChunkKey chunk in direct[id])
                    foreach (MapFoliageChunkKey key in Readers(chunk))
                        if (presentA.Contains(key) || presentB.Contains(key)) selected.Add(key);
                changes ??= foliageOnly ? new Changes() : Changes.Between(oldContext(), newContext());
                if (changes.Any)
                {
                    if (presentA.Count != 0) changes.Select(oldContext(), id, presentA, selected, budget);
                    if (presentB.Count != 0) changes.Select(newContext(), id, presentB, selected, budget);
                }
            }
            foreach (MapFoliageChunkKey key in selected.OrderBy(k => k.Z).ThenBy(k => k.X))
            {
                Side? sideA = presentA.Contains(key) ? Prepare(oldContext(), id, key, budget) : null;
                Side? sideB = presentB.Contains(key) ? Prepare(newContext(), id, key, budget) : null;
                if (sideA == sideB) continue;
                rows.Add(new(id, key, sideA?.Bounds, sideB?.Bounds, sideA?.Digest, sideB?.Digest));
            }
        }
        return Array.AsReadOnly(rows.ToArray());
    }

    static Dictionary<string, MapFoliageLayer> Layers(MapFoliageSet? set)
    {
        var layers = new Dictionary<string, MapFoliageLayer>(StringComparer.Ordinal);
        foreach (MapFoliageLayer layer in set?.Layers ?? []) layers.TryAdd(layer.Id, layer);
        return layers;
    }

    static IEnumerable<MapFoliageChunkKey> Readers(MapFoliageChunkKey chunk)
    {
        for (int dz = 0; dz <= 1; dz++)
            for (int dx = 0; dx <= 1; dx++)
                if ((long)chunk.X - dx >= int.MinValue && (long)chunk.Z - dz >= int.MinValue)
                    yield return new(chunk.X - dx, chunk.Z - dz);
    }

    // The batches that read at least one stored chunk and are not provably off the raster.
    internal static HashSet<MapFoliageChunkKey> Present(MapFoliageLayer? layer)
    {
        var present = new HashSet<MapFoliageChunkKey>();
        if (layer is null) return present;
        foreach (MapFoliageChunkDigest stored in layer.ChunkDigests)
            foreach (MapFoliageChunkKey key in Readers(stored.Key))
                if (!present.Contains(key) && !OffRaster(layer, key)) present.Add(key);
        return present;
    }

    static bool OffRaster(MapFoliageLayer layer, MapFoliageChunkKey key)
    {
        try { return MapFoliageDistribution.KnownOffRaster(layer, MapFoliageDistribution.Rectangle(layer, key)); }
        catch (MapFoliageRefusal) { return false; }
    }

    // The batch digest Generate publishes for this chunk, read from the context's own captured layer.
    static Side Prepare(MapFoliageGenerationContext context, string layerId, MapFoliageChunkKey key, Budget budget)
    {
        budget.Prepare();
        MapFoliageLayer layer = context.Foliage?.Layers.FirstOrDefault(l => string.Equals(l.Id, layerId, StringComparison.Ordinal))
            ?? throw new ArgumentException($"the foliage context does not hold layer '{layerId}'.");
        MapPreparedFoliageClearance clearance = MapFoliageClearance.Prepare(context, layer, key, MapFoliageDistribution.Limits(null));
        string digest = clearance.Status switch
        {
            MapFoliageStatus.Complete => MapFoliageDependencies.Compute(clearance.Support, clearance, layer, key),
            MapFoliageStatus.CapacityExceeded => throw MapFoliageEditRefusal.Create(MapFoliageStatus.CapacityExceeded,
                $"foliage layer '{layerId}' batch {key.X},{key.Z} exceeds a preparation budget"),
            _ => Refused(layer, key, clearance.Status),
        };
        return new(BatchBounds(layer, key), digest);
    }

    // A deterministic refusal still names what was read, so a byte change on a refused batch shows.
    static string Refused(MapFoliageLayer layer, MapFoliageChunkKey key, MapFoliageStatus status) => MapCanonical.HashHex(w =>
    {
        w.WriteStartArray();
        w.WriteStringValue("kemap/foliage-batch-refused/1");
        w.WriteStringValue(layer.Id); w.WriteNumberValue(key.X); w.WriteNumberValue(key.Z);
        w.WriteStringValue(status.ToString());
        string settings = MapFoliageEffects.Settings(layer);
        var stored = layer.ChunkDigests.ToDictionary(d => d.Key, d => d.Sha256);
        for (int dz = 0; dz <= 1; dz++)
            for (int dx = 0; dx <= 1; dx++)
            {
                var read = new MapFoliageChunkKey(key.X + dx, key.Z + dz);
                w.WriteStringValue(stored.TryGetValue(read, out string? sha) ? MapFoliageEffects.Digest(settings, read, sha) : "absent");
            }
        w.WriteEndArray();
    });

    // The chunk rectangle clipped to the raster sample domain, rounded outward.
    internal static MapFoliageRect? BatchBounds(MapFoliageLayer layer, MapFoliageChunkKey key)
    {
        var (minX, minZ, maxX, maxZ) = MapFoliageSupport.Rect(layer, key);
        minX = Math.Max(minX, layer.OriginX);
        minZ = Math.Max(minZ, layer.OriginZ);
        maxX = Math.Min(maxX, layer.OriginX + (double)(layer.Width - 1) * layer.CellSize);
        maxZ = Math.Min(maxZ, layer.OriginZ + (double)(layer.Depth - 1) * layer.CellSize);
        if (!(minX <= maxX && minZ <= maxZ)) return null;
        return new(MapFoliageEffects.Down(minX), MapFoliageEffects.Down(minZ), MapFoliageEffects.Up(maxX), MapFoliageEffects.Up(maxZ));
    }

    sealed class Budget(Limits limits)
    {
        long _scanned;
        int _prepared;
        internal void Scan(long count = 1)
        {
            _scanned += count;
            if (_scanned > limits.MaxScannedBatches)
                throw MapFoliageEditRefusal.Create(MapFoliageStatus.CapacityExceeded, $"foliage finalization scanned more than {limits.MaxScannedBatches} batches");
        }
        internal void Prepare()
        {
            if (++_prepared > limits.MaxPreparedSides)
                throw MapFoliageEditRefusal.Create(MapFoliageStatus.CapacityExceeded, $"foliage finalization prepared more than {limits.MaxPreparedSides} batch sides");
        }
    }

    /// <summary>The XZ bounds of every input that changed between two captured contexts. All marks a change whose
    /// extent is not bounded here, such as a different closure, which selects every existing batch.</summary>
    sealed class Changes
    {
        readonly List<Bounds> _rects = new();
        bool _all;
        internal bool Any => _all || _rects.Count != 0;

        internal static Changes Between(MapFoliageGenerationContext a, MapFoliageGenerationContext b)
        {
            var changes = new Changes();
            // A foliage-only edit shares one expansion, so nothing it can read changed.
            if (ReferenceEquals(a.Resources, b.Resources)) return changes;
            if (!string.Equals(a.Assets.Hash, b.Assets.Hash, StringComparison.Ordinal)) { changes._all = true; return changes; }
            try
            {
                var patches = new List<Bounds>();
                MapEffectiveContext ea = Effective(a.Resources), eb = Effective(b.Resources);
                if (!string.Equals(ea.Digest, eb.Digest, StringComparison.Ordinal)) Surfaces(ea, eb, patches);
                changes._rects.AddRange(patches);
                Owners(a, b, patches, changes._rects);
            }
            catch (Exception error) when (error is MapExactOverflowException or OverflowException or MapDocumentException or KeyNotFoundException)
            { changes._all = true; }
            return changes;
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
            foreach (string key in a.Transforms.Keys.Union(b.Transforms.Keys, StringComparer.Ordinal))
            {
                string prefix = MapEffectiveContext.Key(MapPrefabChildKind.Record, "");
                if (!key.StartsWith(prefix, StringComparison.Ordinal) || a.Transforms.GetValueOrDefault(key) == b.Transforms.GetValueOrDefault(key)) continue;
                string id = key[prefix.Length..];
                foreach (MapEffectiveContext context in new[] { a, b })
                    foreach (MapSurfacePatch patch in context.Surfaces.Patches.Values)
                        foreach (MapTopologyRecord record in patch.Records.Where(r => string.Equals(r.Id, id, StringComparison.Ordinal)))
                            Record(context, patch, record, rects);
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
            foreach (MapTopologyRecord record in patch.Records) Record(context, patch, record, rects);
        }

        // A prism can reach beyond its anchor patch. Other records lie on their anchor, which the patch already covers.
        static void Record(MapEffectiveContext context, MapSurfacePatch patch, MapTopologyRecord record, List<Bounds> rects)
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
            _ = patch;
        }

        // Clearance owners whose placement, pose, proxy or released seat differ, and every exception mask whose owner
        // pose, proxy or seat differs. An owner whose seat scope meets a changed patch counts as changed, since its
        // exceptional seat reads support there.
        static void Owners(MapFoliageGenerationContext a, MapFoliageGenerationContext b, List<Bounds> patches, List<Bounds> rects)
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
                    !SeatMeets(a, owner, patches) && !SeatMeets(b, owner, patches);
                if (same) continue;
                if (hasA) rects.Add(oa.Proxy);
                if (hasB) rects.Add(ob.Proxy);
            }
            var masksA = a.ClearanceOwners.MaskProxies().ToDictionary(m => (m.LayerId, m.Mask.Mask.Id));
            var masksB = b.ClearanceOwners.MaskProxies().ToDictionary(m => (m.LayerId, m.Mask.Mask.Id));
            foreach (var key in masksA.Keys.Union(masksB.Keys))
            {
                bool hasA = masksA.TryGetValue(key, out var ma), hasB = masksB.TryGetValue(key, out var mb);
                bool same = hasA && hasB && ma.Proxy == mb.Proxy && ma.Mask.Pose == mb.Mask.Pose && ma.Mask.Mask == mb.Mask.Mask &&
                    a.ClearanceOwners.SeatIdentity(ma.Mask.Mask.Owner) == b.ClearanceOwners.SeatIdentity(mb.Mask.Mask.Owner) &&
                    !SeatMeets(a, ma.Mask.Mask.Owner, patches) && !SeatMeets(b, mb.Mask.Mask.Owner, patches);
                if (same) continue;
                if (hasA) rects.Add(ma.Proxy);
                if (hasB) rects.Add(mb.Proxy);
            }
        }

        static bool SamePlacement(MapResolvedPlacement a, MapResolvedPlacement b) =>
            string.Equals(a.AssetId, b.AssetId, StringComparison.Ordinal) && string.Equals(a.Kind, b.Kind, StringComparison.Ordinal) &&
            a.NumericId == b.NumericId && a.Transform == b.Transform && a.Tags.SequenceEqual(b.Tags, StringComparer.Ordinal);

        // An exceptional seat acquires support within two metres of the owner's origin.
        static bool SeatMeets(MapFoliageGenerationContext context, MapPrefabReference owner, List<Bounds> patches)
        {
            if (patches.Count == 0) return false;
            if (owner.Kind == MapPrefabChildKind.Marker && !context.Resources.EffectiveMarkers.Any(m => m.Id == owner.Id)) return false;
            MapEffectiveTransform pose = MapFoliageOwners.Pose(context.Resources, owner);
            var seat = new Bounds(pose.X - 2.01, pose.Z - 2.01, pose.X + 2.01, pose.Z + 2.01);
            return patches.Any(p => p.Meets(seat));
        }

        /// <summary>Adds every existing batch of <paramref name="layerId"/> on the context's side whose preparation scope
        /// meets a changed input. Each changed rectangle visits only the batches a widened rectangle can reach.</summary>
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
            foreach (Bounds rect in _rects)
            {
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
}

/// <summary>Typed foliage refusals raised by document finalization or its preflight, before any result exists. The
/// message starts with the status name, so every caller and wire reply carries it.</summary>
internal static class MapFoliageEditRefusal
{
    internal static MapDocumentException Create(MapFoliageStatus status, string detail) =>
        new(string.Create(CultureInfo.InvariantCulture, $"Foliage {status}: {detail}."));
}
