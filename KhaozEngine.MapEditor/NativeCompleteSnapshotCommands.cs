using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;

namespace KhaozEngine.MapEditor;

internal interface INativeAssetClosureCommand
{
    MapAssetClosure Closure(bool undo);
}

/// <summary>One captured complete document/closure switch. History never recomputes a generic callback.</summary>
internal sealed class NativeCompleteSnapshotCommand : EditorCommand, INativeBoundDocumentCommand, INativeAssetClosureCommand
{
    readonly string _label;
    readonly MapDocument _before, _after;
    readonly MapAssetClosure _beforeAssets, _afterAssets;
    readonly MapNativeEditEffects _effects;
    MapDocumentFinalizationStamp? _stamp;

    /// <summary>Captures both snapshots. <paramref name="stamp"/> is an earlier finalization of this before snapshot,
    /// such as the staged command a late callback replaced. Staging reuses each of its sides only while that side's
    /// fingerprint and closure still match, and acceptance keeps the final one for replay. Both snapshots pass the foliage
    /// preflight before either is cloned.</summary>
    internal NativeCompleteSnapshotCommand(string label, MapDocument before, MapDocument after,
        MapAssetClosure beforeAssets, MapAssetClosure afterAssets, MapNativeEditEffects effects, MapDocumentFinalizationStamp? stamp = null)
    {
        MapDocumentEditFinalizer.Preflight(before, beforeAssets);
        MapDocumentEditFinalizer.Preflight(after, afterAssets);
        _stamp = stamp;
        _label = label;
        _before = NativeDocumentSnapshot.Clone(before, MapDocRegistry.CreateDefault());
        _after = NativeDocumentSnapshot.Clone(after, MapDocRegistry.CreateDefault());
        _beforeAssets = beforeAssets;
        _afterAssets = afterAssets;
        _effects = effects;
    }
    public override string Label => _label;
    internal override bool AffectsWorld => true;
    public override void Apply(MapDocument doc) => NativeDocumentTransaction.Run(doc, this, false, _afterAssets);
    public override void Revert(MapDocument doc) => NativeDocumentTransaction.Run(doc, this, true, _beforeAssets);
    public MapAssetClosure Closure(bool undo) => undo ? _beforeAssets : _afterAssets;
    /// <summary>The accepted forward finalization, null before acceptance or without foliage.</summary>
    internal MapDocumentFinalizationStamp? AcceptedStamp => _stamp;
    public NativeDocumentPreparation Prepare(MapDocument candidate, bool undo, MapAssetClosure assets)
    {
        MapDocument desired = NativeDocumentSnapshot.Clone(undo ? _before : _after, MapDocRegistry.CreateDefault());
        RetainReservations(desired, candidate);
        NativeDocumentSnapshot.Publish(candidate, desired);
        return new(EmptyWrites, undo ? Reverse(_effects) : _effects, () => { })
        {
            Mode = MapNativePublicationMode.CompleteSnapshot,
            Stamp = undo ? _stamp?.Reverse() : _stamp,
            AcceptStamp = staged => _stamp = undo ? staged?.Reverse() : staged,
        };
    }
    internal static MapNativeWriteSet EmptyWrites { get; } = new([], [], [], [], [], [], false);
    internal static MapNativeEditEffects Reverse(MapNativeEditEffects effects) => effects with
    {
        OldBounds = effects.NewBounds,
        NewBounds = effects.OldBounds,
        OldSpatialBounds = effects.NewSpatialBounds,
        NewSpatialBounds = effects.OldSpatialBounds,
        DigestChanges = effects.DigestChanges.Select(d => new MapDigestChange(d.Key, d.After, d.Before)).ToArray(),
        FoliageChunks = MapDocumentFinalizationStamp.ReverseRows(effects.FoliageChunks),
        FoliageLayers = MapDocumentFinalizationStamp.ReverseLayers(effects.FoliageLayers),
    };

    // Retain identity, never live payload. A restored prefab reclaims its own transferred inventory.
    internal static void RetainReservations(MapDocument desired, MapDocument current)
    {
        desired.NumericIdHighWaterMark = Math.Max(desired.NumericIdHighWaterMark, current.NumericIdHighWaterMark);
        if (current.Prefabs is not { } old) return;
        var set = desired.Prefabs ?? new(1, 1, [], []);
        var instances = set.Instances.ToList();
        foreach (var instance in old.Instances)
        {
            int index = instances.FindIndex(i => i.Id == instance.Id);
            if (index < 0)
                instances.Add(instance with
                {
                    Removed = true,
                    Bindings = new(instance.Bindings.Entries.Select(b => b with { Removed = !instance.Unpacked || b.Removed }).ToArray())
                });
            else
            {
                var restored = instances[index];
                var retained = instance.Bindings.Entries.Where(b => !restored.Bindings.Entries.Any(r => r.Child == b.Child));
                instances[index] = restored with { Bindings = new(restored.Bindings.Entries.Concat(retained.Select(b => b with { Removed = !restored.Unpacked || b.Removed })).ToArray()) };
            }
        }
        var transfers = set.Transfers.ToList();
        foreach (var transfer in old.Transfers)
        {
            if (transfers.Any(t => t.InstanceId == transfer.InstanceId && t.Binding.Child == transfer.Binding.Child)) continue;
            var owner = instances.SingleOrDefault(i => i.Id == transfer.InstanceId);
            if (owner is { Removed: false } || owner is { Unpacked: false }) continue;
            transfers.Add(transfer with { Retired = true });
        }
        var attachments = set.Attachments.ToList();
        foreach (var row in old.Attachments)
        {
            var oldOwner = old.Transfers.SingleOrDefault(t => t.Binding.Child.Kind == MapPrefabChildKind.Placement && t.Binding.WorldId == row.PlacementId);
            string ownerId = oldOwner is null ? row.PlacementId : transfers.SingleOrDefault(t => t.InstanceId == oldOwner.InstanceId && t.Binding.Child == oldOwner.Binding.Child)?.Binding.WorldId ?? row.PlacementId;
            if (!attachments.Any(a => a.PlacementId == ownerId && a.Kind == row.Kind && a.ResourceId == row.ResourceId) &&
                row.Children.All(c => transfers.Any(t => t.Binding.WorldId == c.WorldId && t.Binding.Child.Kind == c.LocalChild.Kind && t.Retired)))
                attachments.Add(row with { PlacementId = ownerId, Retired = true });
        }
        desired.Prefabs = new(set.PayloadVersion, set.ExpansionVersion, instances.OrderBy(i => i.Id, StringComparer.Ordinal).ToArray(), transfers)
        { Attachments = attachments };
    }
}
