using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapEditor;

/// <summary>Detached prefab authoring with native commands and captured materialized attachment ownership.</summary>
public sealed class MapPrefabLocalSession
{
    readonly string _localInstance;
    readonly MapDocument _context;
    readonly MapAssetClosure _sourceAssets;
    readonly MapRecordRef? _parent;
    readonly string _assetId;
    readonly MapTransform _origin;
    readonly IReadOnlySet<MapPrefabChildKey> _excluded;
    readonly EditorHistory _history = new();
    MapDocument _document;
    MapAssetClosure _assets;
    // The fixed context without the source, checked and expanded once per session.
    MapDocument? _authoring;
    // The canonical export of the current document, until the next publication.
    byte[]? _exported;
    bool _cancelled;
    /// <summary>A detached snapshot of the local command document, including its stable typed binding inventory.</summary>
    public MapDocument Document => NativeDocumentSnapshot.Clone(_document, MapDocRegistry.CreateDefault());

    public MapPrefabLocalSession(MapPrefabAsset asset, MapAssetClosure assets, MapDocument authoringContext, MapRecordRef? parentSpace = null)
        : this(asset, assets, authoringContext, parentSpace, null, null) { }

    internal MapPrefabLocalSession(MapPrefabAsset asset, MapAssetClosure assets, MapDocument authoringContext, MapRecordRef? parentSpace,
        MapTransform? origin, IReadOnlySet<MapPrefabChildKey>? excluded)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(authoringContext);
        _context = NativeDocumentSnapshot.Clone(authoringContext, MapDocRegistry.CreateDefault());
        _sourceAssets = assets;
        _parent = parentSpace;
        _assetId = asset.Id;
        var source = (_context.Prefabs?.Instances ?? []).Where(i => MapAssetVersionIdentity.LogicalId("prefab", i.ResourceId) == asset.Id && i.ParentSpace == parentSpace)
            .OrderBy(i => i.Id, StringComparer.Ordinal).FirstOrDefault();
        _origin = origin ?? source?.Transform ?? new(Vector3.Zero, 0, 1);
        _excluded = excluded ?? (source?.Bindings.Entries ?? []).Select(b => new MapPrefabChildKey(b.Child.Kind, b.WorldId)).ToHashSet();
        _localInstance = MapPrefabAuthoringContext.InstanceId(_context);
        (_document, _assets) = Detach(asset);
        _history.BindNativeAssets(_document, _assets);
    }
    public MapNativeEditEffects Apply(IEditorCommand command)
    {
        RequireOpen();
        var staged = _history.StageExecute(_document, command);
        return Publish(staged);
    }
    public bool Undo()
    {
        RequireOpen();
        var staged = _history.StageUndo(_document);
        if (staged is null) return false;
        Publish(staged);
        return true;
    }
    public bool Redo()
    {
        RequireOpen();
        var staged = _history.StageRedo(_document);
        if (staged is null) return false;
        Publish(staged);
        return true;
    }
    MapNativeEditEffects Publish(NativeStagedPublication staged)
    {
        NativeDocumentTransaction.Publish(_document, staged);
        _assets = staged.Assets!;
        _exported = null;
        staged.Accept();
        return staged.Effects;
    }
    internal void Edit(MapPrefabChild child, bool remove)
    {
        RequireOpen();
        MapPrefabAsset edited = MapPrefabAssetEdits.Replace(Export(), child, remove);
        var (after, assets) = Detach(edited);
        var command = new NativeSessionSnapshotCommand("Edit prefab asset child", _document, after, _assets, assets,
            new(null, null, [], [], [], [], MapNativeInvalidation.Unbounded | MapNativeInvalidation.Residency));
        Apply(command);
    }
    /// <summary>Exports current captured definitions and frames, retaining empty suppression rows and local keys.</summary>
    public MapPrefabAsset Export()
    {
        RequireOpen();
        if (_exported is { } bytes) return MapPrefabFile.Read(bytes, _assetId);
        var instance = _document.Prefabs!.Instances.Single(i => i.Id == _localInstance);
        var schema = _assets.GetPrefab(instance.ResourceId);
        var selected = _document.Prefabs.Transfers.Where(t => t.InstanceId == _localInstance && !t.Retired && !t.Binding.Removed)
            .Select(t => new MapPrefabReference(MapPrefabReferenceScope.World, t.Binding.Child.Kind, t.Binding.WorldId)).ToArray();
        if (selected.Length == 0) return schema with { Children = [], Roofs = [], MaterializedAttachments = [] };
        var exported = MapPrefabExtraction.Extract(_document, _assets, selected, _origin, _assetId);
        var children = exported.Children.Select(child =>
        {
            var original = schema.Children.SingleOrDefault(c => c.Key == child.Key);
            if (original is null) return child;
            var (restored, occurrences) = MapPrefabReferenceWalker.Child(child, new RestoreSlots(original.References, child.References));
            return restored with { References = MapPrefabReferences.Slots(occurrences) };
        }).ToArray();
        // Extraction reads live captured rows from the document, including rows whose child edge was suppressed.
        var asset = exported with { Children = children };
        asset = asset with
        {
            MaterializedAttachments = asset.MaterializedAttachments.Select(row => row with
            {
                Children = row.Children.Select(c => c with
                { SourceDigest = MapDoc.Identity.MapPrefabIdentity.ChildDigest(children.Single(x => x.Key == new MapPrefabChildKey(c.Child.Kind, c.Child.Id))) }).ToArray()
            }).ToArray()
        };
        _exported = MapPrefabFile.Write(asset);
        return MapPrefabFile.Read(_exported, _assetId);
    }
    public void Cancel() => _cancelled = true;
    void RequireOpen()
    {
        if (_cancelled) throw new MapDocumentException("The prefab asset session is cancelled.");
    }
    (MapDocument Document, MapAssetClosure Assets) Detach(MapPrefabAsset asset)
    {
        byte[] prefabBytes = MapPrefabFile.Write(asset);
        string sha = Convert.ToHexStringLower(SHA256.HashData(prefabBytes));
        var reference = new MapAssetRef(MapAssetVersionIdentity.Address("prefab", asset.Id, prefabBytes), "prefab-local/" + sha, sha, 1);
        var roots = _sourceAssets.Roots.ToArray();
        if (roots.Length == 0) throw new MapDocumentException("Prefab editing requires an asset manifest root.");
        var root = roots[0];
        var manifest = MapAssetManifestReader.Read(_sourceAssets.GetResource(root.Id).Bytes.ToArray(), root.Id);
        var dependencies = _sourceAssets.CapturedResources.Where(r => r.Kind is not (MapResourceKind.Prefab or MapResourceKind.Manifest)).Select(r => r.Reference.Id).ToList();
        if (_sourceAssets.TryGetResource(reference.Id, out var existing)) reference = existing!.Reference;
        else manifest.Resources.Add(new() { Reference = reference, Kind = MapResourceKind.Prefab, Dependencies = dependencies });
        byte[] manifestBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestOptions());
        roots[0] = root with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes)) };
        var source = new CapturedSource(_sourceAssets, roots[0], manifestBytes, reference, prefabBytes);
        MapAssetClosure assets = MapAssetClosure.Load(roots, source);
        _authoring ??= MapPrefabAuthoringContext.Create(_context, _sourceAssets, _excluded);
        var document = NativeDocumentSnapshot.Clone(_authoring, MapDocRegistry.CreateDefault());
        document.NativeAssets = roots.ToList();
        // Context supplies authored world references and the explicit parent. Local payloads get their own typed IDs.
        var placed = MapPrefabEdits.Prepare(document, assets,
            new MapPlacePrefab(_localInstance, reference.Id, _origin, _parent, NumericLeaves: false)).After;
        var detached = MapPrefabEdits.Prepare(placed, assets, new MapUnpackPrefab(_localInstance)).After;
        return (detached, assets);
    }
    static System.Text.Json.JsonSerializerOptions ManifestOptions()
    {
        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        MapNativeJson.Configure(options);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }
    sealed class CapturedSource(MapAssetClosure assets, MapAssetRef root, byte[] rootBytes, MapAssetRef prefab, byte[] prefabBytes) : IMapAssetSource
    {
        public ReadOnlyMemory<byte> Read(MapAssetRef reference) => reference.Id == root.Id ? rootBytes : reference.Id == prefab.Id ? prefabBytes : assets.GetResource(reference.Id).Bytes;
    }
    sealed class RestoreSlots(IReadOnlyList<MapPrefabReferenceSlot> original, IReadOnlyList<MapPrefabReferenceSlot> extracted) : MapPrefabReferenceMap
    {
        internal override (MapRecordRef Value, MapPrefabReferenceScope? IdScope, MapPrefabReferenceScope? AnchorScope) Record(
            string path, MapRecordRef value, bool space)
        {
            if (original.Any(s => s.Path == path + "/id" && s.Target.Id == "$parent"))
                return (new("$parent", new("$parent", 0, 0)), MapPrefabReferenceScope.Local, MapPrefabReferenceScope.Local);
            return base.Record(path, value, space);
        }
        internal override (string Id, MapPrefabReferenceScope? Scope) Text(string path, MapPrefabChildKind kind, string id, bool space)
        {
            var slot = original.SingleOrDefault(s => s.Path == path);
            if (slot is not null && slot.Target.Id == "$parent") return (slot.Target.Id, slot.Target.Scope);
            var current = extracted.SingleOrDefault(s => s.Path == path);
            return (id, current?.Target.Scope ?? slot?.Target.Scope);
        }
    }
}
