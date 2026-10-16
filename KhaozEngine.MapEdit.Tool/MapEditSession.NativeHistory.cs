using System;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapEditor;

namespace KhaozEngine.MapEdit;

public sealed partial class MapEditSession
{
    EditorHistory _nativeHistory = new();
    NativeStagedPublication? _pendingNative;
    // The staged candidate's semantic hash and tile index when ApplyNative staged it, so publication can tell whether
    // the callback edited the shared candidate afterwards.
    (string Hash, MapTileIndex? Tiles)? _pendingNativeStamp;
    bool _nativeMutation;
    long _nativeEvents;
    internal Action? BeforeNativePublication { get; set; }
    internal MapNativeEditEffects? LastNativeEffects { get; private set; }
    internal (int Cursor, int Depth, long Events) NativeHistoryState
    {
        get { lock (_lock) return (_nativeHistory.UndoDepth, _nativeHistory.UndoDepth + _nativeHistory.RedoDepth, _nativeEvents); }
    }
    /// <summary>Raised once after a native edit, undo or redo has been accepted.</summary>
    public event Action<MapNativeEditEffects>? NativeEdited;

    void ResetNativeHistory()
    {
        _nativeHistory = new() { Registry = _registry };
        _nativeHistory.RebindNativeAssets(_nativeAssets, NativePlacementBoundsProvider.Instance);
        LastNativeEffects = null;
    }
    void BindNativeAssetsLocked(MapAssetClosure assets)
    {
        bool same = _nativeAssets?.Hash == assets.Hash && _nativeAssets.Roots.SequenceEqual(assets.Roots);
        _nativeAssets = assets;
        if (!same) ResetNativeHistory();
        else _nativeHistory.RebindNativeAssets(assets, NativePlacementBoundsProvider.Instance);
    }
    internal MapNativeEditEffects ApplyNative(EditorCommand command, MapDocument document, MapDocRegistry registry)
    {
        if (!_nativeMutation || _pendingNative is not null)
            throw new MapDocumentException("Native commands require one staged session mutation.");
        _nativeHistory.Registry = registry;
        var staged = _nativeHistory.StageExecute(_doc!, command);
        NativeDocumentTransaction.Publish(document, staged);
        _pendingNative = staged;
        _pendingNativeStamp = (SemanticHash(staged.Candidate), staged.Candidate.Tiles);
        return staged.Effects;
    }
    internal MapNativeEditEffects ExecuteNativeCommand(EditorCommand command) => Mutate((doc, registry) =>
    {
        if (doc.ResolverIdentity is null) throw new MapDocumentException("Native commands require a native document.");
        return ApplyNative(command, doc, registry);
    }, command.AffectsWorld);

    internal MapNativeEditEffects ApplyNativeAssets(IEditorCommand command, MapAssetClosure assets) => ApplyNativeAssets(command, assets, null);
    internal MapNativeEditEffects ApplyNativeAssets(IEditorCommand command, MapAssetClosure assets, Action? checkpoint)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(assets);
        lock (_lock)
        {
            RequireDocumentLocked();
            var staged = _nativeHistory.StageExecute(_doc!, command);
            if (staged.Assets?.Hash != assets.Hash) throw new MapDocumentException("Asset command closure differs from its publication.");
            if (SemanticHash(_doc!) == SemanticHash(staged.Candidate)) return new(null, null, [], [], [], [], MapNativeInvalidation.None);
            return PublishNative(staged, checkpoint);
        }
    }
    T MutateNative<T>(Func<MapDocument, MapDocRegistry, T> fn, bool worldChanged)
    {
        if (_nativeAssets is null) throw new MapDocumentException("Native editing requires an explicit asset closure binding.");
        if (_nativeMutation) throw new MapDocumentException("Nested native mutations are not supported.");
        MapDocument before = NativeDocumentSnapshot.Clone(_doc!, _registry);
        MapDocument candidate = NativeDocumentSnapshot.Clone(_doc!, _registry);
        _nativeMutation = true;
        try
        {
            T result = fn(candidate, _registry);
            var staged = _pendingNative;
            string published = SemanticHash(staged?.Candidate ?? candidate);
            if (SemanticHash(before) == published) return result;
            // fn can still edit the candidate it shares with a publication it staged. Only then is that publication
            // validated again, and only then is the expansion staging built no longer the published state.
            // SemanticHash covers the format version, ID, globals including water, prefabs, building intent and resource
            // transforms, the placement and spawn lists through the world hash, the native globals including surface refs,
            // native assets and resolver identity, and every patch digest. It nulls Tiles, the storage index, which
            // expansion never reads beyond refusing a partial document. The Tiles reference is compared as well, so a
            // swapped index still counts as a change.
            bool editedAfterStaging = staged is not null && _pendingNativeStamp is { } stamp &&
                (stamp.Hash != published || !ReferenceEquals(stamp.Tiles, staged.Candidate.Tiles));
            if (staged is null)
            {
                var effects = new MapNativeEditEffects(null, null, [], [], [], [],
                    worldChanged ? MapNativeInvalidation.Terrain | MapNativeInvalidation.Nav | MapNativeInvalidation.Residency | MapNativeInvalidation.Unbounded : MapNativeInvalidation.None);
                var command = new NativeSessionSnapshotCommand("Native document edit", before, candidate, _nativeAssets, _nativeAssets, effects);
                staged = _nativeHistory.StageExecute(before, command);
            }
            PublishNative(staged, worldChanged: worldChanged, editedAfterStaging: editedAfterStaging);
            return result;
        }
        finally { _pendingNative = null; _pendingNativeStamp = null; _nativeMutation = false; }
    }
    // Staging validated the candidate against its closure, including effective water. Only a candidate edited after
    // staging is validated again.
    MapNativeEditEffects PublishNative(NativeStagedPublication staged, Action? checkpoint = null, bool worldChanged = true,
        bool editedAfterStaging = false)
    {
        if (editedAfterStaging)
        {
            MapBoundDocumentValidation.Validate(staged.Candidate, staged.Assets!, _registry);
            if (staged.Candidate.Prefabs is not null || staged.Candidate.NativeResourceTransforms is not null || staged.Candidate.BuildingIntent is not null)
                MapBoundDocumentValidation.ValidateEffectiveWater(MapPrefabResolver.Expand(staged.Candidate, staged.Assets!), staged.Assets!);
        }
        BeforeNativePublication?.Invoke();
        checkpoint?.Invoke();
        NativeDocumentTransaction.Publish(_doc!, staged);
        // A callback edit after staging makes the staged expansion stale, so it is not remembered.
        if (editedAfterStaging) NativeAcceptedExpansions.Forget(_doc!);
        _nativeAssets = staged.Assets;
        staged.Accept();
        _dirty = true;
        if (worldChanged) _field = null;
        LastNativeEffects = staged.Effects;
        _nativeEvents++;
        NativeEdited?.Invoke(staged.Effects);
        return staged.Effects;
    }
    string SemanticHash(MapDocument document)
    {
        var tiles = document.Tiles;
        document.Tiles = null;
        try
        {
            return MapCanonical.HashHex(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("displayName", document.DisplayName);
                writer.WriteString("schema", document.Schema);
                writer.WriteString("world", MapDocumentHash.OfWorld(document, _registry));
                MapCanonical.WriteNativeGlobals(writer, document, MapDocumentFile.CreateCompactOptions(_registry));
                writer.WriteStartArray("patches");
                foreach (var pair in document.Surfaces.Patches.OrderBy(p => p.Key))
                {
                    writer.WriteStartArray();
                    MapPrefabJson.Write(writer, pair.Key);
                    writer.WriteStringValue(MapDoc.Surfaces.MapSurfaceSemantics.PatchDigest(pair.Value));
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            });
        }
        finally { document.Tiles = tiles; }
    }
    internal string RequireNativeResourcePath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) throw new MapDocumentException("Prefab publication paths must be relative to the resource root.");
        string root = MapDocumentStorage.ResourceRoot(_storagePath!, _storageForm);
        string target = Path.GetFullPath(relativePath, root);
        if (!ResourcePathContainment.IsInsideRoot(Path.GetRelativePath(root, target)) ||
            MapDocumentStorage.IsReserved(_storagePath!, _storageForm, target))
            throw new MapDocumentException("Prefab publication path escapes the resource root or enters writer-owned map storage.");
        return target;
    }
    /// <summary>Undoes one accepted native edit against its captured closure. An empty history returns null.</summary>
    public MapNativeEditEffects? UndoNative()
    {
        lock (_lock)
        {
            RequireDocumentLocked();
            var staged = _nativeHistory.StageUndo(_doc!);
            return staged is null ? null : PublishNative(staged);
        }
    }
    /// <summary>Redoes one accepted native edit against its captured closure. An empty history returns null.</summary>
    public MapNativeEditEffects? RedoNative()
    {
        lock (_lock)
        {
            RequireDocumentLocked();
            var staged = _nativeHistory.StageRedo(_doc!);
            return staged is null ? null : PublishNative(staged);
        }
    }
}
