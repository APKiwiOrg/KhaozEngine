using System;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;

namespace KhaozEngine.MapEditor;

/// <summary>Publishes a standalone interior or roof edit against the explicitly bound closure.</summary>
public sealed class InteriorEditCommand : EditorCommand, INativeBoundDocumentCommand
{
    readonly MapInteriorEdit _edit;
    readonly NativePrefabCommandState _state = new();
    public InteriorEditCommand(MapInteriorEdit edit) => _edit = edit ?? throw new ArgumentNullException(nameof(edit));
    public override string Label => _edit.Label;
    internal override bool AffectsWorld => true;
    public override void Apply(MapDocument doc) => NativeDocumentTransaction.Run(doc, this, false, null, localOnly: true);
    public override void Revert(MapDocument doc) => NativeDocumentTransaction.Run(doc, this, true, null, localOnly: true);
    NativeDocumentPreparation INativeBoundDocumentCommand.Prepare(MapDocument candidate, bool undo, MapAssetClosure assets) =>
        _state.Prepare(candidate, undo, () => MapInteriorEdits.Prepare(candidate, assets, _edit));
}
