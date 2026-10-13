using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;

namespace KhaozEngine.MapEditor;

// Prepare mutates only a detached candidate. Its returned action captures command state after validation.
internal interface INativePlacementCommand
{
    Action Prepare(MapDocument candidate, bool undo, MapAssetClosure? assets = null);
}

internal static class NativePlacementTransaction
{
    internal static void Run(MapDocument document, IEditorCommand command, bool undo,
        MapAssetClosure? assets, MapDocRegistry? registry = null, bool localOnly = false) =>
        NativeDocumentTransaction.Run(document, command, undo, assets, registry, localOnly);

    internal static MapPlacement Copy(MapPlacement p) => new()
    {
        Id = p.Id,
        Kind = p.Kind,
        AssetId = p.AssetId,
        NumericId = p.NumericId,
        DisplayName = p.DisplayName,
        SupportBinding = p.SupportBinding,
        X = p.X,
        Y = p.Y,
        Z = p.Z,
        Yaw = p.Yaw,
        Scale = p.Scale,
        Tags = p.Tags.ToList(),
    };

    internal static MapPlacement Find(MapDocument doc, string id) =>
        doc.Placements.SingleOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal))
        ?? throw new MapDocumentException($"No placement with id '{id}'.");

    internal static void RequireAbsent(MapDocument doc, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || doc.Placements.Any(p => string.Equals(p.Id, id, StringComparison.Ordinal)))
            throw new MapDocumentException($"Placement id '{id}' is empty or already exists.");
    }
}

internal interface INativePlacementWriteSetCommand
{
    MapNativeWriteSet? AcceptedWrites { get; }
}

internal interface INativePlacementHistoryMerge
{
    Action? PrepareMerge(IEditorCommand newer, NativeStagedPublication publication);
}

// Original before state survives coalescing. Redo restores the validated final resource snapshot.
internal sealed class NativePlacementEdit
{
    NativePlacementResourceState? _before, _after;
    MapNativeWriteSet? _writes;
    string? _beforeId, _afterId;
    internal bool Accepted => _before is not null;
    internal MapNativeWriteSet? Writes => _writes;

    internal Action Change(MapDocument doc, string id, bool undo, Action<MapPlacement> edit,
        MapPlacementPoseFields fields = MapPlacementPoseFields.None, MapAssetClosure? assets = null)
    {
        if (_before is not null)
        {
            NativePlacementTransaction.Find(doc, undo ? _afterId! : _beforeId!);
            if (_beforeId != _afterId)
            {
                string oldId = undo ? _afterId! : _beforeId!, newId = undo ? _beforeId! : _afterId!;
                NativePlacementTransaction.Find(doc, oldId).Id = newId;
                NativePlacementReferences.Remap(doc, oldId, newId);
            }
            NativePlacementResourceState.Restore(doc, (undo ? _before : _after)!);
            return () => { };
        }
        if (undo) throw new InvalidOperationException("Revert called before Apply.");
        var before = NativePlacementResourceState.Capture(doc, id);
        var p = NativePlacementTransaction.Find(doc, id);
        edit(p);
        if (p.Id != id) NativePlacementReferences.Remap(doc, id, p.Id);
        var writes = NativePlacementResourceState.ApplyPose(doc, p.Id, fields);
        if (assets is not null && p.Y is null) writes = NativePlacementResourceState.Union(writes, NativePlacementResourceState.Reseat(doc, assets));
        var after = NativePlacementResourceState.Capture(doc, p.Id);
        writes = NativePlacementResourceState.Union(writes, NativePlacementResourceState.Union(before.WriteSet, after.WriteSet));
        string afterId = p.Id;
        return () => { _before = before; _after = after; _beforeId = id; _afterId = afterId; _writes = writes; };
    }
    internal Action Remove(MapDocument doc, string id, bool undo)
    {
        if (_before is not null)
        {
            if (undo) NativePlacementTransaction.RequireAbsent(doc, _beforeId!);
            else NativePlacementTransaction.Find(doc, _afterId!);
            NativePlacementResourceState.Restore(doc, (undo ? _before : _after)!);
            return () => { };
        }
        if (undo) throw new InvalidOperationException("Revert called before Apply.");
        var before = NativePlacementResourceState.Capture(doc, id);
        NativePlacementTransaction.Find(doc, id);
        var writes = NativePlacementResourceState.Remove(doc, id);
        var after = NativePlacementResourceState.Capture(doc, id);
        return () => { _before = before; _after = after; _beforeId = id; _afterId = id; _writes = writes; };
    }
    internal Action? PrepareMerge(NativePlacementEdit newer, NativeStagedPublication publication, string id, Action targets)
    {
        if (_before is null || newer.Accepted) return null;
        var after = NativePlacementResourceState.Capture(publication.Candidate, id);
        var writes = NativePlacementResourceState.Union(_writes!, publication.WriteSet);
        return () => { _after = after; _afterId = id; _writes = writes; targets(); };
    }
}
