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
    // The command's staged semantic hash and tile index, plus its history label. The callback can edit
    // unshared roots before staging, or leave shared members behind when it replaces its own roots later.
    (string Hash, MapTileIndex? Tiles, string Label)? _pendingNativeStamp;
    // Nested mutation remains refused through accepted-edit notification. Publication admission covers only fn.
    bool _nativeMutation;
    bool _nativeCallbackActive;
    MapDocument? _nativeMutationCandidate;
    // The live document's legacy list canonical for the current session mutation only, cleared when it ends.
    string? _nativeLiveLists;
    long _nativeEvents;
    internal Action? BeforeNativePublication { get; set; }
    internal MapNativeEditEffects? LastNativeEffects { get; private set; }
    /// <summary>The finalization of the last published native edit, undo or redo, null without foliage.</summary>
    internal MapDocumentFinalizationStamp? LastNativeStamp { get; private set; }
    /// <summary>The finalization of the publication staged inside the current session mutation, if any.</summary>
    internal MapDocumentFinalizationStamp? PendingNativeStamp => _pendingNative?.Stamp;
    internal (int Cursor, int Depth, long Events) NativeHistoryState
    {
        get { lock (_lock) return (_nativeHistory.UndoDepth, _nativeHistory.UndoDepth + _nativeHistory.RedoDepth, _nativeEvents); }
    }
    /// <summary>The labels of the next native undo and redo entries, null when that stack is empty.</summary>
    internal (string? Undo, string? Redo) NativeHistoryLabels
    {
        get { lock (_lock) return (_nativeHistory.UndoLabel, _nativeHistory.RedoLabel); }
    }
    /// <summary>Raised once after a native edit, undo or redo has been accepted.</summary>
    public event Action<MapNativeEditEffects>? NativeEdited;

    void ResetNativeHistory()
    {
        _nativeHistory = new() { Registry = _registry };
        _nativeHistory.RebindNativeAssets(_nativeAssets, NativePlacementBoundsProvider.Instance);
        LastNativeEffects = null;
        LastNativeStamp = null;
    }
    void BindNativeAssetsLocked(MapAssetClosure assets)
    {
        bool same = _nativeAssets?.Hash == assets.Hash && _nativeAssets.Roots.SequenceEqual(assets.Roots);
        _nativeAssets = assets;
        NewSnapshotLocked();
        if (!same) ResetNativeHistory();
        else _nativeHistory.RebindNativeAssets(assets, NativePlacementBoundsProvider.Instance);
    }
    internal MapNativeEditEffects ApplyNative(EditorCommand command, MapDocument document, MapDocRegistry registry)
    {
        if (!_nativeMutation || _pendingNative is not null)
            throw new MapDocumentException(MapDocumentRefusal.ToolStagedMutationRequired, "Native commands require one staged session mutation.")
            { DocumentId = _doc?.Id, CommandType = command?.GetType(), Operation = nameof(ApplyNative) };
        if (!ReferenceEquals(document, _nativeMutationCandidate))
            throw new MapDocumentException(MapDocumentRefusal.ToolMutationCandidateRequired,
                "Native commands must stage into the current mutation callback's detached candidate.")
            { DocumentId = _doc?.Id, CommandType = command?.GetType(), Operation = nameof(ApplyNative) };
        _nativeHistory.Registry = registry;
        var staged = _nativeHistory.StageExecute(_doc!, command, _nativeLiveLists);
        NativeDocumentTransaction.Publish(document, staged);
        _pendingNativeStamp = (SemanticHash(staged.Candidate), staged.Candidate.Tiles, command.Label);
        _pendingNative = staged;
        return staged.Effects;
    }
    internal MapNativeEditEffects ExecuteNativeCommand(EditorCommand command) => Mutate((doc, registry) =>
    {
        if (doc.ResolverIdentity is null) throw new MapDocumentException(MapDocumentRefusal.ToolNativeDocumentRequired, "Native commands require a native document.")
        { DocumentId = doc.Id, CommandType = command.GetType(), Operation = nameof(ExecuteNativeCommand), FieldPath = "ResolverIdentity" };
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
            RequireClosedNativeMutation(nameof(ApplyNativeAssets), nativePublication: true);
            string liveLists = NativeLegacySpawnAdmission.LiveLists(_doc!);
            var staged = _nativeHistory.StageExecute(_doc!, command, liveLists);
            string? actualHash = staged.Assets?.Hash;
            string expectedHash = assets.Hash;
            if (actualHash != expectedHash) throw new MapDocumentException(MapDocumentRefusal.ToolPublishedClosureMismatch,
                "Asset command closure differs from its publication.")
            { DocumentId = _doc!.Id, ExpectedHash = expectedHash, ActualHash = actualHash, Operation = nameof(ApplyNativeAssets) };
            if (SemanticHash(_doc!) == SemanticHash(staged.Candidate)) return new(null, null, [], [], [], [], MapNativeInvalidation.None);
            return PublishNative(staged, checkpoint, liveLists: liveLists);
        }
    }
    T MutateNative<T>(Func<MapDocument, MapDocRegistry, T> fn, bool worldChanged)
    {
        MapSessionAdmission.RequireNativeMutation(_nativeAssets, _nativeMutation, _doc?.Id);
        // The live source and its verified closure pass the foliage context preflight before either clone.
        MapDocumentEditFinalizer.Preflight(_doc!, _nativeAssets);
        MapDocument before = NativeDocumentSnapshot.Clone(_doc!, _registry);
        MapDocument candidate = NativeDocumentSnapshot.Clone(_doc!, _registry);
        // The live document does not change until publication, so its lists are serialized once for every check.
        string liveLists = NativeLegacySpawnAdmission.LiveLists(_doc!);
        _nativeLiveLists = liveLists;
        _nativeMutation = true;
        _nativeCallbackActive = true;
        _nativeMutationCandidate = candidate;
        try
        {
            T result;
            try { result = fn(candidate, _registry); }
            finally { _nativeCallbackActive = false; }
            var staged = _pendingNative;
            if (staged is not null) NativeLegacySpawnAdmission.RequireAdmitted(staged.Candidate, candidate, null);
            // Both candidates must still match the staged stamp. The callback may preserve an early unshared edit,
            // or restore its own root after editing a shared member that remains changed in the staged candidate.
            // Tiles is storage metadata, so its reference is checked separately from semantic roots and patch digests.
            bool callbackEdited = staged is not null && (_pendingNativeStamp is not { } stamp ||
                stamp.Hash != SemanticHash(candidate) || !ReferenceEquals(stamp.Tiles, candidate.Tiles) ||
                stamp.Hash != SemanticHash(staged.Candidate) || !ReferenceEquals(stamp.Tiles, staged.Candidate.Tiles));
            MapDocument final = callbackEdited ? candidate : staged?.Candidate ?? candidate;
            NativeLegacySpawnAdmission.RequireAdmitted(_doc!, final, staged?.LegacySpawns, liveLists);
            if (SemanticHash(before) == SemanticHash(final) && ReferenceEquals(before.Tiles, final.Tiles)) return result;
            // A callback edit leaves the staged effects and the command's captured after state describing the staged state,
            // so the final candidate is staged again like a generic callback edit, as one complete snapshot from before
            // under the command's label. The published document, its effects, marker bounds and redo state then describe
            // one edit, and staging validates it again. The snapshot carries no adoption permit, so it refuses any
            // legacy list change. The staged finalization travels with it, so its unchanged before side is reused and only
            // the stale after side is captured again.
            if (staged is null || callbackEdited)
            {
                var effects = new MapNativeEditEffects(null, null, [], [], [], [],
                    worldChanged ? MapNativeInvalidation.Terrain | MapNativeInvalidation.Nav | MapNativeInvalidation.Residency | MapNativeInvalidation.Unbounded : MapNativeInvalidation.None);
                string label = staged is not null && _pendingNativeStamp is { } late ? late.Label : "Native document edit";
                var command = new NativeSessionSnapshotCommand(label, before, final, _nativeAssets!,
                    staged?.Assets ?? _nativeAssets!, effects, staged?.Stamp);
                staged = _nativeHistory.StageExecute(before, command, liveLists);
            }
            PublishNative(staged, worldChanged: worldChanged, liveLists: liveLists);
            return result;
        }
        finally
        {
            _pendingNative = null;
            _pendingNativeStamp = null;
            _nativeLiveLists = null;
            _nativeMutationCandidate = null;
            _nativeCallbackActive = false;
            _nativeMutation = false;
        }
    }
    // Staging validated the candidate against its closure, including effective water. A candidate with callback edits
    // was staged again from before, so its expansion is that of the published state.
    MapNativeEditEffects PublishNative(NativeStagedPublication staged, Action? checkpoint = null, bool worldChanged = true,
        string? liveLists = null)
    {
        liveLists ??= NativeLegacySpawnAdmission.LiveLists(_doc!);
        NativeLegacySpawnAdmission.RequireAdmitted(_doc!, staged.Candidate, staged.LegacySpawns, liveLists);
        BeforeNativePublication?.Invoke();
        checkpoint?.Invoke();
        NativeDocumentTransaction.Publish(_doc!, staged, liveLists);
        _nativeAssets = staged.Assets;
        NewSnapshotLocked();
        staged.Accept();
        _dirty = true;
        if (worldChanged) _field = null;
        LastNativeEffects = staged.Effects;
        LastNativeStamp = staged.Stamp;
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
        if (Path.IsPathRooted(relativePath)) throw new MapDocumentException(MapDocumentRefusal.ToolPublicationPathInvalid,
            "Prefab publication paths must be relative to the resource root.")
        { Path = relativePath, StoragePath = _storagePath, DocumentForm = _storageForm };
        string root = MapDocumentStorage.ResourceRoot(_storagePath!, _storageForm);
        string target = Path.GetFullPath(relativePath, root);
        bool inside = ResourcePathContainment.IsInsideRoot(Path.GetRelativePath(root, target));
        if (!inside ||
            MapDocumentStorage.IsReserved(_storagePath!, _storageForm, target))
            throw new MapDocumentException(inside ? MapDocumentRefusal.AssetStoragePathReserved : MapDocumentRefusal.ToolPublicationPathInvalid,
                "Prefab publication path escapes the resource root or enters writer-owned map storage.")
            { Path = relativePath, StoragePath = _storagePath, DocumentForm = _storageForm, ResourceRoot = root };
        return target;
    }
    /// <summary>Undoes one accepted native edit against its captured closure. An empty history returns null.</summary>
    public MapNativeEditEffects? UndoNative()
    {
        lock (_lock)
        {
            RequireDocumentLocked();
            RequireClosedNativeMutation(nameof(UndoNative), nativePublication: true);
            string liveLists = NativeLegacySpawnAdmission.LiveLists(_doc!);
            var staged = _nativeHistory.StageUndo(_doc!, liveLists);
            return staged is null ? null : PublishNative(staged, liveLists: liveLists);
        }
    }
    /// <summary>Redoes one accepted native edit against its captured closure. An empty history returns null.</summary>
    public MapNativeEditEffects? RedoNative()
    {
        lock (_lock)
        {
            RequireDocumentLocked();
            RequireClosedNativeMutation(nameof(RedoNative), nativePublication: true);
            string liveLists = NativeLegacySpawnAdmission.LiveLists(_doc!);
            var staged = _nativeHistory.StageRedo(_doc!, liveLists);
            return staged is null ? null : PublishNative(staged, liveLists: liveLists);
        }
    }
}
