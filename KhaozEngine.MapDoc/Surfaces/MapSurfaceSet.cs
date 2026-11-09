using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>Declared logical surfaces and their currently resident authored patches.</summary>
public sealed class MapSurfaceSet
{
    public List<MapSurfaceRef> Refs { get; } = new();
    public SortedDictionary<MapPatchKey, MapSurfacePatch> Patches { get; } = new();
    public bool IsEmpty => Refs.Count == 0 && Patches.Count == 0;

    public MapSurfaceSet Clone()
    {
        using var preparation = Scene.MapSceneAdmission.Current?.BeginPreparation();
        Scene.MapSceneAdmission.Current?.ReserveBytes(checked(128L + Refs.Sum(s => 128L + 8L * (s.IndoorSpan?.DomainTags.Count ?? 0)) +
            8L * Scene.MapSceneAdmission.CloneCapacity(Refs.Count, true) + 64L * Patches.Count));
        var copy = new MapSurfaceSet();
        foreach (MapSurfaceRef surface in Refs)
            copy.Refs.Add(surface with
            {
                IndoorSpan = surface.IndoorSpan is { } span
                    ? span with { DomainTags = span.DomainTags.ToArray() } : null,
            });
        foreach (var patch in Patches) copy.Patches.Add(patch.Key, patch.Value.Clone());
        preparation?.Complete(new Scene.MapSceneInventory().Surfaces(copy));
        return copy;
    }

    public IEnumerable<MapTopologyRecord> AllRecords()
    {
        foreach (MapSurfacePatch patch in Patches.Values)
            foreach (MapTopologyRecord record in patch.Records.OrderBy(r => r.Id, StringComparer.Ordinal))
                yield return record;
    }

    /// <summary>Looks only in the referenced anchor's resident patch, without a global ID fallback.</summary>
    public bool TryGetRecord(MapRecordRef reference, out MapTopologyRecord? record)
    {
        ArgumentNullException.ThrowIfNull(reference);
        record = null;
        if (!Patches.TryGetValue(reference.Anchor, out MapSurfacePatch? patch)) return false;
        record = patch.Records.FirstOrDefault(r => string.Equals(r.Id, reference.Id, StringComparison.Ordinal));
        return record is not null;
    }

}
