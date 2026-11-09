using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Root foliage's typed World references. Active binding kinds choose the namespace, never matching text.
/// Validation uses already expanded resources and never samples support or expands a second time.</summary>
internal static class MapFoliageReferences
{
    internal static MapFoliageSet Remap(MapFoliageSet set, IReadOnlyDictionary<MapPrefabReference, MapPrefabReference> changes)
    {
        MapPrefabReference Ref(MapPrefabReference value) => value.Scope == MapPrefabReferenceScope.World && changes.TryGetValue(value, out var to) ? to : value;
        string Id(MapPrefabChildKind kind, string id) => Ref(new(MapPrefabReferenceScope.World, kind, id)).Id;
        MapSupportBinding Binding(MapSupportBinding b) => b.Kind switch
        {
            MapSupportBindingKind.Surface when b.SurfaceId is { } s => b with { SurfaceId = Id(MapPrefabChildKind.Surface, s) },
            MapSupportBindingKind.Space when b.SpaceId is { } s => b with { SpaceId = Id(MapPrefabChildKind.Record, s) },
            _ => b,
        };
        MapFoliageLayer Layer(MapFoliageLayer l)
        {
            var metadata = new MapFoliageLayer(l.PayloadVersion, l.Id, Binding(l.Support), l.OriginX, l.OriginZ, l.CellSize,
                l.Width, l.Depth, l.RowDirection, l.Seed, l.Spacing, l.ScaleMin, l.ScaleMax, l.RootOffset, l.Models,
                l.AllowedVisibleMaterials, l.Materials, l.Policy, l.ExcludeIndoors, l.ExcludeSolidObjects, l.DoorClearance, l.EdgeFade,
                l.CompatibilityOrdinals.Select(r => r with { SurfaceId = Id(MapPrefabChildKind.Surface, r.SurfaceId) }).ToArray(), l.ClearanceRules,
                l.ExceptionMasks.Select(m => m with { Owner = Ref(m.Owner), Support = Binding(m.Support) }).ToArray(),
                l.OwnerSupports.Select(o => o with { Owner = Ref(o.Owner), Support = Binding(o.Support) }).ToArray(), l.Chunks);
            return MapFoliageLayer.FromStorage(metadata, l.ChunkDigests, l.Chunks);
        }
        return new(set.PayloadVersion, set.Layers.Select(Layer).ToArray());
    }

    internal static IReadOnlyList<string> ValidateOwners(MapDocument document, MapEffectiveResources resources) =>
        Array.AsReadOnly(ValidateOwnerFindings(document, resources).Select(f => f.Message).ToArray());

    internal static IReadOnlyList<MapDocumentFinding> ValidateOwnerFindings(MapDocument document, MapEffectiveResources resources)
    {
        if (document.Foliage is not { } set) return Array.Empty<MapDocumentFinding>();
        var findings = new List<MapDocumentFinding>();
        var surfaces = resources.SurfaceSource.Surfaces.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var topology = resources.Surfaces.AllRecords().ToArray();
        var records = topology.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var placements = resources.Placements.Select(p => p.PlacementId).ToHashSet(StringComparer.Ordinal);
        var markers = resources.EffectiveMarkers.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var spaces = topology.OfType<MapSpaceDoc>().Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        void Surface(string id, string layerId, string path)
        {
            if (!surfaces.TryGetValue(id, out var surface) || surface.Role != MapSurfaceRole.SupportFloor)
                findings.Add(new(surface is null ? MapDocumentRefusal.ReferenceMissing : MapDocumentRefusal.ReferenceKindMismatch,
                    $"foliage: missing effective support surface '{id}'.")
                {
                    LayerId = layerId,
                    FieldPath = path,
                    SurfaceId = id,
                    SubjectKind = "support surface",
                    EnumType = surface is null ? null : typeof(MapSurfaceRole),
                    ActualInteger = surface is null ? null : (long)surface.Role,
                });
        }
        void Binding(MapSupportBinding b, string layerId, string path)
        {
            if (b.Kind == MapSupportBindingKind.Surface && b.SurfaceId is { } s) Surface(s, layerId, path);
            if (b.Kind == MapSupportBindingKind.Space && (b.SpaceId is null || !spaces.Contains(b.SpaceId)))
                findings.Add(new(b.SpaceId is null ? MapDocumentRefusal.SchemaValueRequired : MapDocumentRefusal.ReferenceMissing,
                    $"foliage: missing effective support space '{b.SpaceId}'.")
                { LayerId = layerId, FieldPath = path, SpaceId = b.SpaceId, SubjectKind = "support space" });
        }
        void Owner(MapPrefabReference o, string layerId, string path)
        {
            bool exists = o.Scope == MapPrefabReferenceScope.World && o.Kind switch
            {
                MapPrefabChildKind.Placement => placements.Contains(o.Id),
                MapPrefabChildKind.Surface => surfaces.ContainsKey(o.Id),
                MapPrefabChildKind.Record => records.ContainsKey(o.Id),
                MapPrefabChildKind.Marker => markers.Contains(o.Id),
                _ => false,
            };
            if (!exists) findings.Add(new(o.Scope != MapPrefabReferenceScope.World ? MapDocumentRefusal.AuthoredOwnershipMismatch :
                o.Kind is not (MapPrefabChildKind.Placement or MapPrefabChildKind.Surface or MapPrefabChildKind.Record or MapPrefabChildKind.Marker)
                    ? MapDocumentRefusal.AuthoredReferenceKindInvalid : MapDocumentRefusal.ReferenceMissing,
                $"foliage: missing effective World {o.Kind} owner '{o.Id}'.")
            {
                LayerId = layerId,
                FieldPath = path,
                EntityId = o.Id,
                ChildKind = o.Kind,
                ReferenceScope = o.Scope,
                ExpectedReferenceScope = MapPrefabReferenceScope.World,
                SubjectKind = "foliage owner",
            });
        }
        foreach (MapFoliageLayer layer in set.Layers)
        {
            Binding(layer.Support, layer.Id, "Support");
            for (int i = 0; i < layer.CompatibilityOrdinals.Count; i++)
                Surface(layer.CompatibilityOrdinals[i].SurfaceId, layer.Id, $"CompatibilityOrdinals[{i}].SurfaceId");
            for (int i = 0; i < layer.ExceptionMasks.Count; i++)
            {
                var mask = layer.ExceptionMasks[i];
                Owner(mask.Owner, layer.Id, $"ExceptionMasks[{i}].Owner");
                Binding(mask.Support, layer.Id, $"ExceptionMasks[{i}].Support");
            }
            for (int i = 0; i < layer.OwnerSupports.Count; i++)
            {
                var owner = layer.OwnerSupports[i];
                Owner(owner.Owner, layer.Id, $"OwnerSupports[{i}].Owner");
                Binding(owner.Support, layer.Id, $"OwnerSupports[{i}].Support");
            }
        }
        return Array.AsReadOnly(findings.ToArray());
    }
}
