using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using KhaozEngine.Diagnostics;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Prefabs;

internal sealed record MapPrefabExpansionWork(long Instances, long Children, long ReferenceEdges, long Patches,
    long Records, long GeometryElements, long Bindings, long Overrides, long Transfers, long PayloadBytes);
internal sealed record MapPrefabExpansionObserver(Action<string> EnterStage);

/// <summary>Charges captured metadata and every retained and per-instance expansion before guarded work.</summary>
internal static class MapPrefabPreflight
{
    internal static MapPrefabExpansionWork Count(MapDocument document, MapAssetClosure assets, MapPrefabExpansionLimits limits)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaxInstances < 0 || limits.MaxChildren < 0 || limits.MaxReferenceEdges < 0 || limits.MaxPatches < 0 ||
            limits.MaxRecords < 0 || limits.MaxGeometryElements < 0 || limits.MaxBindings < 0 || limits.MaxOverrides < 0 ||
            limits.MaxTransfers < 0 || limits.MaxPayloadBytes < 0) throw new ArgumentOutOfRangeException(nameof(limits));
        var count = new Counter(limits, document.Prefabs?.Attachments);
        try
        {
            foreach (MapResolvedResource resource in assets.CapturedResources)
            {
                count.PayloadBytes = checked(count.PayloadBytes + assets.ResourceByteLength(resource.Reference.Id));
                count.ReferenceEdges = checked(count.ReferenceEdges + resource.Dependencies.Count);
                count.Check();
            }
            foreach (var surface in document.Surfaces.Refs) { count.Children = AddProduct(count.Children, 1, 1); count.Surface(surface); count.Check(); }
            foreach (var patch in document.Surfaces.Patches.Values)
            { count.Children = AddProduct(count.Children, patch.Records.Count, 1); count.Patch(patch, document.Surfaces.Refs.Find(s => s.Id == patch.Key.SurfaceId)!, references: true); }
            foreach (var placement in document.Placements) { count.Children = AddProduct(count.Children, 1, 1); count.Placement(assets, placement, null, allowOpaque: true); }
            if (document.Prefabs is { } set)
            {
                count.Instances = set.Instances.Count;
                count.Transfers = checked(set.Transfers.Count + (long)set.Attachments.Count);
                count.Check();
                foreach (var transfer in set.Transfers) { count.Bindings++; count.ReferenceEdges++; count.Text(transfer.InstanceId); count.Text(transfer.Binding.WorldId); count.Text(transfer.Binding.Child.Key); count.Check(); }
                foreach (var attachment in set.Attachments)
                {
                    count.Transfers = checked(count.Transfers + attachment.Children.Count);
                    count.ReferenceEdges = checked(count.ReferenceEdges + 1 + 2L * attachment.Children.Count);
                    count.Check();
                }
                foreach (MapPrefabInstance instance in set.Instances)
                {
                    count.Text(instance.Id); count.Text(instance.ResourceId); count.Text(instance.ResourceSha256);
                    foreach (var binding in instance.Bindings.Entries) { count.Text(binding.Child.Key); count.Text(binding.WorldId); }
                    count.Bindings = checked(count.Bindings + instance.Bindings.Entries.Count);
                    count.ReferenceEdges = checked(count.ReferenceEdges + 1 + (instance.ParentSpace is null ? 0 : 2));
                    count.Check();
                    // A resolvable retained base keeps its established work charge. Missing historical versions
                    // have only the persisted reservation inventory, just like unpacked sources.
                    bool retainedBase = !instance.Unpacked && (!instance.Removed ||
                        assets.TryGetResource(instance.ResourceId, out var pinned) && pinned!.Kind == MapResourceKind.Prefab &&
                        pinned.Reference.Sha256 == instance.ResourceSha256);
                    MapPrefabAsset? asset = retainedBase ? assets.BorrowPrefab(instance.ResourceId) : null;
                    if (asset is not null)
                    {
                        foreach (var child in asset.Children) count.Child(assets, child, asset.MaterializedAttachments);
                        count.ReferenceEdges = checked(count.ReferenceEdges + asset.Roofs.Count);
                        foreach (var row in asset.MaterializedAttachments)
                        {
                            count.Transfers = checked(count.Transfers + 1 + row.Children.Count);
                            count.ReferenceEdges = checked(count.ReferenceEdges + 2 + row.Children.Count);
                            count.Check();
                        }
                    }
                    foreach (var value in instance.Overrides)
                        for (MapPrefabOverride? retained = value; retained is not null; retained = (retained as MapPrefabRemoveOverride)?.Previous)
                        {
                            count.Text(retained.Child.Key);
                            if (retained is MapPrefabPlacementOverride metadata) { count.Text(metadata.AssetId); if (metadata.Tags is not null) foreach (var tag in metadata.Tags) count.Text(tag); }
                            count.Overrides = checked(count.Overrides + 1);
                            count.ReferenceEdges = checked(count.ReferenceEdges + 1);
                            count.Check();
                            if (retained is MapPrefabReplaceOverride replace) count.Child(assets, replace.Value, asset?.MaterializedAttachments, expandResources: retainedBase);
                            if (retained is MapPrefabAddOverride add) count.Child(assets, add.Value, asset?.MaterializedAttachments, expandResources: retainedBase);
                            if (retainedBase && retained is MapPrefabPlacementOverride { AssetId: { } id }) count.PlacementAsset(assets, id, retained.Child.Key, false, asset!.MaterializedAttachments);
                        }
                }
            }
            if (document.BuildingIntent is { } intent)
            {
                count.Children = AddProduct(count.Children, intent.Lights.Count + (long)intent.Markers.Count + intent.RoofLinks.Count + intent.RoofGroups.Count + intent.Storeys.Count, 1);
                count.GeometryElements = AddProduct(count.GeometryElements, intent.Lights.Count + (long)intent.Markers.Count, 1);
                count.ReferenceEdges = AddProduct(count.ReferenceEdges, intent.Roofs.Count, 1);
                foreach (var light in intent.Lights) { count.ReferenceEdges = AddProduct(count.ReferenceEdges, light.Owner is null ? 0 : 1, 1); count.Text(light.Id); }
                foreach (var marker in intent.Markers) { count.Text(marker.Id); count.Text(marker.Name); foreach (var tag in marker.Tags) count.Text(tag); }
                foreach (var link in intent.RoofLinks) count.ReferenceEdges = AddProduct(count.ReferenceEdges, 1L + link.Rooms.Count + link.Groups.Count, 1);
                foreach (var group in intent.RoofGroups) count.ReferenceEdges = AddProduct(count.ReferenceEdges, group.Rooms.Count, 1);
            }
            if (document.Water is { } water)
            {
                count.Children = AddProduct(count.Children, water.Bodies.Count + (long)water.Media.Count, 1);
                foreach (var body in water.Bodies)
                {
                    count.GeometryElements = AddProduct(count.GeometryElements, body.Domain.Rects.Count, 4);
                    foreach (var ring in body.Domain.Rings) count.GeometryElements = AddProduct(count.GeometryElements, ring.Vertices.Count, 1);
                    count.ReferenceEdges = AddProduct(count.ReferenceEdges, 2L * body.Covers.Count + 2, 1);
                }
            }
            count.Check();
            return new(count.Instances, count.Children, count.ReferenceEdges, count.Patches, count.Records,
                count.GeometryElements, count.Bindings, count.Overrides, count.Transfers, count.PayloadBytes);
        }
        catch (OverflowException) { throw new MapDocumentException("Prefab expansion preflight count overflow."); }
    }

    internal static long AddProduct(long accumulated, long items, long copies)
    {
        if (accumulated < 0 || items < 0 || copies < 0) throw new ArgumentOutOfRangeException(nameof(items));
        try { return checked(accumulated + checked(items * copies)); }
        catch (OverflowException) { throw new MapDocumentException("Prefab expansion preflight count overflow."); }
    }

    sealed class Counter(MapPrefabExpansionLimits limits, IReadOnlyList<MapPrefabAttachmentTransfer>? standaloneAttachments)
    {
        internal long Instances, Children, ReferenceEdges, Patches, Records, GeometryElements, Bindings, Overrides, Transfers, PayloadBytes;
        internal void Text(string? value)
        {
            if (value is null) return;
            PayloadBytes = AddProduct(PayloadBytes, Encoding.UTF8.GetByteCount(value), 1);
            Check();
        }
        internal void Check()
        {
            if (DemandDiagnostics.Check(this, "MapPrefabPreflight.MaxInstances.1", "MaxInstances", 0, Instances, limits.MaxInstances, Instances > limits.MaxInstances, CapacityDemandLifetime.Inventory) || DemandDiagnostics.Check(this, "MapPrefabPreflight.MaxChildren.1", "MaxChildren", 0, Children, limits.MaxChildren, Children > limits.MaxChildren, CapacityDemandLifetime.Inventory) || DemandDiagnostics.Check(this, "MapPrefabPreflight.MaxReferenceEdges.1", "MaxReferenceEdges", 0, ReferenceEdges, limits.MaxReferenceEdges, ReferenceEdges > limits.MaxReferenceEdges, CapacityDemandLifetime.Inventory) ||
                DemandDiagnostics.Check(this, "MapPrefabPreflight.MaxPatches.1", "MaxPatches", 0, Patches, limits.MaxPatches, Patches > limits.MaxPatches, CapacityDemandLifetime.Inventory) || DemandDiagnostics.Check(this, "MapPrefabPreflight.MaxRecords.1", "MaxRecords", 0, Records, limits.MaxRecords, Records > limits.MaxRecords, CapacityDemandLifetime.Inventory) || DemandDiagnostics.Check(this, "MapPrefabPreflight.MaxGeometryElements.1", "MaxGeometryElements", 0, GeometryElements, limits.MaxGeometryElements, GeometryElements > limits.MaxGeometryElements, CapacityDemandLifetime.Inventory) ||
                DemandDiagnostics.Check(this, "MapPrefabPreflight.MaxBindings.1", "MaxBindings", 0, Bindings, limits.MaxBindings, Bindings > limits.MaxBindings, CapacityDemandLifetime.Inventory) || DemandDiagnostics.Check(this, "MapPrefabPreflight.MaxOverrides.1", "MaxOverrides", 0, Overrides, limits.MaxOverrides, Overrides > limits.MaxOverrides, CapacityDemandLifetime.Inventory) || DemandDiagnostics.Check(this, "MapPrefabPreflight.MaxTransfers.1", "MaxTransfers", 0, Transfers, limits.MaxTransfers, Transfers > limits.MaxTransfers, CapacityDemandLifetime.Inventory) || DemandDiagnostics.Check(this, "MapPrefabPreflight.MaxPayloadBytes.1", "MaxPayloadBytes", 0, PayloadBytes, limits.MaxPayloadBytes, PayloadBytes > limits.MaxPayloadBytes, CapacityDemandLifetime.Inventory))
                throw new MapDocumentException("Prefab expansion preflight limit exceeded.");
        }
        internal void Child(MapAssetClosure assets, MapPrefabChild child, IReadOnlyList<MapPrefabLocalMaterialization>? rows, bool expandResources = true)
        {
            Children = checked(Children + 1);
            Text(child.Key.Key);
            ReferenceEdges = checked(ReferenceEdges + child.References.Count);
            Check();
            switch (child)
            {
                case MapPrefabSurfaceChild surface:
                    Surface(surface.Surface);
                    foreach (var patch in surface.Patches) Patch(patch, surface.Surface);
                    break;
                case MapPrefabRecordChild record: Record(record.Value); break;
                case MapPrefabPlacementChild placement:
                    foreach (var tag in placement.Value.Tags) Text(tag);
                    if (expandResources) Placement(assets, placement.Value, rows);
                    else { Text(placement.Value.AssetId); ReferenceEdges = checked(ReferenceEdges + 1 + (placement.Value.SupportBinding is null ? 0 : 1)); }
                    break;
                case MapPrefabWaterChild water:
                    GeometryElements = checked(GeometryElements + 4L * water.Value.Domain.Rects.Count);
                    foreach (var ring in water.Value.Domain.Rings) GeometryElements = checked(GeometryElements + ring.Vertices.Count);
                    break;
                case MapPrefabLightChild light:
                    ReferenceEdges = checked(ReferenceEdges + (light.Value.Owner is null ? 0 : 1));
                    GeometryElements = checked(GeometryElements + 1); break;
                case MapPrefabMarkerChild: GeometryElements = checked(GeometryElements + 1); break;
                case MapPrefabRoofChild roof:
                    ReferenceEdges = checked(ReferenceEdges + 1 + roof.Value.Rooms.Count + roof.Value.Groups.Count); break;
                case MapPrefabRoofGroupChild group: ReferenceEdges = checked(ReferenceEdges + group.Value.Rooms.Count); break;
            }
            Check();
        }
        internal void Placement(MapAssetClosure assets, MapPlacement placement, IReadOnlyList<MapPrefabLocalMaterialization>? rows, bool allowOpaque = false) =>
            PlacementAsset(assets, placement.AssetId!, placement.Id, placement.SupportBinding is not null, rows, allowOpaque);
        internal void PlacementAsset(MapAssetClosure assets, string assetId, string placementId, bool binding,
            IReadOnlyList<MapPrefabLocalMaterialization>? rows, bool allowOpaque = false)
        {
            MapResolvedAsset leaf = assets.GetAsset(assetId);
            ReferenceEdges = checked(ReferenceEdges + 1 + leaf.MaterialResourceIds.Count + leaf.LodResourceIds.Count +
                leaf.SupportResourceIds.Count + leaf.LightResourceIds.Count + (leaf.CollisionResourceId is null ? 0 : 1) +
                (leaf.SelectionResourceId is null ? 0 : 1) + (binding ? 1 : 0));
            Check();
            bool Suppressed(MapPrefabAttachmentKind kind, string id)
            {
                if (rows is not null) foreach (var row in rows)
                    if (row.Placement.Id == placementId && row.Kind == kind && row.ResourceId == id) return true;
                if (rows is null && standaloneAttachments is not null)
                    foreach (var row in standaloneAttachments)
                        if (row.PlacementId == placementId && row.Kind == kind && row.ResourceId == id) return true;
                return false;
            }
            foreach (string id in leaf.SupportResourceIds)
            {
                if (Suppressed(MapPrefabAttachmentKind.Support, id) || allowOpaque && !assets.HasSupport(id)) continue;
                var support = assets.BorrowSupport(id);
                Children = checked(Children + support.Surfaces.Refs.Count);
                ReferenceEdges = checked(ReferenceEdges + support.References.Count);
                foreach (var surface in support.Surfaces.Refs) Surface(surface);
                foreach (var patch in support.Surfaces.Patches.Values)
                {
                    Children = checked(Children + patch.Records.Count);
                    ReferenceEdges = AddProduct(ReferenceEdges, patch.Records.Count, 1);
                    Patch(patch, support.Surfaces.Refs.Find(s => s.Id == patch.Key.SurfaceId)!);
                }
                Check();
            }
            foreach (string id in leaf.LightResourceIds)
            {
                if (Suppressed(MapPrefabAttachmentKind.Light, id) || allowOpaque && !assets.HasLight(id)) continue;
                Children = checked(Children + 1);
                GeometryElements = checked(GeometryElements + 1);
                ReferenceEdges = checked(ReferenceEdges + (assets.BorrowLight(id).Owner is null ? 0 : 1));
                Check();
            }
        }
        internal void Surface(MapSurfaceRef surface) => ReferenceEdges = checked(ReferenceEdges + (surface.IndoorSpan is null ? 0 : 2) +
            (surface.PaintTargetSurfaceId is null ? 0 : 1));
        internal void Patch(MapSurfacePatch patch, MapSurfaceRef surface, bool references = false)
        {
            Patches = checked(Patches + 1);
            GeometryElements = checked(GeometryElements + patch.Heights.LongLength + patch.Cells.LongLength + patch.EdgeSubdivisions.Count);
            for (int z = 0; z < patch.Depth; z++)
                for (int x = 0; x < patch.Width; x++)
                    GeometryElements = AddProduct(GeometryElements, MapSurfaceCompiler.CountFaces(surface, patch, (z + patch.CellMinZ) * 64 + x + patch.CellMinX), 1);
            foreach (var edge in patch.EdgeSubdivisions) GeometryElements = AddProduct(GeometryElements, edge.Segments - 1, 1);
            ReferenceEdges = AddProduct(ReferenceEdges, patch.CornerDependencies.Count, 1);
            foreach (var record in patch.Records) Record(record, references);
            Check();
        }
        internal void Record(MapTopologyRecord record, bool references = false)
        {
            Records = checked(Records + 1);
            if (references)
            {
                foreach (var _ in MapSurfaceReferences.Records(record)) ReferenceEdges = AddProduct(ReferenceEdges, 2, 1);
                ReferenceEdges = AddProduct(ReferenceEdges, record switch
                {
                    MapBoundaryChain chain => chain.Vertices.Count + (chain.SourcePatch is null ? 0L : 1L),
                    MapCavePortal portal => portal.Interval.Count,
                    MapPrismDomain => 1L,
                    MapSpaceFootprint footprint => 1L + (footprint.Lower.SurfaceId is null ? 0 : 1) + (footprint.Upper.SurfaceId is null ? 0 : 1),
                    MapHorizontalOpening => 1L,
                    MapSurfaceSeam seam => 6L + 2L * seam.Pairs.Count,
                    _ => 0L,
                }, 1);
            }
            switch (record)
            {
                case MapPrismDomain prism:
                    GeometryElements = checked(GeometryElements + 4L * prism.Footprint.Rects.Count + prism.Footprint.Rings.Sum(r => (long)r.Vertices.Count));
                    break;
                case MapSpaceFootprint footprint: GeometryElements = checked(GeometryElements + footprint.SlotCells.Count); break;
                case MapBoundaryChain chain: GeometryElements = checked(GeometryElements + chain.Vertices.Count); break;
                case MapCavePortal portal: GeometryElements = checked(GeometryElements + portal.Interval.Count); break;
                case MapHorizontalOpening opening: GeometryElements = checked(GeometryElements + opening.SlotCells.Count); break;
                case MapSurfaceSeam seam: GeometryElements = checked(GeometryElements + 2L * seam.Pairs.Count); break;
            }
            Check();
        }
    }
}
