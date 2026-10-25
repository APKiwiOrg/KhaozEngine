using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using static KhaozEngine.MapDoc.Prefabs.MapPrefabResolver;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>Selected owner inputs. This is not a document and carries no whole-world completeness claim.</summary>
internal sealed record MapEffectiveResolutionInput(IReadOnlyList<MapPlacement> Placements, MapPrefabSet? Prefabs,
    MapBuildingIntent? BuildingIntent, IReadOnlyList<MapStandaloneResourceTransform>? NativeResourceTransforms,
    MapSurfaceSet Surfaces, MapWaterSet? Water, string RootDigest)
{
    internal static MapEffectiveResolutionInput FromDocument(MapDocument document, string rootDigest = "") => new(document.Placements,
        document.Prefabs, document.BuildingIntent, document.NativeResourceTransforms, document.Surfaces, document.Water,
        rootDigest);
}

internal sealed record MapEffectiveEnumeration(List<MapPrefabChild> Children, Dictionary<string, MapEffectiveTransform> Transforms,
    Dictionary<string, string> Owners, List<MapPrefabReference> Roofs, Dictionary<string, string> StoreyOwners,
    HashSet<string> StandaloneIds, List<MapPrefabAttachmentTransfer> Attachments, List<string> DigestParts, Dictionary<string, MapTransform> WorldLightFrames);

/// <summary>The shared effective rules. Full entry points own complete preflight, scoped callers own dependency closure.</summary>
internal static class MapEffectiveResolutionKernel
{
    internal static MapEffectiveEnumeration Enumerate(MapEffectiveResolutionInput authored, MapAssetClosure assets,
        IMapSurfaceSource? nativeSource = null, MapPrefabExpansionObserver? observer = null, bool metadataOnly = false)
    {
        var transforms = new Dictionary<string, MapEffectiveTransform>(StringComparer.Ordinal);
        foreach (var value in authored.NativeResourceTransforms ?? []) transforms.Add(MapEffectiveContext.Key(value.Kind, value.OwnerId), value.Transform);
        var children = new List<MapPrefabChild>();
        var storeyOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        var attachments = new List<MapPrefabAttachmentTransfer>();
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var worldLightFrames = new Dictionary<string, MapTransform>(StringComparer.Ordinal);
        var roofs = new List<MapPrefabReference>(authored.BuildingIntent?.Roofs ?? []);
        var digestParts = new List<string> { "kemap/effective-resources/1", "2", assets.Hash, authored.RootDigest };
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
            if (!metadataOnly) _ = MapEffectiveAttachments.Reuse(authored, attachment);
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
            MapEffectiveProductPreparation.BeforeAttachments(suppression);
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
                    pose = DeclaredOwnerFrame(instancePose, local.LocalTransform, placement.Value, ownerRow?.PlacementFrame);
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
            {
                if (metadataOnly) continue;
                throw new MapDocumentException($"Unknown effective light owner '{owners[key]}'.");
            }
            transforms[key] = MapEffectiveTransform.Compose(owner, local);
        }
        return new(children, transforms, owners, roofs, storeyOwners, standaloneNumbers.Keys.ToHashSet(StringComparer.Ordinal), attachments, digestParts, worldLightFrames);
    }

    internal static MapEffectiveResolutionProduct Resolve(MapEffectiveResolutionInput authored, MapAssetClosure assets,
        IMapSurfaceSource? nativeSource = null, MapPrefabExpansionObserver? observer = null, MapQueryLimits? queryLimits = null)
    {
        queryLimits ??= new();
        var admission = Scene.MapSceneAdmission.Current;
        var callerPreparation = admission?.PreparationScope;
        using var preparation = admission?.BeginPreparation();
        if (nativeSource is Scene.MapSceneSurfaceSource { PreparationOwnership: { } nativeOwner } && callerPreparation is not null)
            callerPreparation.Export(nativeOwner, preparation!);
        using var geometryDiagnostics = admission?.AcquisitionDiagnostics?.Enter(Scene.MapAcquisitionStage.ResolverGeometry);
        using var geometryPreparation = admission?.BeginPreparation();
        using var enumerationWorkspace = admission?.Workspace();
        MapEffectiveConstructionPreparation.BeforeEnumerate(authored, assets, enumerationWorkspace);
        var captured = Enumerate(authored, assets, nativeSource, observer);
        if (enumerationWorkspace is not null)
            geometryPreparation!.Track(enumerationWorkspace.Transfer(MapEffectiveConstructionPreparation.Tables(captured)));
        // Remapping has finished. Payload clones stay in the geometry collector until context capture.
        enumerationWorkspace?.Dispose();
        var children = captured.Children;
        var transforms = captured.Transforms;
        var owners = captured.Owners;
        var roofs = captured.Roofs;
        var storeyOwners = captured.StoreyOwners;
        var standaloneNumbers = captured.StandaloneIds;
        var attachments = captured.Attachments;
        var digestParts = captured.DigestParts;
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
        var placements = MapEffectivePlacementPreparation.Resolve(children, assets, transforms, source, queryLimits,
            standaloneNumbers, owners, out var supports, out var supportRequests, out var placementOwnership);
        foreach (var support in supports)
        {
            string key = MapEffectiveContext.Key(MapPrefabChildKind.Placement, support.PlacementId);
            double delta = support.Support.WorldY - transforms[key].Y;
            transforms[key] = transforms[key] with { Y = support.Support.WorldY };
            foreach (var owned in owners.Where(o => o.Value == support.PlacementId)) transforms[owned.Key] = transforms[owned.Key] with { Y = transforms[owned.Key].Y + delta };
        }
        if (supports.Count != 0)
        {
            var previousContext = context;
            var previousSource = source;
            context = new(surfaces, transforms, Digest(), Storeys(), localWater);
            source = Source(context);
            previousContext.PreparationOwnership?.Dispose();
            MapEffectiveProductPreparation.SourceOwnership(previousSource)?.Dispose();
        }
        if (geometryPreparation is not null)
        {
            if (context.PreparationOwnership is { } contextOwner) geometryPreparation.Export(contextOwner, preparation!);
            if (MapEffectiveProductPreparation.SourceOwnership(source) is { } sourceOwner) geometryPreparation.Export(sourceOwner, preparation!);
            if (placementOwnership is not null) geometryPreparation.Export(placementOwnership, preparation!);
        }
        // The context owns detached geometry now. Later stages consume only the remaining child kinds.
        children.RemoveAll(child => child is MapPrefabSurfaceChild or MapPrefabRecordChild);
        surfaces.Patches.Clear(); surfaces.Refs.Clear();
        var workingTables = geometryPreparation?.Complete(MapEffectiveConstructionPreparation.Tables(captured));
        geometryPreparation?.Dispose();
        geometryDiagnostics?.Dispose();
        using var tablesDiagnostics = admission?.AcquisitionDiagnostics?.Enter(Scene.MapAcquisitionStage.ResolverTables);
        using var tablesPreparation = admission?.BeginPreparation();
        admission?.ReserveBytes(checked(1024L + 1024L * children.Count +
            (authored.BuildingIntent is { } authoredIntent ? 2L * MapBuildingIntentPreparation.Bytes(authoredIntent) : 0)));
        var water = authored.Water?.Clone();
        foreach (var medium in children.OfType<MapPrefabMediumChild>()) (water ??= new()).Media.Add(medium.Value);
        var waterCorners = new Dictionary<string, IReadOnlyList<MapExactPoint>>(StringComparer.Ordinal);
        foreach (var body in children.OfType<MapPrefabWaterChild>())
        {
            using var bodyPreparation = Scene.MapSceneAdmission.Current?.BeginPreparation();
            long vertices = checked(4L * body.Value.Domain.Rects.Count + body.Value.Domain.Rings.Sum(r => (long)r.Vertices.Count));
            Scene.MapSceneAdmission.Current?.ReserveBytes(checked(256L + 128L * vertices));
            var kernel = context.Transform(MapPrefabChildKind.WaterBody, body.Value.Id)!;
            (water ??= new()).Bodies.Add(body.Value with { SurfaceY = kernel.Height(MapExactValue.FromSingle(body.Value.SurfaceY)).ToSingle() });
            var corners = Corners(body.Value).Select(kernel.Point).ToArray();
            waterCorners.Add(body.Value.Id, corners);
            bodyPreparation?.Complete(checked(224L + 48L * corners.Length));
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
        // Markers resolve after the seat rebuild, so seated support placements and their owned floors are in the source.
        var markers = MapMarkerResolution.Resolve(MapMarkerExpansion.Capture(authored, assets), source,
            assets.Roots.Select(a => a.Sha256).ToArray(), queryLimits);
        var livePlacementIds = placements.Select(p => p.PlacementId).ToHashSet(StringComparer.Ordinal);
        var inactivePlacementIds = (authored.Prefabs?.Instances ?? []).SelectMany(instance => instance.Bindings.Entries
            .Where(binding => binding.Child.Kind == MapPrefabChildKind.Placement && (instance.Removed || binding.Removed)).Select(binding => binding.WorldId))
            .Concat((authored.Prefabs?.Transfers ?? []).Where(t => t.Retired && t.Binding.Child.Kind == MapPrefabChildKind.Placement).Select(t => t.Binding.WorldId))
            .ToHashSet(StringComparer.Ordinal);
        var intent = new MapBuildingIntent(1, originalIntent.RoofLinks.Concat(children.OfType<MapPrefabRoofChild>().Select(c => c.Value)).ToArray(),
            originalIntent.RoofGroups.Concat(children.OfType<MapPrefabRoofGroupChild>().Select(c => c.Value)).ToArray(), lights, markers.Intent)
        {
            // Retained inventory references can name a removed reservation. A live transferred producer still wins.
            Roofs = roofs.Where(r => r.Scope != MapPrefabReferenceScope.World || !inactivePlacementIds.Contains(r.Id) || livePlacementIds.Contains(r.Id)).ToArray(),
            Storeys = Storeys(),
        };
        using var captureDiagnostics = admission?.AcquisitionDiagnostics?.Enter(Scene.MapAcquisitionStage.ProductCapture);
        var resources = new MapEffectiveResolutionProduct(context, source, placements, supports, water, intent, lightPoints, waterCorners,
            attachments, markers.Markers, markers.Supports, queryLimits)
        {
            SupportRequests = supportRequests,
            OwnedAttachmentBytes = MapEffectiveProductPreparation.OwnedAttachments(attachments, authored.Prefabs?.Attachments ?? []),
        };
        if (tablesPreparation is not null)
        {
            if (workingTables is not null) tablesPreparation.WorkspaceOwner.Absorb(workingTables);
            if (placementOwnership is not null) tablesPreparation.WorkspaceOwner.Absorb(placementOwnership);
            var inventory = new Scene.MapSceneInventory();
            inventory.Context(context); inventory.Source(source);
            tablesPreparation.Complete(inventory.Product(resources));
        }
        captureDiagnostics?.Dispose();
        tablesPreparation?.Dispose();
        tablesDiagnostics?.Dispose();
        resources.PreparationOwnership = preparation?.Complete(new Scene.MapSceneInventory().Product(resources), retainDependencies: true);
        return resources;
    }

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

internal sealed class MapEffectiveResolutionProduct
{
    internal Scene.MapPreparationReservation? PreparationOwnership { get; set; }
    internal MapEffectiveContext CapturedContext => _context;
    internal MapWaterSet? CapturedWater => _water;
    readonly MapEffectiveContext _context;
    readonly MapQueryLimits _queryLimits;
    readonly MapWaterSet? _water;
    readonly MapBuildingIntent _intent;
    readonly IReadOnlyDictionary<string, MapSupportBinding> _supportRequests =
        new ReadOnlyDictionary<string, MapSupportBinding>(new Dictionary<string, MapSupportBinding>(StringComparer.Ordinal));
    public IReadOnlyList<MapResolvedPlacement> Placements { get; }
    public IReadOnlyList<MapPlacementSupport> PlacementSupports { get; }
    public MapSurfaceSet Surfaces => _context.Surfaces.Clone();
    /// <summary>Mathematical poses keyed by Kind:world-ID. Backend presentation is a separate approximation.</summary>
    public IReadOnlyDictionary<string, MapEffectiveTransform> Transforms => _context.Transforms;
    public MapWaterSet? Water => _water?.Clone();
    public MapBuildingIntent BuildingIntent => _intent.Clone();
    internal MapBuildingIntent CapturedBuildingIntent => _intent;
    public string Digest => _context.Digest;
    public IMapSurfaceSource SurfaceSource { get; }
    /// <summary>Render-only partitions compiled once per affected physical target at strict expansion.</summary>
    public IReadOnlyDictionary<string, MapPaintRegionSet> PaintRegions { get; }
    internal IReadOnlyDictionary<string, MapExactPoint> LightPoints { get; }
    internal int LightPointCapacity { get; }
    internal int WaterCornerCapacity { get; }
    internal int SupportRequestCapacity { get; private init; }
    internal int PaintRegionCapacity { get; }
    internal IReadOnlyList<MapEffectiveLight> EffectiveLights { get; }
    internal IReadOnlyList<MapEffectiveMarker> EffectiveMarkers { get; }
    /// <summary>The support answer of every Support-height marker, disabled ones included, in ordinal marker ID order.
    /// Each carries its acquisition witness, so this channel stays out of annotation digests and marker equality.</summary>
    public IReadOnlyList<MapMarkerSupport> MarkerSupports { get; }
    /// <summary>Versioned roof, light and marker intent, empty without annotations.</summary>
    public string AnnotationDigest { get; }
    internal IReadOnlyDictionary<string, IReadOnlyList<MapExactPoint>> WaterCorners { get; }
    internal IReadOnlyList<MapPrefabAttachmentTransfer> Attachments { get; }
    internal long OwnedAttachmentBytes { get; init; }
    /// <summary>World-space support requests of prefab-local placements, keyed by world placement ID. Each is computed
    /// from the placement's pre-seat frame. Standalone placements keep their authored request and are absent.</summary>
    internal IReadOnlyDictionary<string, MapSupportBinding> SupportRequests
    {
        get => _supportRequests;
        init { _supportRequests = MapEffectiveProductPreparation.CopySupports(value, out int capacity); SupportRequestCapacity = capacity; }
    }

    internal MapEffectiveResolutionProduct(MapEffectiveContext context, IMapSurfaceSource source,
        IReadOnlyList<MapResolvedPlacement> placements, IReadOnlyList<MapPlacementSupport> supports,
        MapWaterSet? water, MapBuildingIntent intent, IReadOnlyDictionary<string, MapExactPoint> lights,
        IReadOnlyDictionary<string, IReadOnlyList<MapExactPoint>> waterCorners, IReadOnlyList<MapPrefabAttachmentTransfer> attachments,
        IReadOnlyList<MapEffectiveMarker> markers, IReadOnlyList<MapMarkerSupport> markerSupports, MapQueryLimits queryLimits)
    {
        MapEffectiveProductPreparation.BeforeCopy(placements, supports);
        _context = context;
        _queryLimits = queryLimits;
        SurfaceSource = source;
        Placements = Array.AsReadOnly(placements.ToArray());
        PlacementSupports = Array.AsReadOnly(supports.ToArray());
        _water = water?.Clone();
        _intent = MapEffectiveProductPreparation.CopyIntent(intent, context);
        MapPrefabValidation.RequireEffectiveAnnotations(_intent, placements, context.Surfaces);
        Scene.MapSceneAdmission.Current?.ReserveBytes(checked(128L * lights.Count + 32L * (markers.Count + markerSupports.Count)));
        EffectiveLights = MapEffectiveAnnotations.CaptureLights(_intent, lights);
        EffectiveMarkers = Array.AsReadOnly(markers.OrderBy(m => m.Id, StringComparer.Ordinal).ToArray());
        MarkerSupports = Array.AsReadOnly(markerSupports.OrderBy(m => m.MarkerId, StringComparer.Ordinal).ToArray());
        AnnotationDigest = MapEffectiveAnnotations.Digest(_intent, EffectiveLights, EffectiveMarkers);
        LightPoints = MapEffectiveProductPreparation.CopyPoints(lights, out int lightCapacity); LightPointCapacity = lightCapacity;
        WaterCorners = MapEffectiveProductPreparation.CopyCorners(waterCorners, out int waterCapacity); WaterCornerCapacity = waterCapacity;
        Scene.MapSceneAdmission.Current?.ReserveBytes(checked(8L * attachments.Count));
        Attachments = Array.AsReadOnly(attachments.ToArray());
        Scene.MapSceneAdmission.Current?.ReserveBytes(checked(128L + 32L * context.Surfaces.Refs.Count));
        var paintTargets = context.Surfaces.Refs.Where(context.IsPartialPaint)
            .Select(s => s.PaintTargetSurfaceId ?? throw new MapDocumentException("PaintOverride requires a target surface."))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        MapSurfaceSourcePreparation.BeforeDictionary<string, MapPaintRegionSet>(paintTargets.Length);
        var paintRegions = new Dictionary<string, MapPaintRegionSet>(paintTargets.Length, StringComparer.Ordinal);
        if (paintTargets.Length != 0)
        {
            var productPreparation = Scene.MapSceneAdmission.Current?.PreparationScope;
            using var paintPreparation = Scene.MapSceneAdmission.Current?.BeginPreparation();
            var scope = MapScopedSurfaces.CompleteEffective(context, _queryLimits).Scope with
            {
                DiscoverySurfaces = context.Surfaces.Refs.Where(context.IsPartialPaint).Select(s => s.Id)
                    .Concat(paintTargets).ToHashSet(StringComparer.Ordinal),
            };
            var view = SurfaceSource is MapComposedSurfaceSource ? Acquire(scope) : MapScopedSurfaces.CompleteEffective(context, _queryLimits);
            foreach (string target in paintTargets)
            {
                Scene.MapSceneAdmission.Current?.BeforePaintTarget(target);
                var regions = MapPartialPaintCompiler.Compile(view, target, _queryLimits.Refinement);
                if (regions.PreparationOwnership is { } owner) paintPreparation!.Export(owner, productPreparation!);
                paintRegions.Add(target, regions);
            }
        }
        PaintRegionCapacity = paintRegions.EnsureCapacity(0);
        PaintRegions = new ReadOnlyDictionary<string, MapPaintRegionSet>(paintRegions);
    }
    internal void RequireView(MapScopedSurfaces view)
    {
        if (view.EffectiveContext?.Digest != Digest) throw new ArgumentException("Acquired view differs from effective resources.", nameof(view));
    }
    public MapScopedSurfaces CompleteView() => SurfaceSource is MapComposedSurfaceSource
        ? Acquire(MapScopedSurfaces.CompleteEffective(_context, _queryLimits).Scope) : MapScopedSurfaces.CompleteEffective(_context, _queryLimits);
    public MapScopedSurfaces Acquire(MapSurfaceScope scope) => MapScopedSurfaces.Acquire(SurfaceSource, scope, new[] { Digest },
        MapScopedSurfaces.EffectiveQueryPolicyVersion, MapScopedSurfaces.EffectiveBuildPolicyVersion);
}
