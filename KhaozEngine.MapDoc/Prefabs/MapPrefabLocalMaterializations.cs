using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
                Fail($"Materialized row for '{row.Placement.Id}' names resource '{row.ResourceId}', which its leaf does not advertise as {row.Kind}.");
            MapResolvedResource resource = assets.GetResource(row.ResourceId);
            if (resource.Reference.Sha256 != row.ResourceSha256)
                Fail($"Materialized row for '{row.Placement.Id}' pins resource '{row.ResourceId}' to a different digest.");
        }
    }

    /// <summary>The closure-free checks the payload codec applies to every row.</summary>
    internal static void RequireStructure(MapPrefabAsset asset)
    {
        if (asset.MaterializedAttachments is null) Fail("Materialized attachment rows must not be null.");
        var children = asset.Children.ToDictionary(c => c.Key);
        var edges = new HashSet<(string, MapPrefabAttachmentKind, string)>();
        var owned = new HashSet<MapPrefabChildKey>();
        var frames = new Dictionary<string, MapEffectiveTransform>(StringComparer.Ordinal);
        foreach (MapPrefabLocalMaterialization row in asset.MaterializedAttachments)
        {
            if (row is null || row.Placement is null || row.Children is null) Fail("Materialized rows and their children must not be null.");
            Local(row.Placement, MapPrefabChildKind.Placement);
            if (!children.ContainsKey(new(MapPrefabChildKind.Placement, row.Placement.Id))) Fail($"Materialized row names undeclared placement '{row.Placement.Id}'.");
            if (string.IsNullOrWhiteSpace(row.ResourceId) || !Enum.IsDefined(row.Kind)) Fail("Materialized rows require a resource ID and a defined kind.");
            if (row.ResourceSha256 is not { Length: 64 } sha || sha.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                Fail("Materialized rows require a lowercase SHA-256 resource digest.");
            Pose(row.PlacementFrame);
            if (!edges.Add((row.Placement.Id, row.Kind, row.ResourceId))) Fail($"Duplicate materialized edge for '{row.Placement.Id}'.");
            if (frames.TryGetValue(row.Placement.Id, out var frame) && frame != row.PlacementFrame)
                Fail($"Materialized rows for '{row.Placement.Id}' must share one placement frame.");
            frames[row.Placement.Id] = row.PlacementFrame;
            var surfaces = row.Children.Where(c => c?.Child?.Kind == MapPrefabChildKind.Surface).Select(c => c.Child.Id).ToHashSet(StringComparer.Ordinal);
            foreach (MapPrefabLocalMaterializedChild captured in row.Children)
            {
                if (captured is null || captured.Child is null) Fail("Materialized children must not be null.");
                bool kind = row.Kind == MapPrefabAttachmentKind.Support
                    ? captured.Child.Kind is MapPrefabChildKind.Surface or MapPrefabChildKind.Record
                    : captured.Child.Kind == MapPrefabChildKind.Light;
                if (!kind) Fail($"Materialized {row.Kind} row holds a {captured.Child.Kind} child.");
                Local(captured.Child, captured.Child.Kind);
                var key = new MapPrefabChildKey(captured.Child.Kind, captured.Child.Id);
                if (!children.TryGetValue(key, out MapPrefabChild? definition)) Fail($"Materialized row names undeclared child '{captured.Child.Id}'.");
                if (!owned.Add(key)) Fail($"Materialized child '{captured.Child.Id}' has more than one producer.");
                Pose(captured.OwnerLocalPose);
                if (captured.SourceDigest != MapPrefabIdentity.ChildDigest(definition))
                    Fail($"Materialized child '{captured.Child.Id}' does not match its captured definition.");
                if (definition is MapPrefabRecordChild record && !surfaces.Contains(record.Anchor.SurfaceId))
                    Fail($"Materialized record '{captured.Child.Id}' must be anchored on a surface captured by the same row.");
            }
        }
    }

    static void Local(MapPrefabReference reference, MapPrefabChildKind kind)
    {
        if (reference.Scope != MapPrefabReferenceScope.Local || reference.Kind != kind || string.IsNullOrWhiteSpace(reference.Id))
            Fail("Materialized rows hold only typed Local references.");
    }

    static void Pose(MapEffectiveTransform value)
    {
        if (!double.IsFinite(value.X) || !double.IsFinite(value.Y) || !double.IsFinite(value.Z) ||
            !double.IsFinite(value.YawRadians) || !double.IsFinite(value.Scale) || value.Scale <= 0)
            Fail("Materialized frames must be finite with positive uniform scale.");
    }

    [DoesNotReturn]
    static void Fail(string message) => throw new MapDocumentException(message);
}
