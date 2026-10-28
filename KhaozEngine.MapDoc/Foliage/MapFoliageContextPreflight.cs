using KhaozEngine.Diagnostics;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Context construction ceilings, admitted before expansion, cloning or index staging. Released prefab
/// expansion limits still apply beside these. Window capture applies them to admitted scoped input bytes, generation
/// and density descriptors, placements, patches and paint targets through the scene admission allowance.</summary>
/// <param name="MaxSourceUtf8Bytes">Demand sites: MapFoliageContextPreflight, MapSceneAdmission. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxChunkDescriptors">Demand sites: MapFoliageContextPreflight, MapFoliageWindowCapture, MapSceneAdmission. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxPlacements">Demand sites: MapFoliageContextPreflight, MapSceneAdmission. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxSurfacePatches">Demand sites: MapFoliageContextPreflight, MapSceneAdmission. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxPaintTargets">Demand sites: MapFoliageContextPreflight, MapSceneAdmission. See docs/CAPACITY-DEMAND.md.</param>
public sealed record MapFoliageContextLimits(long MaxSourceUtf8Bytes = 134_217_728, int MaxChunkDescriptors = 65_536,
    int MaxPlacements = 65_536, int MaxSurfacePatches = 65_536, int MaxPaintTargets = 4096);

/// <summary>Context construction work. The first five members are preflight charges counted before any expansion.
/// Expansions and SnapshotSerializations count actual calls made while capturing, never an assumed value.</summary>
public sealed record MapFoliageContextWork(long SourceUtf8Bytes, long ChunkDescriptors, long Placements, long SurfacePatches,
    long PaintTargets, long Expansions, long SnapshotSerializations);

/// <summary>A preflight outcome. A refusal reports the charges counted up to the refused ceiling.</summary>
public sealed record MapFoliageContextMeasure(MapFoliageStatus Status, MapFoliageContextWork Work)
{
    public Scene.MapSceneCapacity? Capacity { get; init; }
}

/// <summary>Counts a document's context demand from metadata alone, refusing before expansion, cloning or staging.
/// Residency is checked first, so a partial document refuses before any read. SourceUtf8Bytes is a conservative charge
/// over the inline foliage form, walked from the object model with no serialized buffer: 8 bytes per JSON value, a
/// further 64 per number, and 6 times the UTF-16 length plus 2 for every string value and property name. A density
/// chunk's token is canonical base64, which never escapes, so its 1368 characters are charged once each plus 2. Placements and paint targets count authored rows plus the
/// prefab children that can produce them, an upper bound on what expansion yields. Surface patches are the released
/// prefab preflight's patch count, taken under the caller's expansion limits.</summary>
internal static class MapFoliageContextPreflight
{
    const long ValueBytes = 8, NumberBytes = 64;
    static readonly int DensityTokenChars = 4 * ((MapFoliageChunk.ByteLength + 2) / 3);

    internal static MapFoliageContextMeasure Measure(MapDocument document, MapAssetClosure assets, MapFoliageContextLimits limits,
        MapPrefabExpansionLimits? expansionLimits = null) => Measure(document, assets, limits, document?.Foliage, expansionLimits);

    /// <summary>Measures <paramref name="set"/> as the foliage of <paramref name="document"/>. The surface patch count is
    /// the prefab preflight's under <paramref name="expansionLimits"/>, the engine default when null.</summary>
    internal static MapFoliageContextMeasure Measure(MapDocument document, MapAssetClosure assets, MapFoliageContextLimits limits, MapFoliageSet? set,
        MapPrefabExpansionLimits? expansionLimits = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaxSourceUtf8Bytes < 0 || limits.MaxChunkDescriptors < 0 || limits.MaxPlacements < 0 ||
            limits.MaxSurfacePatches < 0 || limits.MaxPaintTargets < 0) throw new ArgumentOutOfRangeException(nameof(limits));
        using var demandScope = DemandDiagnostics.Enter(document, "MapFoliageContextPreflight");
        var work = new MapFoliageContextWork(0, 0, 0, 0, 0, 0, 0);
        if (IsPartial(document) || set?.Layers.Any(l => l.Chunks.Count != l.ChunkDigests.Count) == true)
            return new(MapFoliageStatus.PartialResidency, work);

        work = work with { ChunkDescriptors = set?.Layers.Sum(l => (long)l.ChunkDigests.Count) ?? 0 };
        if (DemandDiagnostics.Check(document, "MapFoliageContextPreflight.MaxChunkDescriptors.1", "MaxChunkDescriptors", 0, work.ChunkDescriptors, limits.MaxChunkDescriptors, work.ChunkDescriptors > limits.MaxChunkDescriptors, CapacityDemandLifetime.Inventory, allowanceSource: limits)) return new(MapFoliageStatus.CapacityExceeded, work)
        { Capacity = new(nameof(MapFoliageContextLimits.MaxChunkDescriptors), work.ChunkDescriptors, limits.MaxChunkDescriptors) };

        var source = new SourceCharge(limits.MaxSourceUtf8Bytes);
        if (set is not null) Charge(source, set);
        work = work with { SourceUtf8Bytes = source.Bytes };
        if (DemandDiagnostics.Check(source, "MapFoliageContextPreflight.SourceBytes", nameof(MapFoliageContextLimits.MaxSourceUtf8Bytes), 0, source.Bytes, limits.MaxSourceUtf8Bytes, source.Exceeded, allowanceSource: limits)) return new(MapFoliageStatus.CapacityExceeded, work)
        { Capacity = new(nameof(MapFoliageContextLimits.MaxSourceUtf8Bytes), work.SourceUtf8Bytes, limits.MaxSourceUtf8Bytes) };

        work = work with { Placements = PlacementDemand(document, assets) };
        if (DemandDiagnostics.Check(document, "MapFoliageContextPreflight.MaxPlacements.1", "MaxPlacements", 0, work.Placements, limits.MaxPlacements, work.Placements > limits.MaxPlacements, CapacityDemandLifetime.Inventory, allowanceSource: limits)) return new(MapFoliageStatus.CapacityExceeded, work)
        { Capacity = new(nameof(MapFoliageContextLimits.MaxPlacements), work.Placements, limits.MaxPlacements) };

        work = work with { PaintTargets = PaintTargetDemand(document, assets) };
        if (DemandDiagnostics.Check(document, "MapFoliageContextPreflight.MaxPaintTargets.1", "MaxPaintTargets", 0, work.PaintTargets, limits.MaxPaintTargets, work.PaintTargets > limits.MaxPaintTargets, CapacityDemandLifetime.Inventory, allowanceSource: limits)) return new(MapFoliageStatus.CapacityExceeded, work)
        { Capacity = new(nameof(MapFoliageContextLimits.MaxPaintTargets), work.PaintTargets, limits.MaxPaintTargets) };

        MapPrefabExpansionWork expansion;
        try { expansion = MapPrefabPreflight.Count(document, assets, expansionLimits ?? new()); }
        catch (MapDocumentException error)
        {
            bool capacity = error.Message.Contains("limit exceeded", StringComparison.Ordinal) ||
                error.Message.Contains("overflow", StringComparison.Ordinal);
            return new(capacity ? MapFoliageStatus.CapacityExceeded : MapFoliageStatus.Invalid, work);
        }
        catch (KeyNotFoundException) { return new(MapFoliageStatus.Invalid, work); }
        work = work with { SurfacePatches = expansion.Patches };
        if (DemandDiagnostics.Check(document, "MapFoliageContextPreflight.MaxSurfacePatches.1", "MaxSurfacePatches", 0, work.SurfacePatches, limits.MaxSurfacePatches, work.SurfacePatches > limits.MaxSurfacePatches, CapacityDemandLifetime.Inventory, allowanceSource: limits)) return new(MapFoliageStatus.CapacityExceeded, work)
        { Capacity = new(nameof(MapFoliageContextLimits.MaxSurfacePatches), work.SurfacePatches, limits.MaxSurfacePatches) };
        return new(MapFoliageStatus.Complete, work);
    }

    /// <summary>True when any tile, surface page or foliage chunk the document advertises is not resident.</summary>
    internal static bool IsPartial(MapDocument document) => document.Tiles?.IsPartial == true ||
        document.Foliage?.Layers.Any(l => l.Chunks.Count != l.ChunkDigests.Count) == true;

    // Authored placements, plus every placement child and placement add-override of each live instance's prefab.
    static long PlacementDemand(MapDocument document, MapAssetClosure assets)
    {
        long count = document.Placements.Count;
        List<MapPrefabAsset> live = LiveAssets(document, assets, out List<MapPrefabOverride> overrides);
        foreach (MapPrefabAsset asset in live) count += asset.Children.Count(c => c is MapPrefabPlacementChild);
        count += overrides.Count(o => o is MapPrefabAddOverride { Value: MapPrefabPlacementChild } or MapPrefabReplaceOverride { Value: MapPrefabPlacementChild });
        return count;
    }

    // Distinct authored paint targets, plus one target per paint surface child or paint add-override of each live
    // instance, since a remapped local target may be distinct per instance.
    static long PaintTargetDemand(MapDocument document, MapAssetClosure assets)
    {
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapSurfaceRef surface in document.Surfaces.Refs)
            if (surface.Role == MapSurfaceRole.PaintOverride) targets.Add(surface.PaintTargetSurfaceId ?? surface.Id);
        long count = targets.Count;
        List<MapPrefabAsset> live = LiveAssets(document, assets, out List<MapPrefabOverride> overrides);
        foreach (MapPrefabAsset asset in live) count += asset.Children.Count(c => c is MapPrefabSurfaceChild { Surface.Role: MapSurfaceRole.PaintOverride });
        count += overrides.Count(o => o is MapPrefabAddOverride { Value: MapPrefabSurfaceChild { Surface.Role: MapSurfaceRole.PaintOverride } }
            or MapPrefabReplaceOverride { Value: MapPrefabSurfaceChild { Surface.Role: MapSurfaceRole.PaintOverride } });
        return count;
    }

    // The resolvable prefab of every live instance, borrowed without expansion. An unresolvable one is left to the
    // released preflight, which refuses it.
    static List<MapPrefabAsset> LiveAssets(MapDocument document, MapAssetClosure assets, out List<MapPrefabOverride> overrides)
    {
        var result = new List<MapPrefabAsset>();
        overrides = new List<MapPrefabOverride>();
        foreach (MapPrefabInstance instance in document.Prefabs?.Instances ?? [])
        {
            if (instance.Removed) continue;
            overrides.AddRange(instance.Overrides);
            if (assets.TryGetResource(instance.ResourceId, out MapResolvedResource? resource) && resource!.Kind == MapResourceKind.Prefab)
                result.Add(assets.BorrowPrefab(instance.ResourceId));
        }
        return result;
    }

    // Mirrors the inline writer member for member, so each charge matches one written token.
    static void Charge(SourceCharge c, MapFoliageSet set)
    {
        c.Value();
        c.Number("payloadVersion");
        c.Container("layers");
        foreach (MapFoliageLayer layer in set.Layers)
        {
            if (c.Exceeded) return;
            c.Value();
            c.Number("payloadVersion");
            c.Text("id", layer.Id);
            c.Name("support");
            Binding(c, layer.Support);
            foreach (string name in new[] { "originX", "originZ", "cellSize", "width", "depth" }) c.Number(name);
            c.Text("rowDirection", layer.RowDirection.ToString());
            foreach (string name in new[] { "seed", "spacing", "scaleMin", "scaleMax", "rootOffset" }) c.Number(name);
            c.Container("models");
            foreach (MapFoliageModel model in layer.Models) { c.Value(); c.Text("assetId", model.AssetId); c.Number("weight"); }
            c.Container("allowedVisibleMaterials");
            c.Add(layer.AllowedVisibleMaterials.Count, ValueBytes + NumberBytes);
            c.Container("materials");
            foreach (MapFoliageMaterial material in layer.Materials)
            { c.Value(); c.Number("id"); c.Text("resourceId", material.ResourceId); c.Text("kind", material.Kind.ToString()); }
            c.Container("policy");
            c.Text("id", layer.Policy.Id);
            c.Number("version");
            c.Number("normalBoundaryHeight");
            c.Boolean("excludeIndoors");
            c.Boolean("excludeSolidObjects");
            c.Number("doorClearance");
            c.Number("edgeFade");
            c.Container("compatibilityOrdinals");
            foreach (MapFoliageCompatibilityOrdinal row in layer.CompatibilityOrdinals) { c.Value(); c.Text("surfaceId", row.SurfaceId); c.Number("ordinal"); }
            c.Container("clearanceRules");
            foreach (MapFoliageClearanceRule rule in layer.ClearanceRules)
            {
                if (c.Exceeded) return;
                c.Value();
                c.Text("id", rule.Id);
                c.Text("assetId", rule.AssetId);
                if (rule.PlacementTag is { } tag) c.Text("placementTag", tag);
                c.Text("kind", rule.Kind.ToString());
                Rect(c);
            }
            c.Container("exceptionMasks");
            foreach (MapFoliageMask mask in layer.ExceptionMasks)
            {
                if (c.Exceeded) return;
                c.Value();
                c.Text("id", mask.Id);
                Reference(c, mask.Owner);
                c.Name("support");
                Binding(c, mask.Support);
                c.Text("kind", mask.Kind.ToString());
                Rect(c);
            }
            c.Container("ownerSupports");
            foreach (MapFoliageOwnerSupport owner in layer.OwnerSupports)
            {
                if (c.Exceeded) return;
                c.Value();
                Reference(c, owner.Owner);
                c.Name("support");
                Binding(c, owner.Support);
            }
            c.Container("chunks");
            c.Add(layer.ChunkDigests.Count, ChunkBytes);
        }
    }

    // One inline chunk object: {"x":n,"z":n,"density":"<1368 base64 characters>"}. The closed codec admits only
    // canonical base64, which never escapes, so the token is charged one byte per character plus its two quotes.
    internal static readonly long ChunkBytes = ValueBytes + Name("x") + ValueBytes + NumberBytes + Name("z") + ValueBytes + NumberBytes +
        Name("density") + ValueBytes + DensityTokenChars + 2;

    /// <summary>The SourceUtf8Bytes charge of <paramref name="set"/>, counting stopped once past <paramref name="max"/>.</summary>
    internal static long SourceBytes(MapFoliageSet set, long max)
    {
        var charge = new SourceCharge(max);
        Charge(charge, set);
        return charge.Bytes;
    }

    static long Name(string name) => 6L * name.Length + 2;

    static void Binding(SourceCharge c, MapSupportBinding? binding)
    {
        c.Value();
        if (binding is null) return;
        c.Text("kind", binding.Kind.ToString());
        if (binding.SurfaceId is { } surface) c.Text("surfaceId", surface);
        if (binding.SpaceId is { } space) c.Text("spaceId", space);
        if (binding.ReferenceY is not null) c.Number("referenceY");
        if (binding.SearchBelow is not null) c.Number("searchBelow");
        if (binding.SearchAbove is not null) c.Number("searchAbove");
    }

    static void Reference(SourceCharge c, MapPrefabReference? owner)
    {
        c.Container("owner");
        if (owner is null) return;
        c.Text("scope", owner.Scope.ToString());
        c.Text("kind", owner.Kind.ToString());
        c.Text("id", owner.Id);
    }

    static void Rect(SourceCharge c)
    {
        c.Container("footprint");
        foreach (string name in new[] { "minX", "minZ", "maxX", "maxZ" }) c.Number(name);
    }

    // Checked accumulation that stops counting once the ceiling is passed, so a hostile payload costs no more.
    sealed class SourceCharge(long max)
    {
        internal long Bytes { get; private set; }
        internal bool Exceeded => Bytes > max;
        internal void Value() => Add(1, ValueBytes);
        internal void Name(string name) => Add(1, 6L * name.Length + 2);
        internal void Container(string name) { Name(name); Value(); }
        internal void Boolean(string name) { Name(name); Value(); }
        internal void Number(string name) { Name(name); Add(1, ValueBytes + NumberBytes); }
        internal void Text(string name, string? value) { Name(name); Add(1, ValueBytes + 6L * (value?.Length ?? 0) + 2); }
        internal void Add(long count, long each)
        {
            if (Exceeded) return;
            long current = Bytes;
            try { Bytes = checked(Bytes + checked(count * each)); }
            catch (OverflowException) { Bytes = long.MaxValue; }
            DemandDiagnostics.Check(this, "MapFoliageContextPreflight.SourceCharge", nameof(MapFoliageContextLimits.MaxSourceUtf8Bytes), current, Bytes - current, max, Exceeded);
        }
    }
}
