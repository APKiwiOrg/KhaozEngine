using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using KhaozEngine.Diagnostics;
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

/// <summary>Selects and digests what one document edit changes in generated foliage (T9-P4a). A layer whose
/// whole-layer member changed, or that holding stored chunks was added or removed, publishes one layer entry with a
/// preparation-free digest. Every other layer publishes batch rows. A batch exists on a side when its layer does, it is
/// not provably off the raster and it reads a stored chunk. Direct batches read a changed chunk. Dependent batches are
/// those whose preparation acquisition, the same <see cref="MapFoliageSupport.Scope"/> preparation uses, meets the old or
/// new bounds of a changed input: geometry, owners, masks, consulted compatibility rows and owner correspondences.
/// Over-selection only costs preparation, since a row needs a bounds or digest change. A layer with no stored chunk on
/// either side publishes nothing. Selection and preparation are budgeted per edit, and a layer that would exceed a
/// ceiling publishes a layer entry instead, so finalization never refuses for its own work.</summary>
internal static class MapFoliageDependents
{
    /// <summary>Finalization ceilings for one edit: batch sides prepared, and existing batches inspected against changed
    /// inputs. Callers use the defaults. One budget is shared by every layer of the edit, spent in ordinal layer ID order,
    /// so once a layer exceeds a ceiling every later layer that selects or prepares anything degrades too. A layer past a
    /// ceiling degrades to a layer entry.</summary>
    internal sealed record Limits(int MaxPreparedSides = 4096, long MaxScannedBatches = 262_144);

    internal readonly record struct Side(MapFoliageRect? Bounds, string Digest);

    /// <summary>The changed batch rows and layer entries. <paramref name="foliageOnly"/> marks documents that differ only
    /// in foliage and share one expansion, so only their foliage members are compared, and no context is captured when
    /// no localized member changed.</summary>
    internal static (IReadOnlyList<MapFoliageChunkChange> Rows, IReadOnlyList<MapFoliageLayerChange> Layers) Describe(MapDocument before,
        MapDocument after, Func<MapFoliageGenerationContext> oldContext, Func<MapFoliageGenerationContext> newContext,
        bool foliageOnly = false, Limits? limits = null)
    {
        Dictionary<string, MapFoliageLayer> a = Layers(before.Foliage), b = Layers(after.Foliage);
        var rows = new List<MapFoliageChunkChange>();
        var entries = new List<MapFoliageLayerChange>();
        var budget = new Budget(limits ?? new());
        MapFoliageChangedInputs? changes = null;
        foreach (string id in a.Keys.Union(b.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            a.TryGetValue(id, out MapFoliageLayer? la);
            b.TryGetValue(id, out MapFoliageLayer? lb);
            // T9-P3: an all-zero raster places nothing, so a layer without stored chunks on either side publishes nothing.
            if ((la?.ChunkDigests.Count ?? 0) == 0 && (lb?.ChunkDigests.Count ?? 0) == 0) continue;
            if (la is null || lb is null || !string.Equals(Whole(la), Whole(lb), StringComparison.Ordinal))
            {
                Entry(entries, id, la, lb);
                continue;
            }
            try
            {
                HashSet<MapFoliageChunkKey> presentA = Present(la), presentB = Present(lb);
                var selected = new HashSet<MapFoliageChunkKey>();
                // Invert the same rounded ownership and released float sampling intervals as batch dependencies.
                var shaA = la.ChunkDigests.ToDictionary(d => d.Key, d => d.Sha256);
                var shaB = lb.ChunkDigests.ToDictionary(d => d.Key, d => d.Sha256);
                foreach (MapFoliageChunkKey chunk in shaA.Keys.Union(shaB.Keys).Where(k => shaA.GetValueOrDefault(k) != shaB.GetValueOrDefault(k)))
                    foreach (MapFoliageChunkKey key in MapFoliageRasterReads.Readers(la, chunk).Union(MapFoliageRasterReads.Readers(lb, chunk)))
                        if (presentA.Contains(key) || presentB.Contains(key)) selected.Add(key);
                bool members = !la.ExceptionMasks.SequenceEqual(lb.ExceptionMasks) || !la.CompatibilityOrdinals.SequenceEqual(lb.CompatibilityOrdinals) ||
                    !la.OwnerSupports.SequenceEqual(lb.OwnerSupports);
                if (!foliageOnly || members)
                {
                    changes ??= MapFoliageChangedInputs.Between(oldContext(), newContext(), foliageOnly);
                    if (members) changes.AddLayerMembers(oldContext(), newContext(), la, lb);
                    if (changes.Any(id))
                    {
                        if (presentA.Count != 0) changes.Select(oldContext(), id, presentA, selected, budget);
                        if (presentB.Count != 0) changes.Select(newContext(), id, presentB, selected, budget);
                    }
                }
                var layerRows = new List<MapFoliageChunkChange>();
                foreach (MapFoliageChunkKey key in selected.OrderBy(k => k.Z).ThenBy(k => k.X))
                {
                    Side? sideA = presentA.Contains(key) ? Prepare(oldContext(), id, key, budget) : null;
                    Side? sideB = presentB.Contains(key) ? Prepare(newContext(), id, key, budget) : null;
                    if (sideA != sideB) layerRows.Add(new(id, key, sideA?.Bounds, sideB?.Bounds, sideA?.Digest, sideB?.Digest));
                }
                rows.AddRange(layerRows);
            }
            catch (BudgetExceeded)
            {
                // Past a ceiling the layer is rebuilt whole rather than refusing the edit, and work stays bounded. The
                // entry is published even when its digests are equal, as after a dependency change.
                entries.Add(new(id, Digest(la), Digest(lb)));
            }
        }
        return (Array.AsReadOnly(rows.ToArray()), Array.AsReadOnly(entries.ToArray()));
    }

    static void Entry(List<MapFoliageLayerChange> entries, string id, MapFoliageLayer? before, MapFoliageLayer? after)
    {
        string? a = Digest(before), b = Digest(after);
        if (!string.Equals(a, b, StringComparison.Ordinal)) entries.Add(new(id, a, b));
    }

    // An entry side's digest, null for a missing layer or one without stored chunks.
    static string? Digest(MapFoliageLayer? layer) => layer is { ChunkDigests.Count: > 0 } ? LayerDigest(layer) : null;

    /// <summary>The preparation-free layer digest: the settings digest over every member except density, and the ordered
    /// stored-chunk digests.</summary>
    internal static string LayerDigest(MapFoliageLayer layer) => MapCanonical.HashHex(w =>
    {
        w.WriteStartArray();
        w.WriteStringValue("kemap/foliage-layer/1");
        w.WriteStringValue(MapFoliageEffects.Settings(layer));
        foreach (MapFoliageChunkDigest chunk in layer.ChunkDigests)
        { w.WriteNumberValue(chunk.Key.X); w.WriteNumberValue(chunk.Key.Z); w.WriteStringValue(chunk.Sha256); }
        w.WriteEndArray();
    });

    // The whole-layer members: every authored member except density, masks, compatibility rows and owner correspondences.
    static string Whole(MapFoliageLayer l) => MapFoliageEffects.Settings(new MapFoliageLayer(l.PayloadVersion, l.Id, l.Support,
        l.OriginX, l.OriginZ, l.CellSize, l.Width, l.Depth, l.RowDirection, l.Seed, l.Spacing, l.ScaleMin, l.ScaleMax, l.RootOffset,
        l.Models, l.AllowedVisibleMaterials, l.Materials, l.Policy, l.ExcludeIndoors, l.ExcludeSolidObjects, l.DoorClearance,
        l.EdgeFade, [], l.ClearanceRules, [], [], []));

    static Dictionary<string, MapFoliageLayer> Layers(MapFoliageSet? set)
    {
        var layers = new Dictionary<string, MapFoliageLayer>(StringComparer.Ordinal);
        foreach (MapFoliageLayer layer in set?.Layers ?? []) layers.TryAdd(layer.Id, layer);
        return layers;
    }

    // The batches that read at least one stored chunk and are not provably off the raster.
    internal static HashSet<MapFoliageChunkKey> Present(MapFoliageLayer? layer)
    {
        var present = new HashSet<MapFoliageChunkKey>();
        if (layer is null) return present;
        foreach (MapFoliageChunkDigest stored in layer.ChunkDigests)
            foreach (MapFoliageChunkKey key in MapFoliageRasterReads.Readers(layer, stored.Key))
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
        // A per-batch refusal, including a preparation budget, is a deterministic refused side, never an edit refusal.
        string digest = clearance.Status == MapFoliageStatus.Complete
            ? MapFoliageDependencies.Compute(clearance.Support, clearance, layer, key) : Refused(layer, key, clearance.Status);
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
        foreach (var read in MapFoliageRasterReads.Density(layer, key))
            w.WriteStringValue(stored.TryGetValue(read, out string? sha) ? MapFoliageEffects.Digest(settings, read, sha) : "absent");
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

    internal sealed class Budget(Limits limits)
    {
        long _scanned;
        int _prepared;
        internal void Scan(long count = 1)
        {
            _scanned += count;
            if (DemandDiagnostics.Check(this, "MapFoliageDependents.MaxScannedBatches", "MaxScannedBatches", 0, _scanned, limits.MaxScannedBatches, _scanned > limits.MaxScannedBatches, CapacityDemandLifetime.Cumulative)) throw new BudgetExceeded();
        }
        internal void Prepare()
        {
            if (DemandDiagnostics.Check(this, "MapFoliageDependents.MaxPreparedSides", "MaxPreparedSides", _prepared, 1, limits.MaxPreparedSides, ++_prepared > limits.MaxPreparedSides, CapacityDemandLifetime.Cumulative)) throw new BudgetExceeded();
        }
    }

    sealed class BudgetExceeded : Exception
    {
    }
}

/// <summary>Typed foliage refusals raised by document finalization or its preflight, before any result exists. The
/// message starts with the status name, so every caller and wire reply carries it.</summary>
internal static class MapFoliageEditRefusal
{
    internal static MapDocumentException Create(MapFoliageStatus status, string detail) =>
        new(string.Create(CultureInfo.InvariantCulture, $"Foliage {status}: {detail}."));
}
