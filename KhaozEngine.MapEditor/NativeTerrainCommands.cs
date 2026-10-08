using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapEditor;

/// <summary>Publishes a terrain edit through the native seam and restores accepted snapshots on undo and redo.</summary>
public sealed class TerrainEditCommand : EditorCommand, INativeDocumentCommand
{
    readonly MapTerrainEdit _edit;
    MapSurfaceSet? _before;
    MapSurfaceSet? _after;
    MapNativeWriteSet? _writeSet;
    MapNativeEditEffects? _effects;

    public TerrainEditCommand(MapTerrainEdit edit) => _edit = edit ?? throw new ArgumentNullException(nameof(edit));
    public override string Label => _edit.Label;
    internal override bool AffectsWorld => true;
    public override void Apply(MapDocument doc) => NativeDocumentTransaction.Run(doc, this, false, null, localOnly: true);
    public override void Revert(MapDocument doc) => NativeDocumentTransaction.Run(doc, this, true, null, localOnly: true);

    NativeDocumentPreparation INativeDocumentCommand.Prepare(MapDocument candidate, bool undo)
    {
        if (_writeSet is { } writes)
        {
            Restore(candidate.Surfaces, (undo ? _before : _after)!, writes);
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
        MapTerrainEditResult prepared = MapTerrainEdits.Prepare(candidate.Surfaces, _edit);
        MapSurfaceSet before = Capture(candidate.Surfaces, prepared.WriteSet), after = Capture(prepared.Candidate, prepared.WriteSet);
        candidate.Surfaces = prepared.Candidate;
        return new(prepared.WriteSet, prepared.Effects, () =>
        {
            _before = before;
            _after = after;
            _writeSet = prepared.WriteSet;
            _effects = prepared.Effects;
        });
    }

    static MapSurfaceSet Capture(MapSurfaceSet set, MapNativeWriteSet writes)
    {
        var snapshot = new MapSurfaceSet();
        foreach (MapPatchKey key in writes.Patches)
            if (set.Patches.TryGetValue(key, out MapSurfacePatch? patch)) snapshot.Patches.Add(key, patch.Clone());
        foreach (MapSurfaceRef surface in set.Refs.Where(s => writes.SurfaceIds.Contains(s.Id))) snapshot.Refs.Add(surface);
        return snapshot.Clone();
    }

    static void Restore(MapSurfaceSet set, MapSurfaceSet snapshot, MapNativeWriteSet writes)
    {
        MapSurfaceSet copy = snapshot.Clone();
        foreach (MapPatchKey key in writes.Patches)
        {
            if (copy.Patches.TryGetValue(key, out MapSurfacePatch? patch)) set.Patches[key] = patch;
            else set.Patches.Remove(key);
        }
        foreach (string id in writes.SurfaceIds)
        {
            int index = set.Refs.FindIndex(s => s.Id == id);
            MapSurfaceRef? surface = copy.Refs.Find(s => s.Id == id);
            if (surface is null) { if (index >= 0) set.Refs.RemoveAt(index); }
            else if (index >= 0) set.Refs[index] = surface;
            else set.Refs.Add(surface);
        }
    }
}
