using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapEditor;

/// <summary>Publishes a water edit atomically, restoring the accepted sets on undo and redo.</summary>
public sealed class WaterEditCommand : EditorCommand, INativeDocumentCommand
{
    readonly MapWaterEdit _edit;
    MapWaterSet? _before;
    MapWaterSet? _after;
    MapNativeWriteSet? _writeSet;
    MapNativeEditEffects? _effects;

    public WaterEditCommand(MapWaterEdit edit) => _edit = edit ?? throw new ArgumentNullException(nameof(edit));
    public override string Label => _edit.Label;
    internal override bool AffectsWorld => true;
    public override void Apply(MapDocument doc) => NativeDocumentTransaction.Run(doc, this, false, null, localOnly: true);
    public override void Revert(MapDocument doc) => NativeDocumentTransaction.Run(doc, this, true, null, localOnly: true);

    NativeDocumentPreparation INativeDocumentCommand.Prepare(MapDocument candidate, bool undo)
    {
        if (_writeSet is { } writes)
        {
            candidate.Water = (undo ? _before : _after)?.Clone();
            MapNativeEditEffects accepted = _effects!;
            MapNativeEditEffects effects = undo ? accepted with
            {
                OldBounds = accepted.NewBounds,
                NewBounds = accepted.OldBounds,
                DigestChanges = Array.AsReadOnly(accepted.DigestChanges.Select(c => new MapDigestChange(c.Key, c.After, c.Before)).ToArray()),
            } : accepted;
            return new(writes, effects, () => { });
        }
        if (undo) throw new InvalidOperationException("Revert called before Apply.");
        MapWaterEditResult prepared = MapWaterEdits.Prepare(candidate, _edit);
        MapWaterSet? before = candidate.Water?.Clone(), after = prepared.After?.Clone();
        candidate.Water = prepared.After;
        return new(prepared.WriteSet, prepared.Effects, () =>
        {
            _before = before;
            _after = after;
            _writeSet = prepared.WriteSet;
            _effects = prepared.Effects;
        });
    }
}
