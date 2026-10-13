using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Editing;

/// <summary>Extracts selected authored definitions and precise captured attachments into one detached prefab payload.</summary>
public static class MapPrefabExtraction
{
    public static MapPrefabAsset Extract(MapDocument document, MapAssetClosure assets, IReadOnlyList<MapPrefabReference> selected,
        MapTransform origin, string assetId)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        var originFrame = MapEffectiveTransform.From(origin);
        var resources = MapPrefabEditPreparation.Begin(document, assets);
        var snapshot = MapPrefabEditPreparation.Snapshot(document, assets, resources);
        var keys = new HashSet<MapPrefabChildKey>();
        foreach (var reference in selected)
        {
            if (reference is null || reference.Scope != MapPrefabReferenceScope.World ||
                !keys.Add(new(reference.Kind, reference.Id))) throw new MapDocumentException("Selection requires distinct typed World references.");
            if (!snapshot.ContainsKey(new(reference.Kind, reference.Id))) throw new MapDocumentException("Selection names an absent effective child.");
        }
        if (keys.Count == 0) throw new MapDocumentException("Cannot extract an empty selection.");
        RequireCompleteSelection(snapshot, keys, resources);
        var rows = resources.Attachments.Where(r => !r.Retired && keys.Contains(new(MapPrefabChildKind.Placement, r.PlacementId))).ToArray();
        foreach (var row in rows)
            foreach (var child in row.Children)
                if (!keys.Contains(new(child.LocalChild.Kind, child.WorldId)))
                    throw new MapDocumentException("A selected materialized owner requires every live associated child.");
        var bindings = (document.Prefabs?.Instances ?? []).SelectMany(i => i.Bindings.Entries)
            .Concat((document.Prefabs?.Transfers ?? []).Select(t => t.Binding)).ToArray();
        var names = keys.ToDictionary(k => k, k => bindings.FirstOrDefault(b => b.Child.Kind == k.Kind && b.WorldId == k.Key)?.Child.Key ?? k.Key);
        foreach (var group in names.GroupBy(p => (p.Key.Kind, p.Value)).Where(g => g.Count() > 1).ToArray())
            foreach (var pair in group) names[pair.Key] = pair.Key.Key;
        if (names.GroupBy(p => (p.Key.Kind, p.Value)).Any(g => g.Count() > 1)) throw new MapDocumentException("Selected local keys collide.");
        var map = new ExtractReferences(names);
        MapPrefabReference Reference(MapPrefabReference r) => names.TryGetValue(new(r.Kind, r.Id), out var local)
            ? new(MapPrefabReferenceScope.Local, r.Kind, local) : r with { Scope = MapPrefabReferenceScope.World };
        var captured = rows.SelectMany(r => r.Children).Select(c => new MapPrefabChildKey(c.LocalChild.Kind, c.WorldId)).ToHashSet();
        var children = new Dictionary<MapPrefabChildKey, MapPrefabChild>();
        foreach (var key in keys.OrderBy(k => k.Kind.ToString(), StringComparer.Ordinal).ThenBy(k => k.Key, StringComparer.Ordinal))
        {
            var (walked, occurrences) = MapPrefabReferenceWalker.Child(snapshot[key], map);
            string id = names[key];
            var localKey = new MapPrefabChildKey(key.Kind, id);
            var frame = resources.Transforms.GetValueOrDefault(MapEffectiveContext.Key(key.Kind, key.Key), MapPrefabEditPreparation.Identity);
            var basis = !captured.Contains(key) && snapshot[key] is MapPrefabLightChild { Value.Owner: { } effectiveOwner }
                ? resources.Transforms[MapEffectiveContext.Key(effectiveOwner.Kind, effectiveOwner.Id)] : originFrame;
            var local = MapPrefabEditPreparation.Relative(basis, frame);
            var pose = MapPrefabEditPreparation.Presentation(local);
            MapPrefabChild child = walked switch
            {
                MapPrefabPlacementChild p => p with { Value = Placement(p.Value, id, pose) },
                MapPrefabSurfaceChild s => s with { Surface = s.Surface with { Id = id, SemanticSha256 = "" } },
                MapPrefabRecordChild r => r with { Value = r.Value is MapPrismDomain prism ? prism with { Id = id, InstanceId = "" } : r.Value with { Id = id } },
                MapPrefabMediumChild m => m with { Value = m.Value with { Key = id } },
                MapPrefabWaterChild w => w with { Value = w.Value with { Id = id } },
                MapPrefabLightChild l => l with { Value = l.Value with { Id = id, Owner = l.Value.Owner is { } owner ? Reference(owner) : null } },
                MapPrefabMarkerChild m => m with { Value = m.Value with { Id = id, Tags = m.Value.Tags.ToArray() } },
                MapPrefabRoofChild r => r with { Value = r.Value with { Id = id, Roof = Reference(r.Value.Roof), Rooms = r.Value.Rooms.Select(Reference).ToArray(), Groups = r.Value.Groups.Select(Reference).ToArray() } },
                MapPrefabRoofGroupChild g => g with { Value = g.Value with { Id = id, Rooms = g.Value.Rooms.Select(Reference).ToArray() } },
                _ => walked,
            };
            child = child with
            {
                Key = localKey,
                References = MapPrefabReferences.Slots(occurrences),
                LocalTransform = child is MapPrefabPlacementChild or MapPrefabStoreyChild or MapPrefabMediumChild or MapPrefabRoofGroupChild || captured.Contains(key)
                    ? MapPrefabEditPreparation.AuthoredIdentity : pose,
            };
            children.Add(localKey, child);
        }
        var materialized = rows.Select(row => new MapPrefabLocalMaterialization(
            Reference(new(MapPrefabReferenceScope.World, MapPrefabChildKind.Placement, row.PlacementId)), row.ResourceId, row.ResourceSha256, row.Kind,
            MapPrefabEditPreparation.Relative(originFrame, resources.Transforms[MapEffectiveContext.Key(MapPrefabChildKind.Placement, row.PlacementId)]),
            row.Children.Select(child =>
            {
                var reference = Reference(new(MapPrefabReferenceScope.World, child.LocalChild.Kind, child.WorldId));
                return new MapPrefabLocalMaterializedChild(reference, child.OwnerLocalPose,
                    MapPrefabIdentity.ChildDigest(children[new(reference.Kind, reference.Id)]));
            }).ToArray())).ToArray();
        var asset = new MapPrefabAsset(1, assetId, children.Values.ToArray())
        {
            MaterializedAttachments = materialized,
            Roofs = resources.BuildingIntent.Roofs.Where(r => keys.Contains(new(r.Kind, r.Id))).Select(Reference).ToArray(),
        };
        return MapPrefabFile.Read(MapPrefabFile.Write(asset), assetId);
    }

    static MapPlacement Placement(MapPlacement source, string id, MapTransform pose)
    {
        var p = MapPrefabReferenceWalker.Copy(source, id, source.SupportBinding);
        p.NumericId = null;
        p.X = pose.Position.X; p.Y = pose.Position.Y; p.Z = pose.Position.Z;
        p.Yaw = pose.YawRadians; p.Scale = pose.Scale;
        return p;
    }

    static void RequireCompleteSelection(IReadOnlyDictionary<MapPrefabChildKey, MapPrefabChild> snapshot,
        IReadOnlySet<MapPrefabChildKey> selected, MapEffectiveResources resources)
    {
        void Required(MapPrefabChildKind kind, string id)
        {
            if (!selected.Contains(new(kind, id))) throw new MapDocumentException("Partial room, floor or roof selection is not lossless.");
        }
        foreach (var key in selected)
        {
            switch (snapshot[key])
            {
                case MapPrefabRecordChild record:
                    Required(MapPrefabChildKind.Surface, record.Anchor.SurfaceId);
                    if (record.Value is MapPrismDomain prism)
                    { Required(MapPrefabChildKind.Record, prism.Space.Id); Required(MapPrefabChildKind.Storey, prism.StoreyId); }
                    if (record.Value is MapSpaceDoc { Kind: MapSpaceKind.Room } room)
                    {
                        var domains = snapshot.Values.OfType<MapPrefabRecordChild>().Where(c => c.Value is MapPrismDomain p && p.Space.Id == room.Id).ToArray();
                        if (domains.Length != 1) throw new MapDocumentException("A room selection requires its unique prism.");
                        Required(MapPrefabChildKind.Record, domains[0].Key.Key);
                    }
                    break;
                case MapPrefabSurfaceChild surface:
                    foreach (var anchoredRecord in resources.Surfaces.Patches.Values.Where(p => p.Key.SurfaceId == surface.Surface.Id)
                                 .SelectMany(p => p.Records).Where(r => r is MapSpaceDoc or MapPrismDomain))
                        Required(MapPrefabChildKind.Record, anchoredRecord.Id);
                    break;
                case MapPrefabRoofChild roof: Required(MapPrefabChildKind.Placement, roof.Value.Roof.Id); break;
            }
        }
    }

    sealed class ExtractReferences(IReadOnlyDictionary<MapPrefabChildKey, string> names) : MapPrefabReferenceMap
    {
        internal override (string Id, MapPrefabReferenceScope? Scope) Text(string path, MapPrefabChildKind kind, string id, bool space) =>
            names.TryGetValue(new(kind, id), out var local) ? (local, MapPrefabReferenceScope.Local) : (id, MapPrefabReferenceScope.World);

        internal override (MapRecordRef Value, MapPrefabReferenceScope? IdScope, MapPrefabReferenceScope? AnchorScope)
            Record(string path, MapRecordRef value, bool space)
        {
            // An external record retains its whole World anchor, even when a copy of that surface is selected.
            if (!names.ContainsKey(new(MapPrefabChildKind.Record, value.Id)))
                return (value, MapPrefabReferenceScope.World, MapPrefabReferenceScope.World);
            return base.Record(path, value, space);
        }
    }
}
