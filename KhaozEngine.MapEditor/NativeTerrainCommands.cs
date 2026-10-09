using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapEditor;

/// <summary>Publishes a terrain edit through the native seam and restores accepted snapshots on undo and redo.</summary>
public sealed class TerrainEditCommand : EditorCommand, INativeDocumentCommand
{
    readonly MapTerrainEdit _edit;
    Snapshot? _before;
    Snapshot? _after;
    MapNativeWriteSet? _writeSet;
    MapNativeWriteSet? _undoWriteSet;
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
            return new(undo ? _undoWriteSet! : writes, effects, () => { });
        }
        if (undo) throw new InvalidOperationException("Revert called before Apply.");
        MapTerrainEditResult prepared = MapTerrainEdits.Prepare(candidate.Surfaces, _edit);
        Snapshot before = Capture(candidate.Surfaces, prepared.WriteSet), after = Capture(prepared.Candidate, prepared.WriteSet);
        MapNativeWriteSet undoWrites = prepared.WriteSet with
        {
            OwnerChanges = Array.AsReadOnly(prepared.WriteSet.OwnerChanges.Select(c => Invert(c, before.Set, after.Set)).ToArray()),
        };
        candidate.Surfaces = prepared.Candidate;
        return new(prepared.WriteSet, prepared.Effects, () =>
        {
            _before = before;
            _after = after;
            _writeSet = prepared.WriteSet;
            _undoWriteSet = undoWrites;
            _effects = prepared.Effects;
        });
    }

    // The write set's patches and refs, plus the whole set's ref order so a restored ref regains its position.
    sealed record Snapshot(MapSurfaceSet Set, IReadOnlyList<string> RefOrder);

    static Snapshot Capture(MapSurfaceSet set, MapNativeWriteSet writes)
    {
        var snapshot = new MapSurfaceSet();
        foreach (MapPatchKey key in writes.Patches)
            if (set.Patches.TryGetValue(key, out MapSurfacePatch? patch)) snapshot.Patches.Add(key, patch.Clone());
        foreach (MapSurfaceRef surface in set.Refs.Where(s => writes.SurfaceIds.Contains(s.Id))) snapshot.Refs.Add(surface);
        return new(snapshot.Clone(), Array.AsReadOnly(set.Refs.Select(s => s.Id).ToArray()));
    }

    static void Restore(MapSurfaceSet set, Snapshot snapshot, MapNativeWriteSet writes)
    {
        MapSurfaceSet copy = snapshot.Set.Clone();
        foreach (MapPatchKey key in writes.Patches)
        {
            if (copy.Patches.TryGetValue(key, out MapSurfacePatch? patch)) set.Patches[key] = patch;
            else set.Patches.Remove(key);
        }
        foreach (string id in writes.SurfaceIds)
        {
            MapSurfaceRef? surface = copy.Refs.Find(s => s.Id == id);
            if (surface is not null) NativeDocumentSnapshot.PlaceRef(set.Refs, surface, snapshot.RefOrder);
            else if (set.Refs.FindIndex(s => s.Id == id) is var index and >= 0) set.Refs.RemoveAt(index);
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
