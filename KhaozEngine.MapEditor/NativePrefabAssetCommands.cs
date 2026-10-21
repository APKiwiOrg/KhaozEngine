using System;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;

namespace KhaozEngine.MapEditor;

/// <summary>An immutable prefab version switch with both verified closures captured before preparation.</summary>
public sealed class PrefabAssetEditCommand : EditorCommand, INativeBoundDocumentCommand, INativeAssetClosureCommand
{
    readonly MapUpdatePrefabAsset _edit;
    readonly MapAssetClosure _before, _after;
    readonly NativePrefabCommandState _state = new();
    /// <summary>The accepted forward finalization, null before acceptance or without foliage.</summary>
    internal MapDocumentFinalizationStamp? AcceptedStamp => _state.AcceptedStamp;
    public PrefabAssetEditCommand(MapPrefabEdit edit, MapAssetClosure before, MapAssetClosure after)
    {
        _edit = edit as MapUpdatePrefabAsset ?? throw new ArgumentException("Asset commands require an asset update.", nameof(edit));
        _before = before ?? throw new ArgumentNullException(nameof(before));
        _after = after ?? throw new ArgumentNullException(nameof(after));
    }
    public override string Label => _edit.Label;
    internal override bool AffectsWorld => true;
    public override void Apply(MapDocument doc) => NativeDocumentTransaction.Run(doc, this, false, _after);
    public override void Revert(MapDocument doc) => NativeDocumentTransaction.Run(doc, this, true, _before);
    public MapAssetClosure Closure(bool undo) => undo ? _before : _after;
    NativeDocumentPreparation INativeBoundDocumentCommand.Prepare(MapDocument candidate, bool undo, MapAssetClosure assets) =>
        _state.Prepare(candidate, undo, Closure(!undo), Closure(undo), () => MapPrefabEdits.PrepareAssetUpdate(candidate, _before, _after, _edit));
}
