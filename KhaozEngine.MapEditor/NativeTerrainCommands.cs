using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapEditor;

/// <summary>Publishes a terrain edit through the native seam and restores accepted snapshots on undo and redo.</summary>
public sealed class TerrainEditCommand : EditorCommand, INativeDocumentCommand
{
    readonly MapTerrainEdit _edit;
    MapDocument? _before;
    MapDocument? _after;
    MapNativeWriteSet? _writeSet;
    MapNativeWriteSet? _undoWriteSet;
    MapNativeEditEffects? _effects;

    public TerrainEditCommand(MapTerrainEdit edit) => _edit = edit ?? throw new ArgumentNullException(nameof(edit));
    public override string Label => _edit.Label;
    internal override bool AffectsWorld => true;
    public override void Apply(MapDocument doc) => NativeDocumentTransaction.Run(doc, this, false, null, localOnly: true);
    public override void Revert(MapDocument doc) => NativeDocumentTransaction.Run(doc, this, true, null, localOnly: true);

    NativeDocumentPreparation INativeDocumentCommand.Prepare(MapDocument candidate, bool undo) => Prepare(candidate, undo, null);
    internal NativeDocumentPreparation Prepare(MapDocument candidate, bool undo, MapAssetClosure? assets, MapDocRegistry? registry = null) =>
        Prepare(candidate, undo, assets, registry ?? MapDocRegistry.CreateDefault(), includePlacementEffects: true);

    // Staging reads placement effects from the same expansions it uses for validation and the other effect channels.
    internal NativeDocumentPreparation PrepareForStage(MapDocument candidate, bool undo, MapAssetClosure? assets, MapDocRegistry registry) =>
        Prepare(candidate, undo, assets, registry, includePlacementEffects: false);

    NativeDocumentPreparation Prepare(MapDocument candidate, bool undo, MapAssetClosure? assets, MapDocRegistry registry,
        bool includePlacementEffects)
    {
        if (_writeSet is { } writes)
        {
            NativeDocumentSnapshot.PublishWriteSet(candidate, NativeDocumentSnapshot.Clone((undo ? _before : _after)!, registry), writes);
            MapNativeWriteSet selectedWrites = undo ? _undoWriteSet! : writes;
            MapNativeEditEffects effects = undo ? NativeCompleteSnapshotCommand.Reverse(_effects!) : _effects!;
            return new(selectedWrites, Describe((undo ? _after : _before)!, selectedWrites, effects), () => { });
        }
        if (undo) throw new InvalidOperationException("Revert called before Apply.");
        var before = NativeDocumentSnapshot.Clone(candidate, registry);
        // The document-aware terrain transaction is the one home of this edit. The local-only path passes no closure.
        var (writeset, surfaceEffects, prepared) = MapDocumentTerrainEdits.Apply(candidate, assets, _edit);
        var after = NativeDocumentSnapshot.Clone(candidate, registry);
        var undoWrites = writeset with
        {
            OwnerChanges = prepared.WriteSet.OwnerChanges.Where(c => before.Surfaces.Patches.ContainsKey(c.Dependent))
                .Select(c => Invert(c, before.Surfaces, after.Surfaces)).ToArray(),
        };
        var named = Describe(before, writeset, surfaceEffects);
        return new(writeset, named, () =>
        {
            _before = before; _after = after; _writeSet = writeset;
            _undoWriteSet = undoWrites; _effects = surfaceEffects;
        });

        MapNativeEditEffects Describe(MapDocument original, MapNativeWriteSet selectedWrites, MapNativeEditEffects surface)
        {
            var effects = MapDocumentTerrainEdits.Name(candidate, surface);
            return !includePlacementEffects || assets is null ? effects : MapTerrainPlacementChanges.Include(original, selectedWrites, effects,
                () => MapPrefabResolver.Expand(original, assets), () => MapPrefabResolver.Expand(candidate, assets));
        }
    }

    // Undo reports new to old. A self-owned corner is a null old owner and the dependent's own address as new owner.
    static MapCornerOwnerChange Invert(MapCornerOwnerChange change, MapSurfaceSet before, MapSurfaceSet after)
    {
        MapSurfacePatch patch = before.Patches.TryGetValue(change.Dependent, out MapSurfacePatch? restored) &&
            change.CornerX <= restored.Width && change.CornerZ <= restored.Depth ? restored : after.Patches[change.Dependent];
        var self = new MapVertexOwner(change.Dependent, patch.CornerAddress(change.CornerX, change.CornerZ));
        MapVertexOwner? old = change.NewOwner.Patch == change.Dependent ? null : change.NewOwner;
        return new(change.Dependent, change.CornerX, change.CornerZ, old, change.OldOwner ?? self);
    }
}
