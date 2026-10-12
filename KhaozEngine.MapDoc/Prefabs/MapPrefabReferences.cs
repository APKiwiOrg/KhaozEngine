using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>The role a reference occurrence plays at its typed position.</summary>
internal enum MapPrefabOccurrenceRole { RecordId, RecordAnchor, SurfaceText, PatchKey, Vertex, SpaceText, Medium }

/// <summary>One typed reference occurrence. <see cref="Path"/> is the canonical JSON pointer of the referencing string
/// in the codec's canonical bytes. Record and Patch carry the whole record reference or patch key around it.</summary>
internal readonly record struct MapPrefabOccurrence(string Path, MapPrefabChildKind Kind, string Id, MapPrefabReferenceScope? Scope,
    MapPrefabOccurrenceRole Role, MapRecordRef? Record, MapPatchKey? Patch, bool Space);

/// <summary>Maps each typed occurrence of a walked value. Paths given to the map are canonical pointers of the input.</summary>
internal abstract class MapPrefabReferenceMap
{
    internal static readonly MapPrefabReferenceMap Identity = new IdentityMap();

    internal abstract (string Id, MapPrefabReferenceScope? Scope) Text(string path, MapPrefabChildKind kind, string id, bool space);

    internal virtual (MapRecordRef Value, MapPrefabReferenceScope? IdScope, MapPrefabReferenceScope? AnchorScope) Record(
        string path, MapRecordRef value, bool space)
    {
        var (id, idScope) = Text(path + "/id", MapPrefabChildKind.Record, value.Id, space);
        var (surface, anchorScope) = Text(path + "/anchor/surfaceId", MapPrefabChildKind.Surface, value.Anchor.SurfaceId, false);
        return (new(id, value.Anchor with { SurfaceId = surface }), idScope, anchorScope);
    }

    sealed class IdentityMap : MapPrefabReferenceMap
    {
        internal override (string Id, MapPrefabReferenceScope? Scope) Text(string path, MapPrefabChildKind kind, string id, bool space) => (id, null);
    }
}

/// <summary>Typed reference positions, scope validation and remapping. The closed slot grammar is the canonical JSON
/// pointer this typed field visitor emits for each reference occurrence. Paths are never evaluated against
/// arbitrary JSON, and scope is never chosen by matching text.</summary>
public static class MapPrefabReferences
{
    internal const string Parent = "$parent";

    /// <summary>Replaces every Local occurrence with its bound world ID, substitutes <paramref name="parentSpace"/> for
    /// Local $parent and keeps World references exactly. Definition IDs take their binding. Tags, names and asset IDs
    /// are never replaced. The result carries World slots only.</summary>
    public static MapPrefabAsset Remap(MapPrefabAsset asset, IReadOnlyDictionary<MapPrefabChildKey, string> bindings, MapRecordRef? parentSpace)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(bindings);
        MapPrefabValidation.RequireAsset(asset);
        string World(MapPrefabChildKind kind, string id) => bindings.TryGetValue(new(kind, id), out string? world) && !string.IsNullOrWhiteSpace(world)
            ? world : throw new MapDocumentException($"No binding for local {kind} '{id}'.");
        MapPrefabReference Annotation(MapPrefabReference r) => r.Scope == MapPrefabReferenceScope.World ? r : new(MapPrefabReferenceScope.World, r.Kind, World(r.Kind, r.Id));
        var children = new List<MapPrefabChild>();
        foreach (MapPrefabChild child in asset.Children)
        {
            var map = new LocalToWorld(child.References, World, parentSpace);
            var (walked, occurrences) = MapPrefabReferenceWalker.Child(child, map);
            string id = World(child.Key.Kind, child.Key.Key);
            MapPrefabChild defined = walked switch
            {
                MapPrefabPlacementChild p => p with { Value = MapPrefabReferenceWalker.Copy(p.Value, id, p.Value.SupportBinding) },
                MapPrefabSurfaceChild s => s with { Surface = s.Surface with { Id = id } },
                MapPrefabRecordChild r => r with { Value = r.Value with { Id = id } },
                MapPrefabMediumChild m => m with { Value = m.Value with { Key = id } },
                MapPrefabWaterChild w => w with { Value = w.Value with { Id = id } },
                MapPrefabLightChild l => l with { Value = l.Value with { Id = id, Owner = l.Value.Owner is { } owner ? Annotation(owner) : null } },
                MapPrefabMarkerChild m => m with { Value = m.Value with { Id = id, Tags = m.Value.Tags.ToArray() } },
                MapPrefabRoofChild r => r with
                {
                    Value = r.Value with { Id = id, Roof = Annotation(r.Value.Roof), Rooms = r.Value.Rooms.Select(Annotation).ToArray(), Groups = r.Value.Groups.Select(Annotation).ToArray() },
                },
                MapPrefabRoofGroupChild g => g with { Value = g.Value with { Id = id, Rooms = g.Value.Rooms.Select(Annotation).ToArray() } },
                _ => walked,
            };
            children.Add(defined with { References = Slots(occurrences, MapPrefabReferenceScope.World) });
        }
        return asset with
        {
            Children = children,
            Roofs = asset.Roofs.Select(Annotation).ToArray(),
            MaterializedAttachments = asset.MaterializedAttachments.Select(row => row with
            {
                Placement = Annotation(row.Placement),
                Children = row.Children.Select(c => c with { Child = Annotation(c.Child) }).ToArray(),
            }).ToArray(),
        };
    }

    /// <summary>Renames one World identity of <paramref name="kind"/> in every typed World reference held by the
    /// document's prefab, building-intent and standalone-transform roots. Local occurrences and plain text are
    /// untouched. The input document is not modified.</summary>
    public static MapDocument RemapWorld(MapDocument document, MapPrefabChildKind kind, string oldId, string newId)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newId);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        string Id(MapPrefabChildKind k, string id) => k == kind && id == oldId ? newId : id;
        MapPrefabReference Ref(MapPrefabReference r) => r.Scope == MapPrefabReferenceScope.World ? r with { Id = Id(r.Kind, r.Id) } : r;
        MapRecordRef Record(MapRecordRef r) => new(Id(MapPrefabChildKind.Record, r.Id), Key(r.Anchor));
        MapPatchKey Key(MapPatchKey k) => k with { SurfaceId = Id(MapPrefabChildKind.Surface, k.SurfaceId) };
        MapDocument copy = MapTiledFile.GlobalsOnly(document);
        copy.Placements = document.Placements.ToList();
        copy.Spawns = document.Spawns.ToList();
        copy.PlayerSpawns = document.PlayerSpawns.ToList();
        copy.TerrainOverrides = document.TerrainOverrides;
        copy.Surfaces = document.Surfaces.Clone();
        copy.Tiles = document.Tiles;
        if (copy.Prefabs is { } set)
            copy.Prefabs = new(set.PayloadVersion, set.ExpansionVersion,
                set.Instances.Select(i => i with
                {
                    ParentSpace = i.ParentSpace is { } parent ? Record(parent) : null,
                    Overrides = i.Overrides.Select(o => WorldOverride(o, kind, oldId, newId)).ToArray(),
                }).ToArray(),
                set.Transfers.Select(t => t with { Binding = t.Binding with { WorldId = Id(t.Binding.Child.Kind, t.Binding.WorldId) } }).ToArray())
            {
                Attachments = set.Attachments.Select(a => a with
                {
                    PlacementId = Id(MapPrefabChildKind.Placement, a.PlacementId),
                    Children = a.Children.Select(c => c with { WorldId = Id(c.LocalChild.Kind, c.WorldId), Anchor = c.Anchor is { } anchor ? Key(anchor) : null }).ToArray(),
                }).ToArray(),
            };
        if (copy.BuildingIntent is { } intent)
            copy.BuildingIntent = new(intent.PayloadVersion,
                intent.RoofLinks.Select(r => r with { Roof = Ref(r.Roof), Rooms = r.Rooms.Select(Ref).ToArray(), Groups = r.Groups.Select(Ref).ToArray() }).ToArray(),
                intent.RoofGroups.Select(g => g with { Rooms = g.Rooms.Select(Ref).ToArray() }).ToArray(),
                intent.Lights.Select(l => l with { Owner = l.Owner is { } owner ? Ref(owner) : null }).ToArray(),
                intent.Markers.ToArray())
            { Roofs = intent.Roofs.Select(Ref).ToArray(), Storeys = intent.Storeys.ToArray() };
        if (copy.NativeResourceTransforms is { } poses)
            copy.NativeResourceTransforms = poses.Select(p => p with { OwnerId = Id(p.Kind, p.OwnerId) }).ToArray();
        return copy;
    }

    /// <summary>Every typed reference occurrence of the asset: slot targets in canonical child and path order, then
    /// annotation references, the roof inventory and materialized rows.</summary>
    public static IReadOnlyList<MapPrefabReference> References(MapPrefabAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var result = new List<MapPrefabReference>();
        foreach (MapPrefabChild child in asset.Children.OrderBy(c => c.Key.Kind.ToString(), StringComparer.Ordinal).ThenBy(c => c.Key.Key, StringComparer.Ordinal))
        {
            result.AddRange(child.References.OrderBy(s => s.Path, StringComparer.Ordinal).Select(s => s.Target));
            result.AddRange(Annotations(child));
        }
        result.AddRange(asset.Roofs);
        foreach (MapPrefabLocalMaterialization row in asset.MaterializedAttachments)
        {
            result.Add(row.Placement);
            result.AddRange(row.Children.Select(c => c.Child));
        }
        return result;
    }

    internal static IEnumerable<MapPrefabReference> Annotations(MapPrefabChild child)
    {
        switch (child)
        {
            case MapPrefabLightChild { Value.Owner: { } owner }: yield return owner; break;
            case MapPrefabRoofChild roof:
                yield return roof.Value.Roof;
                foreach (var r in roof.Value.Rooms) yield return r;
                foreach (var g in roof.Value.Groups) yield return g;
                break;
            case MapPrefabRoofGroupChild group:
                foreach (var r in group.Value.Rooms) yield return r;
                break;
        }
    }

    /// <summary>Requires exactly one consistent scoped slot per typed occurrence and valid Local targets.</summary>
    internal static void RequireSlots(IReadOnlyList<MapPrefabReferenceSlot> slots, IReadOnlyList<MapPrefabOccurrence> occurrences,
        IMapPrefabTargets targets, MapTransform? recordPose, string owner)
    {
        if (slots is null) Fail($"{owner}: reference slots must not be null.");
        var byPath = new Dictionary<string, MapPrefabReferenceSlot>(StringComparer.Ordinal);
        foreach (MapPrefabReferenceSlot slot in slots)
        {
            if (slot?.Path is null || slot.Target is null) Fail($"{owner}: reference slots must not be null.");
            if (!byPath.TryAdd(slot.Path, slot)) Fail($"{owner}: duplicate reference slot '{slot.Path}'.");
        }
        if (byPath.Count != occurrences.Count) Fail($"{owner}: every reference occurrence requires exactly one scoped slot.");
        foreach (MapPrefabOccurrence occurrence in occurrences)
        {
            if (!byPath.TryGetValue(occurrence.Path, out MapPrefabReferenceSlot? slot) || slot.Target.Kind != occurrence.Kind || slot.Target.Id != occurrence.Id)
                Fail($"{owner}: unsupported or inconsistent reference slot at '{occurrence.Path}'.");
            MapPrefabReference target = slot.Target;
            if (!Enum.IsDefined(target.Scope)) Fail($"{owner}: unsupported reference scope.");
            if (occurrence.Role == MapPrefabOccurrenceRole.PatchKey && target.Scope != MapPrefabReferenceScope.Local &&
                occurrence.Path.StartsWith("/patches/", StringComparison.Ordinal) && occurrence.Path.EndsWith("/key/surfaceId", StringComparison.Ordinal))
                Fail($"{owner}: a patch key names its own local surface, at '{occurrence.Path}'.");
            if (occurrence.Role == MapPrefabOccurrenceRole.RecordId &&
                (!byPath.TryGetValue(occurrence.Path[..^"/id".Length] + "/anchor/surfaceId", out var anchorSlot) || anchorSlot.Target.Scope != target.Scope))
                Fail($"{owner}: a record reference at '{occurrence.Path}' must use one scope for its ID and anchor.");
            if (occurrence.Id == Parent || occurrence.Record?.Id == Parent && occurrence.Role == MapPrefabOccurrenceRole.RecordAnchor)
            {
                bool record = occurrence.Role is MapPrefabOccurrenceRole.RecordId or MapPrefabOccurrenceRole.RecordAnchor;
                if (target.Scope != MapPrefabReferenceScope.Local || !occurrence.Space ||
                    record && occurrence.Record != new MapRecordRef(Parent, new(Parent, 0, 0)) ||
                    !record && occurrence.Role != MapPrefabOccurrenceRole.SpaceText)
                    Fail($"{owner}: $parent is valid only as a Local parent or space reference, at '{occurrence.Path}'.");
                continue;
            }
            if (target.Scope == MapPrefabReferenceScope.World) continue;
            if (!targets.Has(target.Kind, target.Id)) Fail($"{owner}: undeclared local {target.Kind} '{target.Id}' at '{occurrence.Path}'.");
            if (occurrence.Role == MapPrefabOccurrenceRole.RecordId && targets.Anchor(target.Id) != occurrence.Record!.Anchor)
                Fail($"{owner}: local record reference '{target.Id}' must name exactly its declared anchor.");
            if (occurrence.Role is MapPrefabOccurrenceRole.PatchKey or MapPrefabOccurrenceRole.RecordAnchor && !targets.HasPatch(occurrence.Patch!.Value))
                Fail($"{owner}: local patch '{occurrence.Patch!.Value}' is not declared by surface '{target.Id}'.");
            if (recordPose is { } pose && target.Kind is MapPrefabChildKind.Record or MapPrefabChildKind.Surface && targets.Pose(target.Kind, target.Id) != pose)
                Fail($"{owner}: local anchor and geometry poses must match the record pose.");
        }
    }

    internal static MapPrefabReferenceSlot[] Slots(IEnumerable<MapPrefabOccurrence> occurrences, MapPrefabReferenceScope? scope = null) =>
        occurrences.Select(o => new MapPrefabReferenceSlot(o.Path, new(scope ?? o.Scope ?? throw new MapDocumentException("Unscoped reference occurrence."), o.Kind, o.Id)))
            .OrderBy(s => s.Path, StringComparer.Ordinal).ToArray();

    static MapPrefabOverride WorldOverride(MapPrefabOverride value, MapPrefabChildKind kind, string oldId, string newId) => value switch
    {
        MapPrefabRemoveOverride { Previous: { } previous } r => r with { Previous = WorldOverride(previous, kind, oldId, newId) },
        MapPrefabReplaceOverride r => r with { Value = WorldChild(r.Value, kind, oldId, newId) },
        MapPrefabAddOverride a => a with { Value = WorldChild(a.Value, kind, oldId, newId) },
        _ => value,
    };

    static MapPrefabChild WorldChild(MapPrefabChild child, MapPrefabChildKind kind, string oldId, string newId)
    {
        var (walked, occurrences) = MapPrefabReferenceWalker.Child(child, new WorldRename(child.References, kind, oldId, newId));
        MapPrefabReference Ref(MapPrefabReference r) => r.Scope == MapPrefabReferenceScope.World && r.Kind == kind && r.Id == oldId ? r with { Id = newId } : r;
        MapPrefabChild renamed = walked switch
        {
            MapPrefabLightChild l => l with { Value = l.Value with { Owner = l.Value.Owner is { } owner ? Ref(owner) : null } },
            MapPrefabRoofChild r => r with { Value = r.Value with { Roof = Ref(r.Value.Roof), Rooms = r.Value.Rooms.Select(Ref).ToArray(), Groups = r.Value.Groups.Select(Ref).ToArray() } },
            MapPrefabRoofGroupChild g => g with { Value = g.Value with { Rooms = g.Value.Rooms.Select(Ref).ToArray() } },
            _ => walked,
        };
        return renamed with { References = Slots(occurrences) };
    }

    static Dictionary<string, MapPrefabReferenceSlot> ByPath(IReadOnlyList<MapPrefabReferenceSlot> slots) =>
        slots.ToDictionary(s => s.Path, StringComparer.Ordinal);

    static MapPrefabReferenceSlot SlotAt(Dictionary<string, MapPrefabReferenceSlot> slots, string path) =>
        slots.TryGetValue(path, out MapPrefabReferenceSlot? slot) ? slot : throw new MapDocumentException($"Missing reference slot '{path}'.");

    sealed class LocalToWorld(IReadOnlyList<MapPrefabReferenceSlot> slots, Func<MapPrefabChildKind, string, string> world, MapRecordRef? parent)
        : MapPrefabReferenceMap
    {
        readonly Dictionary<string, MapPrefabReferenceSlot> _slots = ByPath(slots);

        internal override (string Id, MapPrefabReferenceScope? Scope) Text(string path, MapPrefabChildKind kind, string id, bool space)
        {
            if (SlotAt(_slots, path).Target.Scope == MapPrefabReferenceScope.World) return (id, MapPrefabReferenceScope.World);
            if (id != Parent) return (world(kind, id), MapPrefabReferenceScope.World);
            return ((parent ?? throw new MapDocumentException("Local $parent requires a supplied ParentSpace.")).Id, MapPrefabReferenceScope.World);
        }

        internal override (MapRecordRef Value, MapPrefabReferenceScope? IdScope, MapPrefabReferenceScope? AnchorScope) Record(string path, MapRecordRef value, bool space)
        {
            if (value.Id != Parent || SlotAt(_slots, path + "/id").Target.Scope != MapPrefabReferenceScope.Local) return base.Record(path, value, space);
            return (parent ?? throw new MapDocumentException("Local $parent requires a supplied ParentSpace."), MapPrefabReferenceScope.World, MapPrefabReferenceScope.World);
        }
    }

    sealed class WorldRename(IReadOnlyList<MapPrefabReferenceSlot> slots, MapPrefabChildKind kind, string oldId, string newId) : MapPrefabReferenceMap
    {
        readonly Dictionary<string, MapPrefabReferenceSlot> _slots = ByPath(slots);

        internal override (string Id, MapPrefabReferenceScope? Scope) Text(string path, MapPrefabChildKind k, string id, bool space)
        {
            MapPrefabReferenceScope scope = SlotAt(_slots, path).Target.Scope;
            return (scope == MapPrefabReferenceScope.World && k == kind && id == oldId ? newId : id, scope);
        }
    }

    [DoesNotReturn]
    static void Fail(string message) => throw new MapDocumentException(message);
}

/// <summary>Local targets visible to one validated scope: an asset's children or a support resource's own records.</summary>
internal interface IMapPrefabTargets
{
    bool Has(MapPrefabChildKind kind, string id);
    bool HasPatch(MapPatchKey key);
    MapPatchKey? Anchor(string recordId);
    MapTransform? Pose(MapPrefabChildKind kind, string id);
}

/// <summary>The typed field visitor. Each function returns the mapped value and its occurrences with pointers relative
/// to that value, in the output's canonical order. Lists the codec sorts are sorted here with the codec's keys.</summary>
internal static class MapPrefabReferenceWalker
{
    internal static (MapPrefabChild Child, IReadOnlyList<MapPrefabOccurrence> Occurrences) Child(MapPrefabChild child, MapPrefabReferenceMap map)
    {
        switch (child)
        {
            case MapPrefabPlacementChild p when p.Value.SupportBinding is { } binding:
                var occurrences = new List<MapPrefabOccurrence>();
                MapSupportBinding mapped = binding;
                if (binding.Kind == MapSupportBindingKind.Surface && binding.SurfaceId is { } surface)
                {
                    var (id, o) = Text(surface, "/value/supportBinding/surfaceId", map, MapPrefabChildKind.Surface, MapPrefabOccurrenceRole.SurfaceText, false);
                    mapped = binding with { SurfaceId = id }; occurrences.AddRange(Prefix("/value/supportBinding/surfaceId", o));
                }
                else if (binding.Kind == MapSupportBindingKind.Space && binding.SpaceId is { } space)
                {
                    var (id, o) = Text(space, "/value/supportBinding/spaceId", map, MapPrefabChildKind.Record, MapPrefabOccurrenceRole.SpaceText, true);
                    mapped = binding with { SpaceId = id }; occurrences.AddRange(Prefix("/value/supportBinding/spaceId", o));
                }
                return (p with { Value = Copy(p.Value, p.Value.Id, mapped) }, occurrences);
            case MapPrefabSurfaceChild s:
                var (surfaceRef, so) = SurfaceRef(s.Surface, "/surface", map);
                var (patches, po) = List(s.Patches, "/patches", (patch, path) => Patch(patch, path, map), ByKey);
                return (s with { Surface = surfaceRef, Patches = patches }, Prefix("/surface", so).Concat(Prefix("/patches", po)).ToArray());
            case MapPrefabRecordChild r:
                var (anchor, ao) = Key(r.Anchor, "/anchor", map);
                var (value, vo) = Record(r.Value, "/value", map);
                return (r with { Anchor = anchor, Value = value }, Prefix("/anchor", ao).Concat(Prefix("/value", vo)).ToArray());
            case MapPrefabWaterChild w:
                var (medium, mo) = Text(w.Value.Medium, "/value/medium", map, MapPrefabChildKind.WaterMedium, MapPrefabOccurrenceRole.Medium, false);
                var (covers, co) = List(w.Value.Covers, "/value/covers", (cover, path) => Ref(cover, path, map, false), null);
                return (w with { Value = w.Value with { Medium = medium, Covers = covers } },
                    Prefix("/value/medium", mo).Concat(Prefix("/value/covers", co)).ToArray());
            default:
                return (child, Array.Empty<MapPrefabOccurrence>());
        }
    }

    internal static (MapSupportResource Resource, IReadOnlyList<MapPrefabOccurrence> Occurrences) Resource(MapSupportResource resource, MapPrefabReferenceMap map)
    {
        var (refs, ro) = List(resource.Surfaces.Refs, "/surfaces", (s, path) => SurfaceRef(s, path, map), BySurfaceId);
        var (patches, po) = List(resource.Surfaces.Patches.Values.ToArray(), "/patches", (patch, path) => Patch(patch, path, map), ByKey);
        var occurrences = Prefix("/surfaces", ro).Concat(Prefix("/patches", po)).ToList();
        MapRecordRef? space = null;
        if (resource.Space is { } value)
        {
            var (mapped, so) = Ref(value, "/space", map, true);
            space = mapped; occurrences.AddRange(Prefix("/space", so));
        }
        var surfaces = new MapSurfaceSet();
        surfaces.Refs.AddRange(refs);
        foreach (MapSurfacePatch patch in patches)
            if (!surfaces.Patches.TryAdd(patch.Key, patch)) throw new MapDocumentException("Support resource patch keys must stay distinct.");
        return (resource with { Surfaces = surfaces, Space = space }, occurrences);
    }

    internal static MapPlacement Copy(MapPlacement p, string id, MapSupportBinding? binding) => new()
    {
        Id = id,
        Kind = p.Kind,
        X = p.X,
        Y = p.Y,
        Z = p.Z,
        Yaw = p.Yaw,
        Scale = p.Scale,
        Tags = p.Tags?.ToList()!,
        NumericId = p.NumericId,
        AssetId = p.AssetId,
        DisplayName = p.DisplayName,
        SupportBinding = binding,
    };

    static (MapTopologyRecord, IReadOnlyList<MapPrefabOccurrence>) Record(MapTopologyRecord record, string path, MapPrefabReferenceMap map)
    {
        var o = new List<MapPrefabOccurrence>();
        T Add<T>(string name, (T Value, IReadOnlyList<MapPrefabOccurrence> Occurrences) walked)
        {
            o.AddRange(Prefix(name, walked.Occurrences));
            return walked.Value;
        }
        MapRecordRef RefAt(string name, MapRecordRef value, bool space) => Add(name, Ref(value, path + name, map, space));
        MapRecordRef? Optional(string name, MapRecordRef? value, bool space) => value is null ? null : RefAt(name, value, space);
        MapRecordRef[] Refs(string name, IReadOnlyList<MapRecordRef> values) =>
            Add(name, List(values, path + name, (r, p) => Ref(r, p, map, false), ByRef));
        MapBoundaryRef[] Boundaries(string name, IReadOnlyList<MapBoundaryRef> values) => Add(name, List(values, path + name,
            (b, p) => { var (r, ro) = Ref(b.Record, p + "/record", map, false); return (b with { Record = r }, Prefix("/record", ro)); }, ByBoundary));
        MapLatticeVertex VertexAt(string name, MapLatticeVertex v) => Add(name, Vertex(v, path + name, map));
        MapPatchKey KeyAt(string name, MapPatchKey k) => Add(name, Key(k, path + name, map));
        MapBoundRef Bound(string name, MapBoundRef bound)
        {
            string? surface = bound.SurfaceId is { } id
                ? Add(name + "/surfaceId", Text(id, path + name + "/surfaceId", map, MapPrefabChildKind.Surface, MapPrefabOccurrenceRole.SurfaceText, false)) : null;
            return bound with { SurfaceId = surface, Opening = Optional(name + "/opening", bound.Opening, false) };
        }
        MapSurfaceEdgeRef Edge(string name, MapSurfaceEdgeRef edge) =>
            new(KeyAt(name + "/patch", edge.Patch), VertexAt(name + "/from", edge.From), VertexAt(name + "/to", edge.To));
        MapTopologyRecord result = record switch
        {
            MapSurfaceSeam s => s with
            {
                First = Edge("/first", s.First),
                Second = Edge("/second", s.Second),
                Pairs = Add("/pairs", List(s.Pairs, path + "/pairs", (pair, p) =>
                {
                    var (a, ao) = Vertex(pair.First, p + "/first", map);
                    var (b, bo) = Vertex(pair.Second, p + "/second", map);
                    return ((a, b), Prefix("/first", ao).Concat(Prefix("/second", bo)).ToArray());
                }, ByPair)),
            },
            MapBoundaryChain c => c with
            {
                SourcePatch = c.SourcePatch is { } source ? KeyAt("/sourcePatch", source) : null,
                Vertices = Add("/vertices", List(c.Vertices, path + "/vertices", (v, p) =>
                { var (vertex, vo) = Vertex(v.Vertex, p + "/vertex", map); return (v with { Vertex = vertex }, Prefix("/vertex", vo)); }, null)),
            },
            MapWallStrip w => w with { LowerChain = RefAt("/lowerChain", w.LowerChain, false), UpperChain = RefAt("/upperChain", w.UpperChain, false) },
            MapCavePortal p => p with
            {
                FromSpace = RefAt("/fromSpace", p.FromSpace, true),
                ToSpace = RefAt("/toSpace", p.ToSpace, true),
                Interval = Add("/interval", List(p.Interval, path + "/interval", (v, q) => Vertex(v, q, map), null)),
                BandBottom = RefAt("/bandBottom", p.BandBottom, false),
                BandTop = Optional("/bandTop", p.BandTop, false),
            },
            MapHorizontalOpening h => h with { Patch = KeyAt("/patch", h.Patch) },
            MapVerticalLink l => l with
            {
                UpperSpace = RefAt("/upperSpace", l.UpperSpace, true),
                LowerSpace = RefAt("/lowerSpace", l.LowerSpace, true),
                Openings = Refs("/openings", l.Openings),
                Portals = Refs("/portals", l.Portals),
                GeometryOwners = Refs("/geometryOwners", l.GeometryOwners),
            },
            MapSpaceDoc s => s with
            {
                Parent = Optional("/parent", s.Parent, true),
                AliasOf = Optional("/aliasOf", s.AliasOf, false),
                DomainTags = s.DomainTags.ToArray(),
                Walls = Boundaries("/walls", s.Walls),
                Portals = Boundaries("/portals", s.Portals),
                Links = Refs("/links", s.Links),
            },
            MapSpaceFootprint f => f with
            {
                Space = RefAt("/space", f.Space, true),
                Lattice = KeyAt("/lattice", f.Lattice),
                SlotCells = f.SlotCells.ToArray(),
                Lower = Bound("/lower", f.Lower),
                Upper = Bound("/upper", f.Upper),
            },
            _ => throw new MapDocumentException("Unknown topology record type."),
        };
        return (result, o);
    }

    static (MapSurfaceRef, IReadOnlyList<MapPrefabOccurrence>) SurfaceRef(MapSurfaceRef surface, string path, MapPrefabReferenceMap map)
    {
        var o = new List<MapPrefabOccurrence>();
        string? paint = null;
        if (surface.PaintTargetSurfaceId is { } target)
        {
            var (id, po) = Text(target, path + "/paintTargetSurfaceId", map, MapPrefabChildKind.Surface, MapPrefabOccurrenceRole.SurfaceText, false);
            paint = id; o.AddRange(Prefix("/paintTargetSurfaceId", po));
        }
        MapIndoorSpan? span = null;
        if (surface.IndoorSpan is { } indoor)
        {
            var (parent, io) = Ref(indoor.ParentSpace, path + "/indoorSpan/parentSpace", map, true);
            span = indoor with { ParentSpace = parent, DomainTags = indoor.DomainTags.ToArray() }; o.AddRange(Prefix("/indoorSpan/parentSpace", io));
        }
        return (surface with { PaintTargetSurfaceId = paint, IndoorSpan = span }, o);
    }

    static (MapSurfacePatch, IReadOnlyList<MapPrefabOccurrence>) Patch(MapSurfacePatch patch, string path, MapPrefabReferenceMap map)
    {
        MapSurfacePatch copy = patch.Clone();
        var (key, ko) = Key(patch.Key, path + "/key", map);
        var (dependencies, dO) = List(patch.CornerDependencies, path + "/cornerDependencies", (d, p) =>
        {
            var (owner, oo) = Key(d.Owner.Patch, p + "/owner/patch", map);
            return (d with { Owner = d.Owner with { Patch = owner } }, Prefix("/owner/patch", oo));
        }, ByCorner);
        var (records, ro) = List(patch.Records, path + "/records", (r, p) => Record(r, p, map), ById);
        copy.Key = key;
        copy.CornerDependencies.Clear(); copy.CornerDependencies.AddRange(dependencies);
        copy.Records.Clear(); copy.Records.AddRange(records);
        return (copy, Prefix("/key", ko).Concat(Prefix("/cornerDependencies", dO)).Concat(Prefix("/records", ro)).ToArray());
    }

    static (MapRecordRef, IReadOnlyList<MapPrefabOccurrence>) Ref(MapRecordRef value, string path, MapPrefabReferenceMap map, bool space)
    {
        var (mapped, idScope, anchorScope) = map.Record(path, value, space);
        return (mapped, new MapPrefabOccurrence[]
        {
            new("/id", MapPrefabChildKind.Record, mapped.Id, idScope, MapPrefabOccurrenceRole.RecordId, mapped, mapped.Anchor, space),
            new("/anchor/surfaceId", MapPrefabChildKind.Surface, mapped.Anchor.SurfaceId, anchorScope, MapPrefabOccurrenceRole.RecordAnchor, mapped, mapped.Anchor, space),
        });
    }

    static (MapPatchKey, IReadOnlyList<MapPrefabOccurrence>) Key(MapPatchKey value, string path, MapPrefabReferenceMap map)
    {
        var (id, scope) = map.Text(path + "/surfaceId", MapPrefabChildKind.Surface, value.SurfaceId, false);
        MapPatchKey key = value with { SurfaceId = id };
        return (key, new MapPrefabOccurrence[] { new("/surfaceId", MapPrefabChildKind.Surface, id, scope, MapPrefabOccurrenceRole.PatchKey, null, key, false) });
    }

    static (MapLatticeVertex, IReadOnlyList<MapPrefabOccurrence>) Vertex(MapLatticeVertex value, string path, MapPrefabReferenceMap map)
    {
        var (id, scope) = map.Text(path + "/surfaceId", MapPrefabChildKind.Surface, value.SurfaceId, false);
        return (value with { SurfaceId = id }, new MapPrefabOccurrence[]
        { new("/surfaceId", MapPrefabChildKind.Surface, id, scope, MapPrefabOccurrenceRole.Vertex, null, null, false) });
    }

    static (string, IReadOnlyList<MapPrefabOccurrence>) Text(string value, string path, MapPrefabReferenceMap map, MapPrefabChildKind kind,
        MapPrefabOccurrenceRole role, bool space)
    {
        var (id, scope) = map.Text(path, kind, value, space);
        return (id, new MapPrefabOccurrence[] { new("", kind, id, scope, role, null, null, space) });
    }

    // Input elements take canonical input pointers. Output elements are reordered by the same codec key.
    static (T[] Values, IReadOnlyList<MapPrefabOccurrence> Occurrences) List<T>(IReadOnlyList<T> values, string path,
        Func<T, string, (T, IReadOnlyList<MapPrefabOccurrence>)> walk, IComparer<T>? order)
    {
        T[] input = order is null ? values.ToArray() : values.OrderBy(v => v, order).ToArray();
        var output = input.Select((value, i) => walk(value, path + "/" + i)).ToArray();
        if (order is not null) output = output.OrderBy(o => o.Item1, order).ToArray();
        return (output.Select(o => o.Item1).ToArray(), output.SelectMany((o, i) => Prefix("/" + i, o.Item2)).ToArray());
    }

    static MapPrefabOccurrence[] Prefix(string segment, IEnumerable<MapPrefabOccurrence> occurrences) =>
        occurrences.Select(o => o with { Path = segment + o.Path }).ToArray();

    static readonly IComparer<MapPatchKey> PatchKeyOrder = Comparer<MapPatchKey>.Default;
    static readonly IComparer<MapSurfacePatch> ByKey = Comparer<MapSurfacePatch>.Create((a, b) => PatchKeyOrder.Compare(a.Key, b.Key));
    static readonly IComparer<MapSurfaceRef> BySurfaceId = Comparer<MapSurfaceRef>.Create((a, b) => string.CompareOrdinal(a.Id, b.Id));
    static readonly IComparer<MapTopologyRecord> ById = Comparer<MapTopologyRecord>.Create((a, b) => string.CompareOrdinal(a.Id, b.Id));
    static readonly IComparer<MapCornerDependency> ByCorner = Comparer<MapCornerDependency>.Create((a, b) =>
        a.CornerZ != b.CornerZ ? a.CornerZ.CompareTo(b.CornerZ) : a.CornerX.CompareTo(b.CornerX));
    static readonly IComparer<MapRecordRef> ByRef = Comparer<MapRecordRef>.Create((a, b) =>
    {
        int id = string.CompareOrdinal(a.Id, b.Id);
        return id != 0 ? id : PatchKeyOrder.Compare(a.Anchor, b.Anchor);
    });
    static readonly IComparer<MapBoundaryRef> ByBoundary = Comparer<MapBoundaryRef>.Create((a, b) =>
    {
        int record = ByRef.Compare(a.Record, b.Record);
        return record != 0 ? record : a.Side.CompareTo(b.Side);
    });
    static readonly IComparer<(MapLatticeVertex First, MapLatticeVertex Second)> ByPair = Comparer<(MapLatticeVertex First, MapLatticeVertex Second)>.Create((a, b) =>
    {
        int c = string.CompareOrdinal(a.First.SurfaceId, b.First.SurfaceId);
        if (c == 0) c = a.First.Address.CompareTo(b.First.Address);
        if (c == 0) c = string.CompareOrdinal(a.Second.SurfaceId, b.Second.SurfaceId);
        return c != 0 ? c : a.Second.Address.CompareTo(b.Second.Address);
    });
}
