using System;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;

namespace KhaozEngine.MapEditor;

/// <summary>A closure-bound pure prefab edit, captured only after validated publication.</summary>
public sealed class PrefabEditCommand : EditorCommand, INativeBoundDocumentCommand
{
    readonly MapPrefabEdit _edit;
    readonly NativePrefabCommandState _state = new();
    public PrefabEditCommand(MapPrefabEdit edit) => _edit = edit ?? throw new ArgumentNullException(nameof(edit));
    public override string Label => _edit.Label;
    internal override bool AffectsWorld => true;
    public override void Apply(MapDocument doc) => NativeDocumentTransaction.Run(doc, this, false, null, localOnly: true);
    public override void Revert(MapDocument doc) => NativeDocumentTransaction.Run(doc, this, true, null, localOnly: true);
    NativeDocumentPreparation INativeBoundDocumentCommand.Prepare(MapDocument candidate, bool undo, MapAssetClosure assets) =>
        _state.Prepare(candidate, undo, () => MapPrefabEdits.Prepare(candidate, assets, _edit));
}

// Focused commands retain a whole private capture, but publish only their named roots and patches.
internal sealed class NativePrefabCommandState
{
    MapDocument? _before, _after;
    MapNativeWriteSet? _writes;
    MapNativeEditEffects? _effects;
    internal NativeDocumentPreparation Prepare(MapDocument candidate, bool undo, Func<MapPrefabEditResult> prepare)
    {
        var registry = MapDocRegistry.CreateDefault();
        if (_writes is { } writes)
        {
            var desired = NativeDocumentSnapshot.Clone((undo ? _before : _after)!, registry);
            NativeCompleteSnapshotCommand.RetainReservations(desired, candidate);
            NativeDocumentSnapshot.PublishWriteSet(candidate, desired, writes);
            return new(writes, undo ? NativeCompleteSnapshotCommand.Reverse(_effects!) : _effects!, () => { });
        }
        if (undo) throw new InvalidOperationException("Revert called before Apply.");
        var before = NativeDocumentSnapshot.Clone(candidate, registry);
        var result = prepare();
        var after = NativeDocumentSnapshot.Clone(result.After, registry);
        NativeDocumentSnapshot.PublishWriteSet(candidate, result.After, result.WriteSet);
        return new(result.WriteSet, result.Effects, () =>
        { _before = before; _after = after; _writes = result.WriteSet; _effects = result.Effects; });
    }
}
