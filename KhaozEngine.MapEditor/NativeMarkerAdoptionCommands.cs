using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Markers;

namespace KhaozEngine.MapEditor;

/// <summary>Adopts both legacy spawn lists as one native history entry. Only accepted publication captures the exact
/// before and after states. Undo restores those records and redo restores those markers, without recomputing choices.</summary>
public sealed class AdoptSpawnsCommand : EditorCommand, INativeBoundDocumentCommand
{
    readonly IReadOnlyDictionary<MapLegacySpawnKey, MapMarkerAdoptionChoice> _choices;
    MapDocument? _before, _after;
    static readonly MapNativeWriteSet Writes = new([], [], [], [], [], [], false) { BuildingIntent = true, LegacySpawns = true };
    public AdoptSpawnsCommand(IReadOnlyDictionary<MapLegacySpawnKey, MapMarkerAdoptionChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        _choices = choices.ToDictionary(p => p.Key, p => p.Value);
    }
    public override string Label => "Adopt spawns";
    internal override bool AffectsWorld => false;
    public override void Apply(MapDocument doc) => NativeDocumentTransaction.Run(doc, this, false, null, localOnly: true);
    public override void Revert(MapDocument doc) => NativeDocumentTransaction.Run(doc, this, true, null, localOnly: true);
    NativeDocumentPreparation INativeBoundDocumentCommand.Prepare(MapDocument candidate, bool undo, MapAssetClosure assets) =>
        PrepareAdoption(candidate, undo, assets, out _);

    internal NativeDocumentPreparation PrepareAdoption(MapDocument candidate, bool undo, MapAssetClosure assets, out NativeLegacySpawnPermit? permit)
    {
        var registry = MapDocRegistry.CreateDefault();
        MapDocument before, after;
        if (_before is not null)
        {
            before = _before;
            after = _after!;
        }
        else
        {
            if (undo) throw new InvalidOperationException("Revert called before Apply.");
            before = NativeDocumentSnapshot.Clone(candidate, registry);
            after = MapMarkerAdoption.ConvertSpawns(candidate, _choices);
            MapBoundDocumentValidation.Validate(after, assets);
            after = NativeDocumentSnapshot.Clone(after, registry);
        }
        permit = new(NativeLegacySpawnAdmission.Canonical(undo ? after : before), NativeLegacySpawnAdmission.Canonical(undo ? before : after));
        var desired = NativeDocumentSnapshot.Clone(undo ? before : after, registry);
        NativeDocumentSnapshot.PublishWriteSet(candidate, desired, Writes);
        return new(Writes, new(null, null, [], [], [], [], MapNativeInvalidation.None), () => { _before = before; _after = after; });
    }
}
