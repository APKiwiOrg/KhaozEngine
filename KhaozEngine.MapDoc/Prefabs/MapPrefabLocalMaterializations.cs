using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Identity;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>One captured explicit child of an asset-local materialized attachment, with its owner-relative pose
/// and the canonical digest of the captured definition.</summary>
public sealed record MapPrefabLocalMaterializedChild(MapPrefabReference Child, MapEffectiveTransform OwnerLocalPose, string SourceDigest);

/// <summary>An asset-local disposition for one (placement, kind, resource) attachment edge. The row suppresses the
/// implicit enumeration of that edge. Its children name captured explicit definitions in the asset.</summary>
public sealed record MapPrefabLocalMaterialization(MapPrefabReference Placement, string ResourceId, string ResourceSha256,
    MapPrefabAttachmentKind Kind, MapEffectiveTransform PlacementFrame, IReadOnlyList<MapPrefabLocalMaterializedChild> Children);

/// <summary>Validation of asset-local materialization rows. Rows add no child kind and no reservation.</summary>
public static class MapPrefabLocalMaterializations
{
    /// <summary>Checks rows against the asset and the verified closure edge each owner advertises.</summary>
    public static void Validate(MapPrefabAsset asset, MapAssetClosure assets)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(assets);
        RequireStructure(asset);
        var children = asset.Children.ToDictionary(c => c.Key);
        foreach (MapPrefabLocalMaterialization row in asset.MaterializedAttachments)
        {
            var owner = (MapPrefabPlacementChild)children[new(MapPrefabChildKind.Placement, row.Placement.Id)];
            MapResolvedAsset leaf = assets.GetAsset(owner.Value.AssetId!);
            IReadOnlyList<string> edges = row.Kind == MapPrefabAttachmentKind.Support ? leaf.SupportResourceIds : leaf.LightResourceIds;
            if (!edges.Contains(row.ResourceId, StringComparer.Ordinal))
                throw new MapDocumentException(MapDocumentRefusal.AuthoredOwnershipMismatch,
                    $"Materialized row for '{row.Placement.Id}' names resource '{row.ResourceId}', which its leaf does not advertise as {row.Kind}.")
                { AssetId = asset.Id, PlacementId = row.Placement.Id, ResourceId = row.ResourceId, AttachmentKind = row.Kind };
            MapResolvedResource resource = assets.GetResource(row.ResourceId);
            if (resource.Reference.Sha256 != row.ResourceSha256)
                throw new MapDocumentException(MapDocumentRefusal.DigestMismatch,
                    $"Materialized row for '{row.Placement.Id}' pins resource '{row.ResourceId}' to a different digest.")
                {
                    AssetId = asset.Id,
                    PlacementId = row.Placement.Id,
                    ResourceId = row.ResourceId,
                    AttachmentKind = row.Kind,
                    DigestKind = "resource bytes",
                    ExpectedHash = row.ResourceSha256,
                    ActualHash = resource.Reference.Sha256
                };
        }
    }

    /// <summary>The closure-free checks the payload codec applies to every row.</summary>
    internal static void RequireStructure(MapPrefabAsset asset)
    {
        if (asset.MaterializedAttachments is null)
            throw new MapDocumentException(MapDocumentRefusal.SchemaValueRequired, "Materialized attachment rows must not be null.")
            { AssetId = asset.Id, FieldPath = "$.materializedAttachments" };
        var children = asset.Children.ToDictionary(c => c.Key);
        var edges = new HashSet<(string, MapPrefabAttachmentKind, string)>();
        var owned = new HashSet<MapPrefabChildKey>();
        var frames = new Dictionary<string, MapEffectiveTransform>(StringComparer.Ordinal);
        for (int i = 0; i < asset.MaterializedAttachments.Count; i++)
        {
            MapPrefabLocalMaterialization row = asset.MaterializedAttachments[i];
            string path = "$.materializedAttachments[" + i + "]";
            if (row is null || row.Placement is null || row.Children is null)
                throw new MapDocumentException(MapDocumentRefusal.SchemaValueRequired, "Materialized rows and their children must not be null.")
                {
                    AssetId = asset.Id,
                    FieldPath = row is null ? path : row.Placement is null ? path + ".placement" : path + ".children",
                    PlacementId = row?.Placement?.Id
                };
            Local(row.Placement, MapPrefabChildKind.Placement, asset.Id, row, path + ".placement");
            if (!children.ContainsKey(new(MapPrefabChildKind.Placement, row.Placement.Id)))
                throw new MapDocumentException(MapDocumentRefusal.ReferenceMissing, $"Materialized row names undeclared placement '{row.Placement.Id}'.")
                {
                    AssetId = asset.Id,
                    PlacementId = row.Placement.Id,
                    EntityId = row.Placement.Id,
                    ChildKind = MapPrefabChildKind.Placement,
                    FieldPath = path + ".placement"
                };
            if (string.IsNullOrWhiteSpace(row.ResourceId) || !Enum.IsDefined(row.Kind))
                throw new MapDocumentException(string.IsNullOrWhiteSpace(row.ResourceId) ? MapDocumentRefusal.SchemaValueRequired : MapDocumentRefusal.SchemaEnumUnsupported,
                    "Materialized rows require a resource ID and a defined kind.")
                {
                    AssetId = asset.Id,
                    PlacementId = row.Placement.Id,
                    ResourceId = row.ResourceId,
                    FieldPath = path + (string.IsNullOrWhiteSpace(row.ResourceId) ? ".resourceId" : ".kind"),
                    EnumType = string.IsNullOrWhiteSpace(row.ResourceId) ? null : typeof(MapPrefabAttachmentKind),
                    ActualInteger = string.IsNullOrWhiteSpace(row.ResourceId) ? null : (long)row.Kind
                };
            if (row.ResourceSha256 is not { Length: 64 } sha || sha.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new MapDocumentException(MapDocumentRefusal.DigestMalformed, "Materialized rows require a lowercase SHA-256 resource digest.")
                {
                    AssetId = asset.Id,
                    PlacementId = row.Placement.Id,
                    ResourceId = row.ResourceId,
                    ActualHash = row.ResourceSha256,
                    FieldPath = path + ".resourceSha256"
                };
            Pose(row.PlacementFrame, asset.Id, row, row.Placement, path + ".placementFrame");
            if (!edges.Add((row.Placement.Id, row.Kind, row.ResourceId)))
                throw new MapDocumentException(MapDocumentRefusal.IdentityDuplicate, $"Duplicate materialized edge for '{row.Placement.Id}'.")
                { AssetId = asset.Id, PlacementId = row.Placement.Id, ResourceId = row.ResourceId, AttachmentKind = row.Kind, FieldPath = path };
            if (frames.TryGetValue(row.Placement.Id, out var frame) && frame != row.PlacementFrame)
                throw new MapDocumentException(MapDocumentRefusal.PrefabMaterializedFrameMismatch, $"Materialized rows for '{row.Placement.Id}' must share one placement frame.")
                {
                    AssetId = asset.Id,
                    PlacementId = row.Placement.Id,
                    ActualTransform = row.PlacementFrame,
                    ExpectedTransform = frame,
                    FieldPath = path + ".placementFrame"
                };
            frames[row.Placement.Id] = row.PlacementFrame;
            var surfaces = row.Children.Where(c => c?.Child?.Kind == MapPrefabChildKind.Surface).Select(c => c.Child.Id).ToHashSet(StringComparer.Ordinal);
            for (int j = 0; j < row.Children.Count; j++)
            {
                MapPrefabLocalMaterializedChild captured = row.Children[j];
                string childPath = path + ".children[" + j + "]";
                if (captured is null || captured.Child is null)
                    throw new MapDocumentException(MapDocumentRefusal.SchemaValueRequired, "Materialized children must not be null.")
                    { AssetId = asset.Id, PlacementId = row.Placement.Id, FieldPath = captured is null ? childPath : childPath + ".child" };
                bool kind = row.Kind == MapPrefabAttachmentKind.Support
                    ? captured.Child.Kind is MapPrefabChildKind.Surface or MapPrefabChildKind.Record
                    : captured.Child.Kind == MapPrefabChildKind.Light;
                if (!kind)
                    throw new MapDocumentException(MapDocumentRefusal.AuthoredReferenceKindInvalid, $"Materialized {row.Kind} row holds a {captured.Child.Kind} child.")
                    {
                        AssetId = asset.Id,
                        PlacementId = row.Placement.Id,
                        EntityId = captured.Child.Id,
                        ChildKind = captured.Child.Kind,
                        AttachmentKind = row.Kind,
                        FieldPath = childPath + ".child"
                    };
                Local(captured.Child, captured.Child.Kind, asset.Id, row, childPath + ".child");
                var key = new MapPrefabChildKey(captured.Child.Kind, captured.Child.Id);
                if (!children.TryGetValue(key, out MapPrefabChild? definition))
                    throw new MapDocumentException(MapDocumentRefusal.ReferenceMissing, $"Materialized row names undeclared child '{captured.Child.Id}'.")
                    {
                        AssetId = asset.Id,
                        PlacementId = row.Placement.Id,
                        EntityId = captured.Child.Id,
                        Child = key,
                        ChildKind = key.Kind,
                        FieldPath = childPath + ".child"
                    };
                if (!owned.Add(key))
                    throw new MapDocumentException(MapDocumentRefusal.IdentityDuplicate, $"Materialized child '{captured.Child.Id}' has more than one producer.")
                    {
                        AssetId = asset.Id,
                        PlacementId = row.Placement.Id,
                        EntityId = captured.Child.Id,
                        Child = key,
                        ChildKind = key.Kind,
                        FieldPath = childPath + ".child"
                    };
                Pose(captured.OwnerLocalPose, asset.Id, row, captured.Child, childPath + ".ownerLocalPose");
                string digest = MapPrefabIdentity.ChildDigest(definition);
                if (captured.SourceDigest != digest)
                    throw new MapDocumentException(MapDocumentRefusal.DigestMismatch, $"Materialized child '{captured.Child.Id}' does not match its captured definition.")
                    {
                        AssetId = asset.Id,
                        PlacementId = row.Placement.Id,
                        EntityId = captured.Child.Id,
                        Child = key,
                        ChildKind = key.Kind,
                        DigestKind = "captured definition",
                        ExpectedHash = captured.SourceDigest,
                        ActualHash = digest,
                        FieldPath = childPath + ".sourceDigest"
                    };
                if (definition is MapPrefabRecordChild record && !surfaces.Contains(record.Anchor.SurfaceId))
                    throw new MapDocumentException(MapDocumentRefusal.PrefabMaterializedAnchorMismatch, $"Materialized record '{captured.Child.Id}' must be anchored on a surface captured by the same row.")
                    {
                        AssetId = asset.Id,
                        PlacementId = row.Placement.Id,
                        EntityId = captured.Child.Id,
                        Child = key,
                        ChildKind = key.Kind,
                        Patch = record.Anchor,
                        FieldPath = childPath + ".child"
                    };
            }
        }
    }

    static void Local(MapPrefabReference reference, MapPrefabChildKind kind, string assetId, MapPrefabLocalMaterialization row, string path)
    {
        if (reference.Scope != MapPrefabReferenceScope.Local || reference.Kind != kind || string.IsNullOrWhiteSpace(reference.Id))
            throw new MapDocumentException(reference.Scope != MapPrefabReferenceScope.Local ? MapDocumentRefusal.AuthoredValueMismatch
                : reference.Kind != kind ? MapDocumentRefusal.AuthoredReferenceKindInvalid : MapDocumentRefusal.SchemaValueRequired,
                "Materialized rows hold only typed Local references.")
            {
                AssetId = assetId,
                PlacementId = row.Placement.Id,
                EntityId = reference.Id,
                ChildKind = reference.Kind,
                ExpectedChildKind = kind,
                ReferenceScope = reference.Scope,
                ExpectedReferenceScope = MapPrefabReferenceScope.Local,
                FieldPath = path
            };
    }

    static void Pose(MapEffectiveTransform value, string assetId, MapPrefabLocalMaterialization row, MapPrefabReference subject, string path)
    {
        if (!double.IsFinite(value.X) || !double.IsFinite(value.Y) || !double.IsFinite(value.Z) ||
            !double.IsFinite(value.YawRadians) || !double.IsFinite(value.Scale) || value.Scale <= 0)
        {
            string field = !double.IsFinite(value.X) ? "x" : !double.IsFinite(value.Y) ? "y" : !double.IsFinite(value.Z) ? "z"
                : !double.IsFinite(value.YawRadians) ? "yawRadians" : "scale";
            bool range = double.IsFinite(value.Scale) && value.Scale <= 0 && field == "scale";
            throw new MapDocumentException(range ? MapDocumentRefusal.ValueOutsideRange : MapDocumentRefusal.ValueNonfinite,
                "Materialized frames must be finite with positive uniform scale.")
            {
                AssetId = assetId,
                PlacementId = row.Placement.Id,
                EntityId = subject.Id,
                ChildKind = subject.Kind,
                FieldPath = path + "." + field,
                ActualNumber = range ? value.Scale : null,
                MinimumNumber = range ? 0 : null
            };
        }
    }
}
