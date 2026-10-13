using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;

namespace KhaozEngine.MapEditor;

/// <summary>Delegates captured callback state to the native complete-snapshot publication contract.</summary>
internal sealed class NativeSessionSnapshotCommand : EditorCommand, INativeBoundDocumentCommand, INativeAssetClosureCommand
{
    readonly NativeCompleteSnapshotCommand _snapshot;
    internal NativeSessionSnapshotCommand(string label, MapDocument before, MapDocument after, MapAssetClosure beforeAssets,
        MapAssetClosure afterAssets, MapNativeEditEffects effects) => _snapshot = new(label, before, after, beforeAssets, afterAssets, effects);
    public override string Label => _snapshot.Label;
    internal override bool AffectsWorld => true;
    public override void Apply(MapDocument doc) => _snapshot.Apply(doc);
    public override void Revert(MapDocument doc) => _snapshot.Revert(doc);
    public MapAssetClosure Closure(bool undo) => _snapshot.Closure(undo);
    public NativeDocumentPreparation Prepare(MapDocument candidate, bool undo, MapAssetClosure assets) => _snapshot.Prepare(candidate, undo, assets);
}
