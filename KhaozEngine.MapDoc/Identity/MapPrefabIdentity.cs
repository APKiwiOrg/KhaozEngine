using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Identity;

/// <summary>Prefab identity checks shared by the loaded and stored identity paths: schema, document roots against the
/// closure, numeric reservations and high-water mark, pins, complete bindings, overrides and the ownership
/// registry, without geometric containment. Each entry point runs exactly the checks its whole identity path runs.
/// Both paths reach the closure-bound prefab validation, which refuses duplicate effective marker IDs and names from
/// root globals and verified prefab payloads before any patch read. Root globals already hash all prefab state once, so
/// nothing here adds identity inputs.</summary>
internal static class MapPrefabIdentity
{
    /// <summary>The complete loaded-document validation the loaded identity path runs.</summary>
    internal static void Require(MapDocument document, MapAssetClosure assets)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assets);
        MapBoundDocumentValidation.Validate(document, assets);
    }

    /// <summary>The ownership registry over a whole loaded document, after its prefab structure and closure checks,
    /// including global marker ID and name uniqueness.</summary>
    internal static void RequireOwnership(MapDocument document, MapAssetClosure assets)
    {
        MapPrefabValidation.RequireDocument(document, assets);
        var registry = new MapPrefabIdentityRegistry(document);
        registry.AddSurfaces(document.Surfaces.Refs.Select(s => s.Id));
        foreach (var (key, patch) in document.Surfaces.Patches) registry.AddRecords(key, patch.Records.Select(r => r.Id));
        registry.AddPlacements(document.Placements);
        registry.Finish();
    }

    /// <summary>The complete pinned-storage validation the stored identity path runs. Its manifest checks validate the
    /// pinned globals with the same closure-bound prefab and marker rules as the loaded path.</summary>
    internal static void RequireStored(MapStoredSurfaceSource source, MapAssetClosure assets)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(assets);
        source.RequireIdentityManifest(assets);
        _ = source.IdentityContentDigest(assets);
    }

    /// <summary>The canonical semantic digest of one effective child: key, payload, local pose and scoped slots.</summary>
    internal static string ChildDigest(MapPrefabChild child)
    {
        ArgumentNullException.ThrowIfNull(child);
        return MapPrefabJson.Digest("kemap/prefab-child/1", child);
    }
}

/// <summary>One identity registry across standalone records, live and removed bindings, transfers and attachment rows.
/// The loaded path feeds it the whole document. The stored path feeds it pinned surfaces, index record IDs and each
/// verified tile, without reading geometry.</summary>
internal sealed class MapPrefabIdentityRegistry
{
    readonly bool _active;
    readonly MapPrefabSet? _set;
    readonly IReadOnlyList<MapStandaloneResourceTransform> _poses;
    readonly Dictionary<(MapPrefabChildKind, string), (MapPrefabInstance Instance, MapPrefabBinding Binding)> _bound = new();
    readonly Dictionary<(MapPrefabChildKind, string), MapPrefabBindingTransfer> _transfers = new();
    readonly Dictionary<(MapPrefabChildKind, string), int> _owners = new();
    readonly Dictionary<string, MapPatchKey> _anchors = new(StringComparer.Ordinal);
    readonly HashSet<string> _placements = new(StringComparer.Ordinal);

    internal MapPrefabIdentityRegistry(MapDocument globals)
    {
        _set = globals.Prefabs;
        _active = _set is not null;
        _poses = globals.NativeResourceTransforms ?? [];
        if (_set is null) return;
        foreach (MapPrefabInstance instance in _set.Instances.OrderBy(i => i.Id, StringComparer.Ordinal))
            foreach (MapPrefabBinding binding in instance.Bindings.Entries)
                if (!_bound.TryAdd((binding.Child.Kind, binding.WorldId), (instance, binding)))
                    Fail($"Prefab world {binding.Child.Kind} ID '{binding.WorldId}' is bound more than once.");
        foreach (MapPrefabBindingTransfer transfer in _set.Transfers)
        {
            var key = (transfer.Binding.Child.Kind, transfer.Binding.WorldId);
            if (_bound.TryGetValue(key, out var reserved) &&
                (!reserved.Instance.Removed || !reserved.Instance.Unpacked || reserved.Instance.Id != transfer.InstanceId || reserved.Binding.Removed || reserved.Binding != transfer.Binding) ||
                !_transfers.TryAdd(key, transfer))
                Fail($"Prefab world {key.Kind} ID '{key.WorldId}' is reserved more than once.");
        }
        foreach (var (kind, id) in GlobalStandalone(globals)) Add(kind, id);
    }

    /// <summary>Standalone identities that live in the document globals rather than in surfaces or tiles.</summary>
    internal static IEnumerable<(MapPrefabChildKind Kind, string Id)> GlobalStandalone(MapDocument document)
    {
        if (document.Water is { } water)
        {
            foreach (var medium in water.Media) yield return (MapPrefabChildKind.WaterMedium, medium.Key);
            foreach (var body in water.Bodies) yield return (MapPrefabChildKind.WaterBody, body.Id);
        }
        if (document.BuildingIntent is not { } intent) yield break;
        foreach (var light in intent.Lights) yield return (MapPrefabChildKind.Light, light.Id);
        foreach (var marker in intent.Markers) yield return (MapPrefabChildKind.Marker, marker.Id);
        foreach (var link in intent.RoofLinks) yield return (MapPrefabChildKind.RoofLink, link.Id);
        foreach (var group in intent.RoofGroups) yield return (MapPrefabChildKind.RoofGroup, group.Id);
        foreach (var storey in intent.Storeys.Where(s => s.InstanceId is null)) yield return (MapPrefabChildKind.Storey, storey.Id);
    }

    internal void AddSurfaces(IEnumerable<string> ids)
    {
        if (!_active) return;
        foreach (string id in ids) Add(MapPrefabChildKind.Surface, id);
    }

    internal void AddRecords(MapPatchKey anchor, IEnumerable<string> ids)
    {
        if (!_active) return;
        foreach (string id in ids)
        {
            Add(MapPrefabChildKind.Record, id);
            _anchors[id] = anchor;
        }
    }

    internal void AddPlacements(IEnumerable<MapPlacement> placements)
    {
        if (!_active) return;
        foreach (MapPlacement placement in placements)
        {
            Add(MapPrefabChildKind.Placement, placement.Id);
            _placements.Add(placement.Id);
            if (_transfers.TryGetValue((MapPrefabChildKind.Placement, placement.Id), out var transfer) && transfer.Binding.NumericId != placement.NumericId)
                Fail($"Transfer '{placement.Id}' numeric ID must equal its standalone placement's numeric ID.");
        }
    }

    internal void Finish()
    {
        if (!_active) return;
        foreach (var (key, transfer) in _transfers.OrderBy(t => t.Key.Item2, StringComparer.Ordinal))
            if (!transfer.Retired && _owners.GetValueOrDefault(key) != 1)
                Fail($"Active transfer '{key.Item2}' must have exactly one standalone {key.Item1} owner.");
        var producers = new HashSet<(MapPrefabChildKind, string)>();
        foreach (MapPrefabAttachmentTransfer row in _set!.Attachments)
        {
            if (!row.Retired && !_placements.Contains(row.PlacementId))
                Fail($"Attachment row for '{row.PlacementId}' has no live standalone placement.");
            foreach (MapPrefabAttachedChild child in row.Children)
            {
                var key = (child.LocalChild.Kind, child.WorldId);
                if (!producers.Add(key)) Fail($"World {key.Kind} '{child.WorldId}' has more than one attachment producer.");
                if (!_transfers.TryGetValue(key, out var transfer) || transfer.Binding.Child != child.LocalChild || transfer.Retired != row.Retired)
                    Fail($"Attached child '{child.WorldId}' must match one {(row.Retired ? "retired" : "active")} transfer.");
                if (row.Retired) continue;
                if (child.LocalChild.Kind == MapPrefabChildKind.Record &&
                    (child.Anchor is not { } anchor || !_anchors.TryGetValue(child.WorldId, out MapPatchKey actual) || actual != anchor))
                    Fail($"Attached record '{child.WorldId}' must declare the anchor of its standalone record.");
                MapStandaloneResourceTransform? pose = _poses.FirstOrDefault(p => p.Kind == child.LocalChild.Kind && p.OwnerId == child.WorldId);
                if (pose is null) Fail($"Attached child '{child.WorldId}' has no standalone descriptor pin.");
                if (pose.SourceDigest != child.SourceDigest)
                    Fail($"Attached child '{child.WorldId}' descriptor digest differs from its standalone pin.");
            }
        }
    }

    void Add(MapPrefabChildKind kind, string id)
    {
        if (_bound.TryGetValue((kind, id), out var reserved) && !_transfers.ContainsKey((kind, id)))
            Fail($"Standalone {kind} '{id}' collides with a binding of prefab instance '{reserved.Instance.Id}'.");
        if (!_transfers.TryGetValue((kind, id), out var transfer)) return;
        if (transfer.Retired) Fail($"Retired transfer '{id}' still has a live standalone {kind} owner.");
        _owners[(kind, id)] = _owners.GetValueOrDefault((kind, id)) + 1;
    }

    [DoesNotReturn]
    static void Fail(string message) => throw new MapDocumentException(message);
}
