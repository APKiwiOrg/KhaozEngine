using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;

namespace KhaozEngine.MapEditor;

// Prepare mutates only a detached candidate. Its returned action captures command state after validation.
internal interface INativePlacementCommand
{
    Action Prepare(MapDocument candidate, bool undo);
}

internal static class NativePlacementTransaction
{
    internal static void Run(MapDocument document, IEditorCommand command, bool undo,
        MapAssetClosure? assets, MapDocRegistry? registry = null, bool localOnly = false)
    {
        registry ??= MapDocRegistry.CreateDefault();
        if (!localOnly && assets is null) throw new MapDocumentException("Native editing requires an explicit asset closure binding.");
        if (command is not INativePlacementCommand native)
            throw new MapDocumentException("This command does not support atomic native placement editing.");
        // Serialization is also a deep copy of extension-owned analytic payloads using the caller's registry.
        var candidate = NativeDocumentSnapshot.Clone(document, registry);
        Action accept = native.Prepare(candidate, undo);
        if (localOnly) MapBoundDocumentValidation.ValidateLocal(candidate, registry);
        else MapBoundDocumentValidation.Validate(candidate, assets!, registry);
        // Keep the public document instance. Only placement state belongs to this transaction.
        document.Placements = candidate.Placements;
        document.NumericIdHighWaterMark = candidate.NumericIdHighWaterMark;
        accept();
    }

    internal static MapPlacement Copy(MapPlacement p) => new()
    {
        Id = p.Id,
        Kind = p.Kind,
        AssetId = p.AssetId,
        NumericId = p.NumericId,
        DisplayName = p.DisplayName,
        X = p.X,
        Y = p.Y,
        Z = p.Z,
        Yaw = p.Yaw,
        Scale = p.Scale,
        Tags = p.Tags.ToList(),
    };

    internal static MapPlacement Find(MapDocument doc, string id) =>
        doc.Placements.SingleOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"No placement with id '{id}'.");

    internal static void RequireAbsent(MapDocument doc, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || doc.Placements.Any(p => string.Equals(p.Id, id, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Placement id '{id}' is empty or already exists.");
    }
}

// Captured snapshots are private, never published or edited. Failed validation never advances this state.
internal sealed class NativePlacementEdit
{
    MapPlacement? _before;
    string? _afterId;
    int _index;

    internal Action Change(MapDocument doc, string id, bool undo, Action<MapPlacement> edit)
    {
        if (undo)
        {
            if (_before is null) throw new InvalidOperationException("Revert called before Apply.");
            MapPlacement current = NativePlacementTransaction.Find(doc, _afterId!);
            doc.Placements[doc.Placements.IndexOf(current)] = NativePlacementTransaction.Copy(_before);
            return () => { };
        }
        MapPlacement p = NativePlacementTransaction.Find(doc, id);
        MapPlacement before = _before ?? NativePlacementTransaction.Copy(p);
        edit(p);
        string afterId = p.Id;
        return () => { _before = before; _afterId = afterId; };
    }

    internal Action Remove(MapDocument doc, string id, bool undo)
    {
        if (undo)
        {
            if (_before is null) throw new InvalidOperationException("Revert called before Apply.");
            NativePlacementTransaction.RequireAbsent(doc, _before.Id);
            doc.Placements.Insert(_index, NativePlacementTransaction.Copy(_before));
            return () => { };
        }
        MapPlacement p = NativePlacementTransaction.Find(doc, id);
        int index = doc.Placements.IndexOf(p);
        MapPlacement before = NativePlacementTransaction.Copy(p);
        doc.Placements.RemoveAt(index);
        return () => { _before = before; _index = index; };
    }
}
