using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Identity;

namespace KhaozEngine.MapDoc.Prefabs;

internal sealed record MapPrefabReconciledAsset(MapPrefabAsset EffectiveAsset,
    IReadOnlyDictionary<MapPrefabChildKey, string> EffectiveDigests, IReadOnlySet<MapPrefabChildKey> Removed)
{
    internal MapPrefabAsset LiveAsset => EffectiveAsset with
    {
        Children = EffectiveAsset.Children.Where(c => !Removed.Contains(c.Key)).ToArray(),
        Roofs = EffectiveAsset.Roofs.Where(r => !Removed.Contains(new(r.Kind, r.Id))).ToArray(),
        MaterializedAttachments = EffectiveAsset.MaterializedAttachments.Where(r => !Removed.Contains(new(MapPrefabChildKind.Placement, r.Placement.Id)))
            .Select(r => r with { Children = r.Children.Where(c => !Removed.Contains(new(c.Child.Kind, c.Child.Id))).ToArray() }).ToArray(),
    };
}

/// <summary>Transfers keyed pose intent into captured mathematical frames before ID remapping.</summary>
internal static class MapPrefabMaterializationOverrides
{
    internal static MapPrefabReconciledAsset Reconcile(MapPrefabAsset original, IReadOnlyList<MapPrefabOverride> overrides)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(overrides);
        // Validate the immutable captured pins before any override can hide a bad descriptor.
        MapPrefabValidation.RequireAsset(original);
        var copy = original with
        {
            Children = original.Children.Select(Clone).ToArray(),
            Roofs = original.Roofs.ToArray(),
            MaterializedAttachments = original.MaterializedAttachments.Select(r => r with { Children = r.Children.ToArray() }).ToArray(),
        };
        var children = copy.Children.ToDictionary(c => c.Key);
        foreach (var retained in overrides.OfType<MapPrefabRemoveOverride>())
            if (retained.Previous is MapPrefabAddOverride added) children.TryAdd(added.Child, Clone(added.Value));
        var removed = new HashSet<MapPrefabChildKey>();
        var frames = new Dictionary<MapPrefabChildKey, MapEffectiveTransform>();
        foreach (var value in overrides)
        {
            if (!children.TryGetValue(value.Child, out var child) && value is not MapPrefabAddOverride)
                throw new MapDocumentException("Override names an undeclared child.");
            switch (value)
            {
                case MapPrefabRemoveOverride: removed.Add(value.Child); break;
                case MapPrefabReplaceOverride replace:
                    if (replace.Value.Key != value.Child) throw new MapDocumentException("Replacement child key differs from its target.");
                    if (replace.Value.LocalTransform != child!.LocalTransform) frames[value.Child] = MapEffectiveTransform.From(replace.Value.LocalTransform);
                    if (replace.Value is MapPrefabPlacementChild p && child is MapPrefabPlacementChild old && Pose(p.Value) != Pose(old.Value))
                        frames[value.Child] = MapEffectiveTransform.From(Pose(p.Value));
                    children[value.Child] = replace.Value;
                    break;
                case MapPrefabAddOverride add:
                    if (!children.TryAdd(value.Child, add.Value)) throw new MapDocumentException("Added child already exists.");
                    break;
                case MapPrefabTransformOverride transformOverride:
                    frames[value.Child] = MapEffectiveTransform.From(transformOverride.Transform);
                    children[value.Child] = child is MapPrefabPlacementChild leaf ? Placement(leaf, transformOverride.Transform, null, null)
                        : child! with { LocalTransform = transformOverride.Transform };
                    break;
                case MapPrefabPlacementOverride placement when child is MapPrefabPlacementChild targetPlacement:
                    if (placement.Transform is { } pose) frames[value.Child] = MapEffectiveTransform.From(pose);
                    children[value.Child] = Placement(targetPlacement, placement.Transform, placement.AssetId, placement.Tags);
                    break;
            }
        }
        foreach (var row in copy.MaterializedAttachments)
            if (removed.Contains(new(MapPrefabChildKind.Placement, row.Placement.Id)))
                foreach (var captured in row.Children) removed.Add(new(captured.Child.Kind, captured.Child.Id));
        // Record pose equality is checked before captured presentation poses are normalized away.
        MapPrefabValidation.RequireAsset(copy with
        {
            Children = children.Values.Where(c => !removed.Contains(c.Key)).ToArray(),
            MaterializedAttachments = [],
            Roofs = copy.Roofs.Where(r => !removed.Contains(new(r.Kind, r.Id))).ToArray(),
        });
        var rows = copy.MaterializedAttachments.Select(row => row with
        {
            PlacementFrame = frames.TryGetValue(new(MapPrefabChildKind.Placement, row.Placement.Id), out var owner) ? owner : row.PlacementFrame,
            Children = row.Children.Select(c => c with
            {
                OwnerLocalPose = frames.TryGetValue(new(c.Child.Kind, c.Child.Id), out var pose) ? pose : c.OwnerLocalPose,
            }).ToArray(),
        }).ToArray();
        foreach (var row in rows)
            foreach (var captured in row.Children)
            {
                var key = new MapPrefabChildKey(captured.Child.Kind, captured.Child.Id);
                children[key] = children[key] with { LocalTransform = new(Vector3.Zero, 0, 1) };
            }
        rows = rows.Select(row => row with
        {
            Children = row.Children.Select(c => c with { SourceDigest = MapPrefabIdentity.ChildDigest(children[new(c.Child.Kind, c.Child.Id)]) }).ToArray(),
        }).ToArray();
        var digests = children.ToDictionary(c => c.Key, c => MapPrefabIdentity.ChildDigest(c.Value));
        foreach (var row in rows)
            foreach (var captured in row.Children)
            {
                var key = new MapPrefabChildKey(captured.Child.Kind, captured.Child.Id);
                digests[key] = MapPrefabJson.Digest("kemap/effective-frame/1", new { captured.SourceDigest, row.PlacementFrame, captured.OwnerLocalPose });
            }
        return new(copy with { Children = children.Values.ToArray(), MaterializedAttachments = rows }, digests, removed);
    }

    static MapPrefabChild Clone(MapPrefabChild child)
    {
        var (copy, _) = MapPrefabReferenceWalker.Child(child, MapPrefabReferenceMap.Identity);
        if (copy is MapPrefabPlacementChild placement) copy = placement with { Value = MapPrefabReferenceWalker.Copy(placement.Value, placement.Value.Id, placement.Value.SupportBinding) };
        if (copy is MapPrefabMarkerChild marker) copy = marker with { Value = marker.Value with { Tags = marker.Value.Tags.ToArray() } };
        return copy with { References = child.References.ToArray() };
    }
    internal static MapTransform Pose(MapPlacement placement) => new(new(placement.X, placement.Y ?? 0, placement.Z), placement.Yaw, placement.Scale);
    internal static MapPrefabPlacementChild Placement(MapPrefabPlacementChild child, MapTransform? transform, string? assetId, IReadOnlyList<string>? tags)
    {
        var p = MapPrefabReferenceWalker.Copy(child.Value, child.Value.Id, child.Value.SupportBinding);
        if (transform is { } pose)
        {
            p.X = pose.Position.X; p.Y = p.Y is null ? null : pose.Position.Y; p.Z = pose.Position.Z;
            p.Yaw = pose.YawRadians; p.Scale = pose.Scale;
        }
        if (assetId is not null) p.AssetId = assetId;
        if (tags is not null) p.Tags = tags.ToList();
        return child with { Value = p };
    }
}
