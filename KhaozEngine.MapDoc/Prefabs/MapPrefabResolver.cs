using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>Expands persisted identities through one captured effective resource snapshot.</summary>
public static class MapPrefabResolver
{
    public static MapEffectiveResources Expand(MapDocument document, MapAssetClosure assets, MapPrefabExpansionLimits? limits = null) =>
        Expand(document, assets, limits ?? new(), null);

    internal static MapEffectiveResources Expand(MapDocument document, MapAssetClosure assets, MapPrefabExpansionLimits limits,
        MapPrefabExpansionObserver? observer, IMapSurfaceSource? nativeSource = null)
    {
        _ = MapPrefabPreflight.Count(document, assets, limits);
        observer?.EnterStage("Copies");
        MapPrefabIdentity.RequireOwnership(document, assets);
        if (document.Tiles is { IsPartial: true }) throw new MapDocumentException("Effective expansion requires a complete document.");
        var numericErrors = new List<string>();
        _ = MapNativeValidation.ValidateNumericIds(document, numericErrors);
        if (numericErrors.Count != 0) throw new MapDocumentException(string.Join("\n", numericErrors));
        var authored = MapDocumentFile.LoadText(MapDocumentFile.SaveText(document));
        var transforms = new Dictionary<string, MapEffectiveTransform>(StringComparer.Ordinal);
        foreach (var value in authored.NativeResourceTransforms ?? []) transforms.Add(MapEffectiveContext.Key(value.Kind, value.OwnerId), value.Transform);
        var children = new List<MapPrefabChild>();
        var storeyOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        var attachments = new List<MapPrefabAttachmentTransfer>();
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var worldLightFrames = new Dictionary<string, MapTransform>(StringComparer.Ordinal);
        var markerInstances = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var transfer in authored.Prefabs?.Transfers ?? [])
            if (!transfer.Retired && transfer.Binding.Child.Kind == MapPrefabChildKind.Marker) markerInstances.Add(transfer.Binding.WorldId, transfer.InstanceId);
        var roofs = new List<MapPrefabReference>(authored.BuildingIntent?.Roofs ?? []);
        var digestParts = new List<string> { "kemap/effective-resources/1", "2", assets.Hash, MapSurfaceSemantics.RootDigest(authored) };
        if (nativeSource is not null) { digestParts.Add(nativeSource.RootSha256); digestParts.Add(nativeSource.SnapshotId); }
        var standaloneNumbers = authored.Placements.ToDictionary(p => p.Id, p => p.NumericId, StringComparer.Ordinal);
        var standalone = authored.Placements.Select(p =>
        {
            var value = MapPrefabReferenceWalker.Copy(p, p.Id, p.SupportBinding);
            value.NumericId = null;
            MapPrefabChild child = new MapPrefabPlacementChild(new(MapPrefabChildKind.Placement, p.Id), value);
            var (_, occurrences) = MapPrefabReferenceWalker.Child(child, MapPrefabReferenceMap.Identity);
            return child with { References = MapPrefabReferences.Slots(occurrences, MapPrefabReferenceScope.World) };
        }).ToArray();
        var rows = (authored.Prefabs?.Attachments ?? []).Select(a => new MapPrefabLocalMaterialization(
            new(MapPrefabReferenceScope.Local, MapPrefabChildKind.Placement, a.PlacementId), a.ResourceId, a.ResourceSha256, a.Kind,
            new(0, 0, 0, 0, 1), [])).ToArray();
        foreach (var attachment in authored.Prefabs?.Attachments ?? [])
        {
            _ = MapEffectiveAttachments.Reuse(authored, attachment);
            if (!attachment.Retired)
            {
                attachments.Add(attachment);
                foreach (var child in attachment.Children) owners[MapEffectiveContext.Key(child.LocalChild.Kind, child.WorldId)] = attachment.PlacementId;
            }
        }
        var standaloneOwners = new Dictionary<MapPrefabChildKey, MapPrefabChildKey>();
        var standaloneChildren = standalone.Concat(MapEffectiveSupport.Enumerate(assets, standalone, rows, allowOpaque: true, owners: standaloneOwners).Select(c => OwnerPose(c, standalone, standaloneOwners)))
            .Concat(MapPrefabAttachedLights.Enumerate(assets, standalone, rows, allowOpaque: true, owners: standaloneOwners).Select(c => OwnerPose(c, standalone, standaloneOwners))).ToArray();
        observer?.EnterStage("Remaps");
        var standaloneAsset = new MapPrefabAsset(1, "standalone-effective", standaloneChildren);
        var standaloneBound = MapPrefabReferences.Remap(standaloneAsset, standaloneChildren.ToDictionary(c => c.Key, c => c.Key.Key), null);
        for (int i = 0; i < standaloneChildren.Length; i++)
        {
            var local = standaloneChildren[i];
            var key = MapEffectiveContext.Key(local.Key.Kind, local.Key.Key);
            if (!transforms.ContainsKey(key))
            {
                var pose = local is MapPrefabPlacementChild placement ? MapEffectiveTransform.Compose(MapEffectiveTransform.From(placement.LocalTransform),
                    MapPrefabMaterializationOverrides.Pose(placement.Value)) : MapEffectiveTransform.From(local.LocalTransform);
                if (local is not MapPrefabPlacementChild)
                {
                    if (standaloneOwners.TryGetValue(local.Key, out var owner))
                    { pose = transforms[MapEffectiveContext.Key(owner.Kind, owner.Key)]; owners[key] = owner.Key; }
                }
                transforms.Add(key, pose);
            }
            var world = standaloneBound.Children[i];
            if (world is MapPrefabPlacementChild standalonePlacement) standalonePlacement.Value.NumericId = standaloneNumbers[standalonePlacement.Value.Id];
            children.Add(world);
        }
        foreach (var instance in authored.Prefabs?.Instances ?? [])
        {
            if (instance.Removed) continue;
            var instancePose = MapEffectiveTransform.From(instance.Transform);
            var reconciled = MapPrefabMaterializationOverrides.Reconcile(assets.BorrowPrefab(instance.ResourceId), instance.Overrides);
            var explicitChildren = reconciled.EffectiveAsset.Children.Where(c => !reconciled.Removed.Contains(c.Key)).ToArray();
            var suppression = reconciled.EffectiveAsset.MaterializedAttachments;
            var implicitOwners = new Dictionary<MapPrefabChildKey, MapPrefabChildKey>();
            var localChildren = explicitChildren.Concat(MapEffectiveSupport.Enumerate(assets, explicitChildren, suppression, owners: implicitOwners).Select(c => OwnerPose(c, explicitChildren, implicitOwners)))
                .Concat(MapPrefabAttachedLights.Enumerate(assets, explicitChildren, suppression, owners: implicitOwners).Select(c => OwnerPose(c, explicitChildren, implicitOwners))).ToArray();
            var bindings = instance.Bindings.Entries.Where(b => !b.Removed).ToDictionary(b => b.Child, b => b.WorldId);
            var captured = MapEffectiveAttachments.ResolveLocal(reconciled, bindings, instancePose, instance.ParentSpace);
            attachments.AddRange(captured);
            var liveAsset = reconciled.EffectiveAsset with
            {
                Children = localChildren,
                Roofs = reconciled.EffectiveAsset.Roofs.Where(r => r.Scope == MapPrefabReferenceScope.World || !reconciled.Removed.Contains(new(r.Kind, r.Id))).ToArray(),
                MaterializedAttachments = suppression.Where(r => !reconciled.Removed.Contains(new(MapPrefabChildKind.Placement, r.Placement.Id)))
                    .Select(r => r with { Children = r.Children.Where(c => !reconciled.Removed.Contains(new(c.Child.Kind, c.Child.Id))).ToArray() }).ToArray(),
            };
            var remapped = MapPrefabReferences.Remap(liveAsset, bindings, instance.ParentSpace);
            for (int i = 0; i < localChildren.Length; i++)
            {
                var local = localChildren[i];
                var world = remapped.Children[i];
                MapEffectiveTransform pose;
                var capturedRow = suppression.FirstOrDefault(r => r.Children.Any(c => c.Child.Kind == local.Key.Kind && c.Child.Id == local.Key.Key));
                if (capturedRow is not null)
                {
                    var child = capturedRow.Children.Single(c => c.Child.Kind == local.Key.Kind && c.Child.Id == local.Key.Key);
                    pose = MapEffectiveTransform.Compose(MapEffectiveTransform.Compose(instancePose, capturedRow.PlacementFrame), child.OwnerLocalPose);
                }
                else if (local is MapPrefabPlacementChild placement)
                {
                    var ownerRow = suppression.FirstOrDefault(r => r.Placement.Id == local.Key.Key);
                    pose = ownerRow is not null ? MapEffectiveTransform.Compose(instancePose, ownerRow.PlacementFrame)
                        : MapEffectiveTransform.Compose(MapEffectiveTransform.Compose(instancePose, local.LocalTransform), MapPrefabMaterializationOverrides.Pose(placement.Value));
                }
                else
                {
                    pose = MapEffectiveTransform.Compose(instancePose, local.LocalTransform);
                    if (implicitOwners.TryGetValue(local.Key, out var attachedKey))
                    {
                        var attachedOwner = explicitChildren.OfType<MapPrefabPlacementChild>().Single(p => p.Key == attachedKey);
                        pose = MapEffectiveTransform.Compose(MapEffectiveTransform.Compose(instancePose, attachedOwner.LocalTransform),
                            MapPrefabMaterializationOverrides.Pose(attachedOwner.Value));
                    }
                    else if (local is MapPrefabLightChild { Value.Owner: { Scope: MapPrefabReferenceScope.Local } owner })
                    {
                        var placementOwner = explicitChildren.OfType<MapPrefabPlacementChild>().Single(p => p.Key.Key == owner.Id);
                        pose = MapEffectiveTransform.Compose(MapEffectiveTransform.Compose(MapEffectiveTransform.Compose(instancePose, placementOwner.LocalTransform),
                            MapPrefabMaterializationOverrides.Pose(placementOwner.Value)), local.LocalTransform);
                    }
                }
                string id = bindings[local.Key];
                if (world is MapPrefabStoreyChild) storeyOwners[id] = instance.Id;
                if (world is MapPrefabMarkerChild) markerInstances[id] = instance.Id;
                if (capturedRow is null && local is MapPrefabLightChild { Value.Owner.Scope: MapPrefabReferenceScope.World })
                    worldLightFrames[id] = local.LocalTransform;
                if (world is MapPrefabRecordChild { Value: MapPrismDomain prism } room)
                    world = room with { Value = prism with { InstanceId = instance.Id } };
                if (capturedRow is not null) owners[MapEffectiveContext.Key(local.Key.Kind, id)] = bindings[new(MapPrefabChildKind.Placement, capturedRow.Placement.Id)];
                else if (local is not MapPrefabPlacementChild)
                {
                    if (implicitOwners.TryGetValue(local.Key, out var attached)) owners[MapEffectiveContext.Key(local.Key.Kind, id)] = bindings[attached];
                    else if (world is MapPrefabLightChild { Value.Owner: { Kind: MapPrefabChildKind.Placement } owner }) owners[MapEffectiveContext.Key(local.Key.Kind, id)] = owner.Id;
                }
                transforms.Add(MapEffectiveContext.Key(local.Key.Kind, id), pose);
                if (world is MapPrefabPlacementChild placed)
                {
                    var value = MapPrefabReferenceWalker.Copy(placed.Value, id, placed.Value.SupportBinding);
                    value.NumericId = instance.Bindings.Entries.Single(b => b.Child == local.Key).NumericId;
                    world = placed with { Value = value };
                }
                children.Add(world with { Key = new(local.Key.Kind, id) });
                digestParts.Add(reconciled.EffectiveDigests.TryGetValue(local.Key, out var pin) ? pin : MapPrefabIdentity.ChildDigest(local));
            }
            roofs.AddRange(remapped.Roofs);
            foreach (var roof in remapped.Roofs) digestParts.Add(MapPrefabJson.Digest("kemap/effective-roof/1", roof));
        }
        foreach (var (id, local) in worldLightFrames)
        {
            string key = MapEffectiveContext.Key(MapPrefabChildKind.Light, id);
            if (!transforms.TryGetValue(MapEffectiveContext.Key(MapPrefabChildKind.Placement, owners[key]), out var owner))
                throw new MapDocumentException($"Unknown effective light owner '{owners[key]}'.");
            transforms[key] = MapEffectiveTransform.Compose(owner, local);
        }
        observer?.EnterStage("Sorts");
        children.Sort((a, b) => { int kind = a.Key.Kind.CompareTo(b.Key.Kind); return kind != 0 ? kind : StringComparer.Ordinal.Compare(a.Key.Key, b.Key.Key); });
        var surfaces = MapEffectiveSupport.Install(assets, children, transforms);
        foreach (var surface in nativeSource?.Surfaces ?? authored.Surfaces.Refs) surfaces.Refs.Add(surface);
        foreach (var patch in authored.Surfaces.Patches) surfaces.Patches.Add(patch.Key, patch.Value.Clone());
        observer?.EnterStage("ExpandedGeometry");
        string Digest() => MapPrefabJson.Digest("kemap/effective-resources/1", new
        {
            Parts = digestParts,
            Transforms = transforms.OrderBy(t => t.Key, StringComparer.Ordinal).ToArray(),
            Patches = surfaces.Patches.Select(p => new { p.Key, Digest = MapSurfaceSemantics.PatchDigest(p.Value) }).ToArray(),
        });
        MapStoreyDoc[] Storeys() => (authored.BuildingIntent?.Storeys ?? []).Concat(children.OfType<MapPrefabStoreyChild>()
            .Select(c => new MapStoreyDoc(1, c.Key.Key, c.Name, storeyOwners.GetValueOrDefault(c.Key.Key)))).ToArray();
        var localWater = children.OfType<MapPrefabWaterChild>().Select(c => c.Value).ToArray();
        var context = new MapEffectiveContext(surfaces, transforms, Digest(), Storeys(), localWater);
        IMapSurfaceSource Source(MapEffectiveContext value) => nativeSource is null ? new MapEffectiveSurfaceSource(value) : new MapComposedSurfaceSource(value, nativeSource);
        var source = Source(context);
        var effectivePlacements = new List<MapPlacement>();
        foreach (var child in children.OfType<MapPrefabPlacementChild>())
        {
            var pose = transforms[MapEffectiveContext.Key(MapPrefabChildKind.Placement, child.Value.Id)];
            var p = MapPrefabReferenceWalker.Copy(child.Value, child.Value.Id, child.Value.SupportBinding);
            p.X = (float)pose.X; p.Z = (float)pose.Z; p.Yaw = (float)pose.YawRadians; p.Scale = (float)pose.Scale;
            p.Y = child.Value.Y is null ? null : (float)pose.Y;
            if (p.SupportBinding is { } binding) p.SupportBinding = binding with
            {
                ReferenceY = binding.ReferenceY is { } y ? new MapResourceTransform(pose).Height(MapExactValue.FromSingle(y)).ToSingle() : null,
                SearchBelow = binding.SearchBelow is { } below ? (float)(below * pose.Scale) : null,
                SearchAbove = binding.SearchAbove is { } above ? (float)(above * pose.Scale) : null,
            };
            effectivePlacements.Add(p);
        }
        var placements = MapResolverV2.ResolveEffectivePlacements(authored, assets, effectivePlacements, transforms, source, out var supports, owners);
        foreach (var support in supports)
        {
            string key = MapEffectiveContext.Key(MapPrefabChildKind.Placement, support.PlacementId);
            double delta = support.Support.WorldY - transforms[key].Y;
            transforms[key] = transforms[key] with { Y = support.Support.WorldY };
            foreach (var owned in owners.Where(o => o.Value == support.PlacementId)) transforms[owned.Key] = transforms[owned.Key] with { Y = transforms[owned.Key].Y + delta };
        }
        if (supports.Count != 0)
        {
            context = new(surfaces, transforms, Digest(), Storeys(), localWater);
            source = Source(context);
        }
        var water = authored.Water?.Clone();
        foreach (var medium in children.OfType<MapPrefabMediumChild>()) (water ??= new()).Media.Add(medium.Value);
        var waterCorners = new Dictionary<string, IReadOnlyList<MapExactPoint>>(StringComparer.Ordinal);
        foreach (var body in children.OfType<MapPrefabWaterChild>())
        {
            var kernel = context.Transform(MapPrefabChildKind.WaterBody, body.Value.Id)!;
            (water ??= new()).Bodies.Add(body.Value with { SurfaceY = kernel.Height(MapExactValue.FromSingle(body.Value.SurfaceY)).ToSingle() });
            waterCorners.Add(body.Value.Id, Corners(body.Value).Select(kernel.Point).ToArray());
        }
        var originalIntent = authored.BuildingIntent ?? new(1, [], [], [], []);
        var lightPoints = new Dictionary<string, MapExactPoint>(StringComparer.Ordinal);
        var lights = new List<MapLocalLightDoc>();
        foreach (var light in originalIntent.Lights)
        {
            var frame = MapEffectiveAnnotations.LightFrame(context.Transforms, light);
            var kernel = frame is { } pose ? new MapResourceTransform(pose) : null;
            var point = kernel?.Point(Exact(light.Position)) ?? Exact(light.Position);
            lightPoints.Add(light.Id, point);
            lights.Add(light with { Position = new(point.X.ToSingle(), point.Y.ToSingle(), point.Z.ToSingle()), Radius = kernel is null ? light.Radius : (float)(light.Radius * kernel.Transform.Scale) });
        }
        foreach (var light in children.OfType<MapPrefabLightChild>())
        {
            var kernel = context.Transform(MapPrefabChildKind.Light, light.Value.Id)!;
            var point = kernel.Point(Exact(light.Value.Position));
            lightPoints.Add(light.Value.Id, point);
            lights.Add(light.Value with { Position = new(point.X.ToSingle(), point.Y.ToSingle(), point.Z.ToSingle()), Radius = (float)(light.Value.Radius * kernel.Transform.Scale) });
        }
        var markerPoints = new Dictionary<string, MapExactPoint>(StringComparer.Ordinal);
        var markers = originalIntent.Markers.Select(marker =>
        {
            var kernel = context.Transform(MapPrefabChildKind.Marker, marker.Id);
            var point = kernel?.Point(Exact(marker.Position)) ?? Exact(marker.Position);
            markerPoints.Add(marker.Id, point);
            if (kernel is null) return marker;
            return marker with { Position = new(point.X.ToSingle(), point.Y.ToSingle(), point.Z.ToSingle()), YawRadians = (float)(marker.YawRadians + kernel.Transform.YawRadians) };
        }).ToList();
        foreach (var marker in children.OfType<MapPrefabMarkerChild>())
        {
            var kernel = context.Transform(MapPrefabChildKind.Marker, marker.Value.Id)!;
            var point = kernel.Point(Exact(marker.Value.Position));
            markerPoints.Add(marker.Value.Id, point);
            markers.Add(marker.Value with { Position = new(point.X.ToSingle(), point.Y.ToSingle(), point.Z.ToSingle()), YawRadians = (float)(marker.Value.YawRadians + kernel.Transform.YawRadians) });
        }
        var livePlacementIds = placements.Select(p => p.PlacementId).ToHashSet(StringComparer.Ordinal);
        var inactivePlacementIds = (authored.Prefabs?.Instances ?? []).SelectMany(instance => instance.Bindings.Entries
            .Where(binding => binding.Child.Kind == MapPrefabChildKind.Placement && (instance.Removed || binding.Removed)).Select(binding => binding.WorldId))
            .Concat((authored.Prefabs?.Transfers ?? []).Where(t => t.Retired && t.Binding.Child.Kind == MapPrefabChildKind.Placement).Select(t => t.Binding.WorldId))
            .ToHashSet(StringComparer.Ordinal);
        var intent = new MapBuildingIntent(1, originalIntent.RoofLinks.Concat(children.OfType<MapPrefabRoofChild>().Select(c => c.Value)).ToArray(),
            originalIntent.RoofGroups.Concat(children.OfType<MapPrefabRoofGroupChild>().Select(c => c.Value)).ToArray(), lights, markers)
        {
            // Retained inventory references can name a removed reservation. A live transferred producer still wins.
            Roofs = roofs.Where(r => r.Scope != MapPrefabReferenceScope.World || !inactivePlacementIds.Contains(r.Id) || livePlacementIds.Contains(r.Id)).ToArray(),
            Storeys = Storeys(),
        };
        return new(context, source, placements, supports, water, intent, lightPoints, waterCorners, attachments, markerPoints, markerInstances);
    }
    static MapPrefabChild OwnerPose(MapPrefabChild child, IReadOnlyList<MapPrefabChild> children,
        IReadOnlyDictionary<MapPrefabChildKey, MapPrefabChildKey> owners) =>
        child with { LocalTransform = MapPrefabMaterializationOverrides.Pose(children.OfType<MapPrefabPlacementChild>().Single(p => p.Key == owners[child.Key]).Value) };
    static MapExactPoint Exact(Vector3 point) => new(MapExactValue.FromSingle(point.X), MapExactValue.FromSingle(point.Y), MapExactValue.FromSingle(point.Z));
    static IEnumerable<MapExactPoint> Corners(MapWaterBodyDoc body)
    {
        foreach (var rect in body.Domain.Rects)
            foreach (var address in new[] { MapLatticeAddress.Corner(rect.MinX, rect.MinZ), MapLatticeAddress.Corner(rect.MaxX, rect.MinZ),
                MapLatticeAddress.Corner(rect.MaxX, rect.MaxZ), MapLatticeAddress.Corner(rect.MinX, rect.MaxZ) })
            {
                var xz = body.Domain.Frame.WorldXz(address);
                yield return new(xz.X, MapExactValue.FromSingle(body.SurfaceY), xz.Z);
            }
        foreach (var ring in body.Domain.Rings) foreach (var address in ring.Vertices)
        { var xz = body.Domain.Frame.WorldXz(address); yield return new(xz.X, MapExactValue.FromSingle(body.SurfaceY), xz.Z); }
    }
}
