using System;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;

namespace KhaozEngine.MapEditor;

/// <summary>Publishes one pure foliage edit, or one foliage sequence with its owner edit, as a single undoable step
/// against the explicitly bound closure. The finalized preparation's batch rows are its effects, its finalization is
/// reused by staging and replay, and a refusal publishes nothing. Foliage never changes the terrain field.</summary>
public sealed class FoliageEditCommand : EditorCommand, INativeBoundDocumentCommand
{
    readonly MapFoliageEdit _edit;
    readonly NativePrefabCommandState _state = new();
    public FoliageEditCommand(MapFoliageEdit edit) => _edit = edit ?? throw new ArgumentNullException(nameof(edit));
    public override string Label => _edit.Label;
    internal override bool AffectsWorld => false;
    public override void Apply(MapDocument doc) => NativeDocumentTransaction.Run(doc, this, false, null, localOnly: true);
    public override void Revert(MapDocument doc) => NativeDocumentTransaction.Run(doc, this, true, null, localOnly: true);
    /// <summary>The accepted forward finalization, null before acceptance.</summary>
    internal MapDocumentFinalizationStamp? AcceptedStamp => _state.AcceptedStamp;
    NativeDocumentPreparation INativeBoundDocumentCommand.Prepare(MapDocument candidate, bool undo, MapAssetClosure assets) =>
        _state.Prepare(candidate, undo, assets, assets, () => MapFoliageEdits.Prepare(candidate, assets, _edit));
}
