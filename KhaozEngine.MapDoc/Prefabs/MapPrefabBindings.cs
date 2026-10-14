using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Identity;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>A planned complete binding set and the numeric high-water mark it needs.</summary>
public sealed record MapPrefabBindingPlan(MapPrefabBindings Bindings, long HighWaterMark);

/// <summary>Pure binding planning on a detached reservation candidate. Existing keys keep their bindings, missing keys
/// become Removed and stay reserved, restore reactivates them, and new placement leaves allocate above the mark.</summary>
public static class MapPrefabBindingPlanner
{
    public static MapPrefabBindingPlan Plan(MapDocument document, MapPrefabAsset asset, MapAssetClosure assets, string instanceId,
        MapPrefabBindings existing, bool allocateNumericPlacements = true)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(existing);
        MapPrefabValidation.RequireAsset(asset);
        MapPrefabLocalMaterializations.Validate(asset, assets);
        MapPrefabInstance? instance = document.Prefabs?.Instances.FirstOrDefault(i => i.Id == instanceId);
        MapPrefabChildSet children = MapPrefabChildSet.Compute(asset, assets, instance?.Overrides ?? []);
        // A new parent-dependent instance must first be placed in the detached candidate with its ParentSpace.
        if (children.NeedsParent && instance?.ParentSpace is null)
            throw new MapDocumentException($"Prefab instance '{instanceId}' needs a ParentSpace for its rooms or $parent references.");
        var previous = new Dictionary<MapPrefabChildKey, MapPrefabBinding>();
        foreach (MapPrefabBinding binding in existing.Entries ?? throw new MapDocumentException("Existing bindings must not be null."))
            if (binding is null || !previous.TryAdd(binding.Child, binding)) throw new MapDocumentException("Existing bindings must name distinct children.");

        var keys = children.Known.Concat(children.Live.Keys).Concat(previous.Keys).Distinct()
            .OrderBy(k => k.Kind.ToString(), StringComparer.Ordinal).ThenBy(k => k.Key, StringComparer.Ordinal).ToArray();
        var used = UsedWorldIds(document, instanceId);
        var usedNumbers = UsedNumericIds(document, instanceId);
        var entries = new List<MapPrefabBinding>(keys.Length);
        var numbered = new List<int>();
        foreach (MapPrefabChildKey key in keys)
        {
            bool live = children.Live.ContainsKey(key);
            // Retained and imported bindings are accepted exactly, but only when no other owner holds their identities.
            MapPrefabBinding binding = previous.TryGetValue(key, out MapPrefabBinding? kept)
                ? kept with { Removed = !live }
                : new(key, WorldId(instanceId, key), null, !live);
            if (used.Contains((key.Kind, binding.WorldId)))
                throw new MapDocumentException($"Prefab world ID '{binding.WorldId}' collides with an existing {key.Kind} identity.");
            if (binding.NumericId is { } number && usedNumbers.Contains(number))
                throw new MapDocumentException($"Prefab numeric ID '{number}' for '{binding.WorldId}' is already reserved by another owner.");
            if (kept is null && key.Kind == MapPrefabChildKind.Placement && allocateNumericPlacements) numbered.Add(entries.Count);
            entries.Add(binding);
        }
        var duplicate = entries.GroupBy(b => (b.Child.Kind, b.WorldId)).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new MapDocumentException($"Prefab world ID '{duplicate.Key.WorldId}' is bound more than once.");

        var candidate = new MapDocument
        {
            Placements = document.Placements,
            Prefabs = document.Prefabs,
            NumericIdHighWaterMark = document.NumericIdHighWaterMark,
        };
        MapNumericIds.Reserve(candidate, entries.Where(b => b.NumericId is not null).Select(b => b.NumericId!.Value));
        try
        {
            foreach (int index in numbered) entries[index] = entries[index] with { NumericId = MapNumericIds.Allocate(candidate) };
        }
        catch (OverflowException ex)
        {
            throw new MapDocumentException("Numeric identity space exhausted. No reservation was published.", ex);
        }
        return new(new(entries.ToArray()), candidate.NumericIdHighWaterMark);
    }

    /// <summary>A new world ID, <c>prefab/instance/kind/key</c>, with every UTF-8 byte outside ASCII letters, digits,
    /// hyphen and underscore percent-escaped in uppercase hexadecimal.</summary>
    internal static string WorldId(string instanceId, MapPrefabChildKey key) => "prefab/" + Escape(instanceId) + "/" + key.Kind + "/" + Escape(key.Key);

    internal static string Escape(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (byte b in Encoding.UTF8.GetBytes(text))
        {
            if (b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'_')
                result.Append((char)b);
            else result.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return result.ToString();
    }

    // Every numeric ID another owner reserves: standalone placements, other instances' bindings and transfers.
    static HashSet<long> UsedNumericIds(MapDocument document, string instanceId)
    {
        var used = document.Placements.Where(p => p.NumericId is not null).Select(p => p.NumericId!.Value).ToHashSet();
        if (document.Prefabs is { } set)
        {
            foreach (MapPrefabInstance other in set.Instances.Where(i => i.Id != instanceId))
                foreach (MapPrefabBinding binding in other.Bindings.Entries) if (binding.NumericId is { } id) used.Add(id);
            foreach (MapPrefabBindingTransfer transfer in set.Transfers) if (transfer.Binding.NumericId is { } id) used.Add(id);
        }
        return used;
    }

    // Every identity a binding must avoid, except the planned instance's own bindings.
    static HashSet<(MapPrefabChildKind, string)> UsedWorldIds(MapDocument document, string instanceId)
    {
        var used = new HashSet<(MapPrefabChildKind, string)>();
        foreach (MapPlacement placement in document.Placements) used.Add((MapPrefabChildKind.Placement, placement.Id));
        foreach (var surface in document.Surfaces.Refs) used.Add((MapPrefabChildKind.Surface, surface.Id));
        foreach (var record in document.Surfaces.AllRecords()) used.Add((MapPrefabChildKind.Record, record.Id));
        if (document.Tiles?.Surfaces is { } index)
            foreach (var entry in index.Entries) foreach (string id in entry.RecordIds) used.Add((MapPrefabChildKind.Record, id));
        foreach (var (kind, id) in MapPrefabIdentityRegistry.GlobalStandalone(document)) used.Add((kind, id));
        if (document.Prefabs is { } set)
        {
            foreach (MapPrefabInstance other in set.Instances.Where(i => i.Id != instanceId))
                foreach (MapPrefabBinding binding in other.Bindings.Entries) used.Add((binding.Child.Kind, binding.WorldId));
            foreach (MapPrefabBindingTransfer transfer in set.Transfers) used.Add((transfer.Binding.Child.Kind, transfer.Binding.WorldId));
        }
        return used;
    }
}

/// <summary>An instance's effective children: Live is what expansion produces, including implicit leaf support and light
/// children, and Known holds every declared or added key that needs a retained binding.</summary>
internal sealed record MapPrefabChildSet(IReadOnlyDictionary<MapPrefabChildKey, MapPrefabChild> Live, IReadOnlySet<MapPrefabChildKey> Known, bool NeedsParent)
{
    internal static MapPrefabChildSet Compute(MapPrefabAsset asset, MapAssetClosure assets, IReadOnlyList<MapPrefabOverride> overrides)
    {
        var effective = asset.Children.ToDictionary(c => c.Key);
        var known = asset.Children.Select(c => c.Key).ToHashSet();
        foreach (MapPrefabOverride value in overrides)
            for (MapPrefabOverride? intent = value; intent is not null; intent = (intent as MapPrefabRemoveOverride)?.Previous)
                if (intent is MapPrefabAddOverride add) known.Add(add.Child);
        foreach (MapPrefabOverride value in overrides)
            switch (value)
            {
                case MapPrefabRemoveOverride: effective.Remove(value.Child); break;
                case MapPrefabReplaceOverride replace when effective.ContainsKey(value.Child): effective[value.Child] = replace.Value; break;
                case MapPrefabAddOverride add: effective[value.Child] = add.Value; break;
                case MapPrefabTransformOverride pose when effective.TryGetValue(value.Child, out var posed):
                    effective[value.Child] = posed is MapPrefabPlacementChild leaf ? Moved(leaf, pose.Transform, null, null) : posed with { LocalTransform = pose.Transform };
                    break;
                case MapPrefabPlacementOverride placement when effective.TryGetValue(value.Child, out var target) && target is MapPrefabPlacementChild owner:
                    effective[value.Child] = Moved(owner, placement.Transform, placement.AssetId, placement.Tags);
                    break;
            }
        var explicitChildren = effective.Values.ToArray();
        var live = new Dictionary<MapPrefabChildKey, MapPrefabChild>(effective);
        foreach (MapPrefabChild child in MapEffectiveSupport.Enumerate(assets, explicitChildren, asset.MaterializedAttachments)
                     .Concat(MapPrefabAttachedLights.Enumerate(assets, explicitChildren, asset.MaterializedAttachments)))
            if (known.Contains(child.Key) || !live.TryAdd(child.Key, child))
                throw new MapDocumentException($"Implicit {child.Key.Kind} child '{child.Key.Key}' collides with another child key.");
        bool needsParent = live.Values.Any(c => c is MapPrefabRecordChild { Value: Spaces.MapSpaceDoc } or MapPrefabWaterChild ||
            c.References.Any(r => r.Target.Id == MapPrefabReferences.Parent));
        return new(live, known, needsParent);
    }

    static MapPrefabPlacementChild Moved(MapPrefabPlacementChild child, MapTransform? transform, string? assetId, IReadOnlyList<string>? tags)
    {
        MapPlacement copy = MapPrefabReferenceWalker.Copy(child.Value, child.Value.Id, child.Value.SupportBinding);
        if (transform is { } t)
        {
            copy.X = t.Position.X;
            copy.Y = copy.Y is null ? null : t.Position.Y;
            copy.Z = t.Position.Z;
            copy.Yaw = t.YawRadians;
            copy.Scale = t.Scale;
        }
        if (assetId is not null) copy.AssetId = assetId;
        if (tags is not null) copy.Tags = tags.ToList();
        return child with { Value = copy };
    }
}
