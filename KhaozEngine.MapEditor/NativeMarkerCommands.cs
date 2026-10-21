using System;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;

namespace KhaozEngine.MapEditor;

/// <summary>Publishes one standalone marker set or remove against the explicitly bound closure. The GUI inspector and
/// the tool session invoke this same command. It writes the building intent and only the marker frame or transfer
/// it actually changed, and its effects are the shared marker diff. Markers never change the terrain field.</summary>
public sealed class MarkerEditCommand : EditorCommand, INativeBoundDocumentCommand
{
    readonly MapMarkerEdit _edit;
    readonly NativePrefabCommandState _state = new();
    /// <summary>The accepted forward finalization, null before acceptance or without foliage.</summary>
    internal MapDocumentFinalizationStamp? AcceptedStamp => _state.AcceptedStamp;
    public MarkerEditCommand(MapMarkerEdit edit) => _edit = edit ?? throw new ArgumentNullException(nameof(edit));
    public override string Label => _edit.Label;
    internal override bool AffectsWorld => false;
    public override void Apply(MapDocument doc) => NativeDocumentTransaction.Run(doc, this, false, null, localOnly: true);
    public override void Revert(MapDocument doc) => NativeDocumentTransaction.Run(doc, this, true, null, localOnly: true);
    NativeDocumentPreparation INativeBoundDocumentCommand.Prepare(MapDocument candidate, bool undo, MapAssetClosure assets) =>
        _state.Prepare(candidate, undo, assets, assets, () => MapMarkerEdits.Prepare(candidate, assets, _edit));
}
