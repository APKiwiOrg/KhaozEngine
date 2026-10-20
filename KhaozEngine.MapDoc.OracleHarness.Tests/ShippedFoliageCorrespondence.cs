using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDocCompatibility;
using KhaozEngine.TileWorld;
using static KhaozEngine.Tests.MapDocOracle.ShippedFoliageManifest;

namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>Verifies every correspondence row against the original source and the native scaffold, independently of
/// any generated sample. Seated ordinals come from each original object's plane, never from the native table, and
/// per-owner applicability is recomputed from the source rules: Solid and Door on the layer's own plane, a roof only
/// from a strictly higher plane.</summary>
[UnsupportedOSPlatform("windows")]
internal sealed class ShippedFoliageCorrespondence(TileWorldDocument world, TileWorldCatalogs catalogs, FoliageManifest manifest,
    MapDocument native, MapAssetClosure assets)
{
    static readonly MapFoliageClearanceKind[] Kinds = [MapFoliageClearanceKind.Solid, MapFoliageClearanceKind.RoofAbove, MapFoliageClearanceKind.Door];
    readonly IReadOnlyList<MapFoliageLayer> _native = native.Foliage!.Layers;

    internal IReadOnlyList<FoliageLayerBinding> Layers()
    {
        IReadOnlyList<TileFoliageLayer> sources = world.FoliageLayers;
        Require(manifest.Layers.Count == sources.Count && manifest.Layers.Count == _native.Count &&
            manifest.Layers.Select(l => l.SourceLayerId).ToHashSet(StringComparer.Ordinal).SetEquals(sources.Select(l => l.Id)) &&
            manifest.Layers.Select(l => l.NativeLayerId).ToHashSet(StringComparer.Ordinal).SetEquals(_native.Select(l => l.Id)),
            "incomplete foliage layer correspondence");
        var result = new List<FoliageLayerBinding>();
        foreach (FoliageLayerRow row in manifest.Layers)
        {
            TileFoliageLayer source = sources.Single(l => l.Id == row.SourceLayerId);
            MapFoliageLayer layer = _native.Single(l => l.Id == row.NativeLayerId);
            Require(layer.Support.Kind == MapSupportBindingKind.Surface && layer.Support.SurfaceId == row.SupportSurfaceId,
                "private foliage imports must bind a Surface");
            Require(row.CompatibilitySupports.All(s => s.SourcePlane < world.PlaneCount), "unknown source plane");
            Require(row.CompatibilitySupports.Any(s => s.SourcePlane == source.Plane && s.NativeSurfaceId == row.SupportSurfaceId),
                "foliage layer support differs from its source plane");
            Require(layer.CompatibilityOrdinals.Count == row.CompatibilitySupports.Count && layer.CompatibilityOrdinals.All(t =>
                row.CompatibilitySupports.Any(s => s.NativeSurfaceId == t.SurfaceId && s.SourcePlane == t.Ordinal)),
                "compatibility table differs from source planes");
            foreach (FoliageSupportRow support in row.CompatibilitySupports) RequireDerived(support);
            Require(row.Models.Select(m => m.SourceArchetypeId).SequenceEqual(source.Archetypes.Select(a => a.Id), StringComparer.Ordinal) &&
                row.Models.Select(m => m.NativeAssetId).SequenceEqual(layer.Models.Select(m => m.AssetId), StringComparer.Ordinal),
                "foliage model correspondence differs");
            foreach (FoliageModelRow model in row.Models) Closure(() => assets.GetAsset(model.NativeAssetId));
            RequireMaterials(row, source, layer);
            Require(Same(row.RuleIds, layer.ClearanceRules.Select(r => r.Id)), "unknown or missing foliage clearance rule");
            Require(Same(row.MaskIds, layer.ExceptionMasks.Select(m => m.Id)), "unknown or missing foliage exception mask");
            result.Add(new(source, row, row.Models.ToDictionary(m => m.SourceArchetypeId, m => m.NativeAssetId, StringComparer.Ordinal),
                row.Materials.ToDictionary(m => m.SourceMaterialId, m => m.NativeResourceId)));
        }
        return result.AsReadOnly();
    }

    internal (IReadOnlyList<FoliageOwnerEffect> Effects, int Owners) Owners(MapFoliageGenerationContext context)
    {
        // Every source object the released sampler can clear foliage around: solid collision, a roof or a door tag.
        var sourceOwners = world.AllObjects().Where(o => catalogs.Archetype(o.ArchetypeId) is { } a && (Solid(a) || a.IsRoof || Door(o, a)))
            .ToDictionary(o => o.Id);
        Require(manifest.Owners.Count == sourceOwners.Count && manifest.Owners.All(o => sourceOwners.ContainsKey(o.SourceObjectId)),
            "incomplete foliage owner correspondence");
        var placements = context.Resources.Placements.ToDictionary(p => p.PlacementId, StringComparer.Ordinal);
        var seats = context.Resources.PlacementSupports.ToDictionary(s => s.PlacementId, s => s.Support, StringComparer.Ordinal);
        var effects = new List<FoliageOwnerEffect>();
        var listedPlacements = new HashSet<string>(StringComparer.Ordinal);
        foreach (FoliageOwnerRow row in manifest.Owners)
        {
            TileObject obj = sourceOwners[row.SourceObjectId];
            TileObjectArchetype archetype = catalogs.Archetype(obj.ArchetypeId)!;
            Require(row.SourcePlane == obj.Plane && row.SourceArchetypeId == obj.ArchetypeId, "foliage owner differs from its source object");
            // World placements carry their effective pose under the resolver's kind and id key.
            if (row.NativeOwner.Kind != MapPrefabChildKind.Placement || !placements.TryGetValue(row.NativeOwner.Id, out MapResolvedPlacement? placement) ||
                !context.Resources.Transforms.TryGetValue("Placement:" + row.NativeOwner.Id, out MapEffectiveTransform pose))
                throw new InvalidDataException("missing native foliage owner");
            listedPlacements.Add(row.NativeOwner.Id);
            FoliageLayerRow[] listing = manifest.Layers.Where(l => l.OwnerIds.Contains(row.SourceObjectId)).ToArray();
            Require(listing.Length != 0, "incomplete foliage owner correspondence");
            foreach (FoliageLayerRow layer in listing)
                Require(layer.CompatibilitySupports.Any(s => s.SourcePlane == obj.Plane && s.NativeSurfaceId == row.SeatedSurfaceId),
                    "seated support differs from the source plane");
            if (seats.TryGetValue(row.NativeOwner.Id, out var seat))
                Require(Seated(seat) == row.SeatedSurfaceId && _native.All(l => l.OwnerSupports.All(o => o.Owner != row.NativeOwner)),
                    "seated support differs from the source plane");
            else
                foreach (MapFoliageLayer layer in _native)
                {
                    MapFoliageOwnerSupport[] rows = layer.OwnerSupports.Where(o => o.Owner == row.NativeOwner).ToArray();
                    bool listed = listing.Any(l => l.NativeLayerId == layer.Id);
                    Require(rows.Length == (listed ? 1 : 0) && rows.All(o => o.Support.Kind == MapSupportBindingKind.Surface &&
                        o.Support.SurfaceId == row.SeatedSurfaceId), "seated support differs from the source plane");
                }

            TileRect footprint = TileFootprint.Of(archetype, obj.X, obj.Z, obj.Rotation);
            var matchedRules = new HashSet<string>(StringComparer.Ordinal);
            var matchedMasks = new HashSet<string>(StringComparer.Ordinal);
            var targets = new HashSet<string>(StringComparer.Ordinal);
            foreach (MapFoliageLayer layer in _native)
            {
                FoliageLayerRow layerRow = manifest.Layers.Single(l => l.NativeLayerId == layer.Id);
                TileFoliageLayer sourceLayer = world.FoliageLayers.Single(l => l.Id == layerRow.SourceLayerId);
                MapFoliageClearanceRule[] rules = layer.ClearanceRules.Where(r => Matches(r, placement)).ToArray();
                MapFoliageMask[] masks = layer.ExceptionMasks.Where(m => m.Owner == row.NativeOwner).ToArray();
                Require(listing.Any(l => l.NativeLayerId == layer.Id) == (rules.Length + masks.Length != 0), "foliage layer owner references differ");
                foreach (MapFoliageClearanceRule rule in rules) RequireRule(rule.Kind, rule.Footprint, pose, footprint, obj, archetype, matchedRules, rule.Id);
                foreach (MapFoliageMask mask in masks)
                {
                    Require(mask.Support.Kind == MapSupportBindingKind.Surface, "private foliage imports must bind a Surface");
                    RequireRule(mask.Kind, mask.Footprint, pose, footprint, obj, archetype, matchedMasks, mask.Id);
                }
                int? seated = Ordinal(layer, row.SeatedSurfaceId), sample = Ordinal(layer, layer.Support.SurfaceId!);
                foreach (MapFoliageClearanceKind kind in Kinds)
                {
                    bool expected = SourceEffect(kind, sourceLayer, obj, archetype);
                    bool actual = Enabled(layer, kind) && Applies(seated, sample, row.SeatedSurfaceId, layer.Support.SurfaceId!, kind) &&
                        (rules.Any(r => r.Kind == kind) || masks.Any(m => m.Kind == kind && m.Support.SurfaceId == layer.Support.SurfaceId));
                    Require(expected == actual, "foliage owner applicability differs from source");
                    if (!expected) continue;
                    effects.Add(new(row.SourceObjectId, layer.Id, kind));
                    targets.Add(layer.Id);
                }
            }
            Require(Same(row.RuleIds, matchedRules) && Same(row.MaskIds, matchedMasks), "foliage owner rule references differ");
            Require(Same(row.Targets.Select(t => t.NativeLayerId), targets) && row.Targets.All(t =>
                manifest.Layers.Single(l => l.NativeLayerId == t.NativeLayerId).SupportSurfaceId == t.SupportSurfaceId),
                "foliage clearing targets differ from source");
        }
        // No native owner outside the correspondence: every placement a rule matches and every mask owner is listed.
        foreach (MapFoliageLayer layer in _native)
        {
            Require(layer.ExceptionMasks.All(m => m.Owner.Kind == MapPrefabChildKind.Placement &&
                listedPlacements.Contains(m.Owner.Id)), "unlisted native foliage owner");
            Require(context.Resources.Placements.Where(p => layer.ClearanceRules.Any(r => Matches(r, p))).All(p => listedPlacements.Contains(p.PlacementId)),
                "unlisted native foliage owner");
        }
        return (effects.AsReadOnly(), sourceOwners.Count);
    }

    // The released index: solid collision, a roof archetype, and a door tag on the object or its archetype.
    static bool Solid(TileObjectArchetype archetype) => archetype.CollisionKind == TileCollisionKind.Solid;
    static bool Door(TileObject obj, TileObjectArchetype archetype) => Tag(obj.Tags, "door") || Tag(archetype.Tags, "door");
    static bool Tag(IReadOnlyList<string>? tags, string wanted) => tags?.Any(t => string.Equals(t, wanted, StringComparison.OrdinalIgnoreCase)) == true;

    static bool SourceEffect(MapFoliageClearanceKind kind, TileFoliageLayer layer, TileObject obj, TileObjectArchetype archetype) => kind switch
    {
        MapFoliageClearanceKind.Solid => layer.ExcludeSolidObjects && Solid(archetype) && obj.Plane == layer.Plane,
        MapFoliageClearanceKind.RoofAbove => layer.ExcludeIndoors && archetype.IsRoof && obj.Plane > layer.Plane,
        _ => layer.DoorClearance > 0f && Door(obj, archetype) && obj.Plane == layer.Plane,
    };

    static bool Enabled(MapFoliageLayer layer, MapFoliageClearanceKind kind) => kind switch
    {
        MapFoliageClearanceKind.Solid => layer.ExcludeSolidObjects,
        MapFoliageClearanceKind.RoofAbove => layer.ExcludeIndoors,
        _ => layer.DoorClearance > 0f,
    };

    // Compatibility ordinals as the plan rules them: equal for Solid and Door, strictly higher for a roof, and an
    // unmapped seat falls back to canonical identity for Solid and Door and never clears as a roof.
    static bool Applies(int? owner, int? sample, string seat, string support, MapFoliageClearanceKind kind)
    {
        if (owner is null) return kind != MapFoliageClearanceKind.RoofAbove && string.Equals(seat, support, StringComparison.Ordinal);
        if (sample is null) return false;
        return kind == MapFoliageClearanceKind.RoofAbove ? owner > sample : owner == sample;
    }

    static int? Ordinal(MapFoliageLayer layer, string surface) =>
        layer.CompatibilityOrdinals.FirstOrDefault(r => r.SurfaceId == surface)?.Ordinal;

    static bool Matches(MapFoliageClearanceRule rule, MapResolvedPlacement placement) =>
        string.Equals(rule.AssetId, placement.AssetId, StringComparison.Ordinal) &&
        (rule.PlacementTag is not { } tag || placement.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)));

    static string? Seated(MapSupportResult seat) => seat.Status switch
    {
        MapSupportStatus.Supported => seat.Face?.OwnerId,
        MapSupportStatus.LegacyFallback => seat.Compatibility?.Patch.SurfaceId,
        _ => null,
    };

    // The generator's self-check, repeated: each rule or mask kind needs the original object's own property, a solid
    // or roof footprint must cover exactly the original tiles, and a door clears around the original footprint centre.
    static void RequireRule(MapFoliageClearanceKind kind, MapFoliageRect local, MapEffectiveTransform pose, TileRect footprint,
        TileObject obj, TileObjectArchetype archetype, HashSet<string> ids, string id)
    {
        Require(kind switch
        {
            MapFoliageClearanceKind.Solid => Solid(archetype),
            MapFoliageClearanceKind.RoofAbove => archetype.IsRoof,
            _ => Door(obj, archetype),
        }, "clearance rule disagrees with the source collision, roof or door");
        var kernel = new MapResourceTransform(pose);
        (double X, double Z) Point(float x, float z)
        {
            MapExactPoint p = kernel.Point(new(MapExactValue.FromSingle(x), default, MapExactValue.FromSingle(z)));
            return (p.X.ToDouble(), p.Z.ToDouble());
        }
        if (kind == MapFoliageClearanceKind.Door)
        {
            (double x, double z) = Point((local.MinX + local.MaxX) * 0.5f, (local.MinZ + local.MaxZ) * 0.5f);
            float cx = TileWorldSpace.WorldX(footprint.X + footprint.Width * 0.5f, 1f), cz = TileWorldSpace.WorldZ(footprint.Z + footprint.Height * 0.5f, 1f);
            Require(Math.Abs(x - cx) <= 0.00001 && Math.Abs(z - cz) <= 0.00001, "clearance rule disagrees with the source footprint");
        }
        else
        {
            var corners = new[] { Point(local.MinX, local.MinZ), Point(local.MaxX, local.MinZ), Point(local.MaxX, local.MaxZ), Point(local.MinX, local.MaxZ) };
            double minX = TileWorldSpace.WorldX(footprint.X, 1f), maxX = TileWorldSpace.WorldX(footprint.X1, 1f);
            double minZ = TileWorldSpace.WorldZ(footprint.Z1, 1f), maxZ = TileWorldSpace.WorldZ(footprint.Z, 1f);
            Require(Math.Abs(corners.Min(c => c.X) - minX) <= 0.0001 && Math.Abs(corners.Max(c => c.X) - maxX) <= 0.0001 &&
                Math.Abs(corners.Min(c => c.Z) - minZ) <= 0.0001 && Math.Abs(corners.Max(c => c.Z) - maxZ) <= 0.0001,
                "clearance rule disagrees with the source footprint");
        }
        ids.Add(id);
    }

    // A compatibility row's surface must be the released terrain conversion of its own source plane: one patch per source
    // region whose plane is authored, each with the semantic digest of LegacyOracleConverter.ToNative for that region and
    // plane, so heights, cells, flags, cuts and presence all agree.
    void RequireDerived(FoliageSupportRow row)
    {
        var surface = native.Surfaces.Refs.FirstOrDefault(s => s.Id == row.NativeSurfaceId);
        Require(surface is not null && surface.Role == MapSurfaceRole.SupportFloor &&
            surface.Frame.Equals(MapLatticeFrame.ImportedMetreCentimetre), "native support surface does not derive from its source plane");
        var patches = native.Surfaces.Patches.Values.Where(p => p.Key.SurfaceId == row.NativeSurfaceId).ToArray();
        var authored = world.Regions.Values.Where(r => !r.Planes[row.SourcePlane].IsEmpty).Select(r => r.Coord).ToHashSet();
        Require(patches.Length != 0 && authored.All(c => patches.Any(p => p.Key.SlotX == c.Rx && p.Key.SlotZ == c.Rz)),
            "native support surface does not derive from its source plane");
        foreach (var patch in patches)
        {
            var coord = new RegionCoord((int)patch.Key.SlotX, (int)patch.Key.SlotZ);
            Require(patch.Key.SlotX == coord.Rx && patch.Key.SlotZ == coord.Rz && world.GetRegion(coord) is not null,
                "native support surface does not derive from its source plane");
            MapSurfacePatch converted = LegacyOracleConverter.ToNative(world, coord, row.SourcePlane).Patch;
            converted.Key = patch.Key;
            Require(MapSurfaceSemantics.PatchDigest(converted) == MapSurfaceSemantics.PatchDigest(patch),
                "native support surface does not derive from its source plane");
        }
    }

    void RequireMaterials(FoliageLayerRow row, TileFoliageLayer source, MapFoliageLayer layer)
    {
        Require(layer.Materials.OrderBy(m => m.Id).Select(m => (m.Id, m.ResourceId, m.Kind))
            .SequenceEqual(row.Materials.Select(m => (m.SourceMaterialId, m.NativeResourceId, m.Kind))), "foliage material correspondence differs");
        foreach (FoliageMaterialRow material in row.Materials)
        {
            Require(catalogs.Material(material.SourceMaterialId) is { } m && (m.Kind == GroundMaterialKind.Water) == (material.Kind == MapFoliageMaterialKind.Water),
                "foliage material kind differs from the source catalog");
            Require(Closure(() => assets.GetResource(material.NativeResourceId)).Kind == MapResourceKind.Material, "unattested foliage material");
        }
        var visible = new HashSet<ushort>();
        foreach (TileRegion region in world.Regions.Values)
        {
            TilePlaneData plane = region.Planes[source.Plane];
            foreach (ushort id in (plane.Underlay ?? []).Concat(plane.Overlay ?? [])) if (id != 0) visible.Add(id);
        }
        Require(visible.All(id => row.Materials.Any(m => m.SourceMaterialId == id)), "incomplete foliage material correspondence");
    }

    static T Closure<T>(Func<T> read)
    {
        try { return read(); }
        catch (MapDocumentException) { throw new InvalidDataException("unattested foliage closure member"); }
    }

    static bool Same(IEnumerable<string> first, IEnumerable<string> second) =>
        first.ToHashSet(StringComparer.Ordinal).SetEquals(second);
}
