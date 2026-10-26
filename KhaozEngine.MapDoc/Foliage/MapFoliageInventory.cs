using System;
using System.Collections.Generic;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Separate retained full samples, compact columns and simultaneous construction storage.</summary>
internal static class MapFoliageInventory
{
    internal const long InstanceControl = 56, InstanceBatchControl = 112, WorkControl = 80;
    internal const long BuilderControl = 104, ColumnBudgetControl = 56, OrdinalControl = 48;
    internal const long FrozenControls = InstanceControl + InstanceBatchControl + WorkControl;
    internal const long ConstructionControls = BuilderControl + ColumnBudgetControl + 2 * OrdinalControl;
    internal static long FullSamples(IReadOnlyList<MapFoliageSampleBatch> batches)
    {
        ArgumentNullException.ThrowIfNull(batches);
        var graph = new FullGraph();
        foreach (var batch in batches) graph.Batch(batch);
        return graph.Bytes;
    }

    // Seven independent arrays. Scalar columns round individually, not after summing their payloads.
    internal static long NativeColumns(int rows, int supports, int models) => checked(
        Array(rows, 64) + Array(rows, 12) + 5 * Array(rows, 4) + ReferenceArray(supports) + ReferenceArray(models));

    internal static long ReferenceArray(int count) => Array(count, 8);

    internal static long Array(int count, int elementBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfNegative(elementBytes);
        return Align(checked(24L + (long)count * elementBytes));
    }

    internal static long Construction(int capacity, int rows, int supports, int models)
    {
        if (capacity < rows) throw new ArgumentOutOfRangeException(nameof(capacity));
        return checked(Array(capacity, 96) + NativeColumns(rows, supports, models));
    }

    internal static long Text(string text) => Align(checked(22L + 2L * text.Length));
    static long Align(long bytes) => checked(bytes + 7) & ~7L;

    internal static long InstanceMetadata(string layer, MapFoliagePolicy? policy, string digest, ReadOnlySpan<string> models,
        ReadOnlySpan<string?> supports, ReadOnlySpan<MapFoliageSampleKey> keys, int capacity)
    {
        var seen = new HashSet<object>(capacity, ReferenceEqualityComparer.Instance);
        long bytes = 0;
        void String(string? value) { if (value is not null && seen.Add(value)) bytes = checked(bytes + Text(value)); }
        void Policy(MapFoliagePolicy? value)
        { if (value is not null && seen.Add(value)) { bytes = checked(bytes + 32); String(value.Id); } }
        String(layer); Policy(policy); String(digest);
        foreach (var id in models) String(id);
        foreach (var id in supports) String(id);
        foreach (var key in keys) { String(key.Address.LayerId); String(key.Address.SupportOwnerId); Policy(key.Policy); }
        return bytes;
    }

    sealed class FullGraph
    {
        readonly HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);
        internal long Bytes { get; private set; }
        bool Add(object? value, long bytes)
        {
            if (value is null || !_seen.Add(value)) return false;
            Bytes = checked(Bytes + bytes);
            return true;
        }
        void String(string? value) { if (value is not null) Add(value, Text(value)); }
        internal void Batch(MapFoliageSampleBatch batch)
        {
            if (!Add(batch, 64)) return;
            Add(batch.Work, 80);
            Add(batch.Samples, checked(24 + ReferenceArray(batch.Samples.Count)));
            String(batch.LayerId); String(batch.DependencyDigest);
            foreach (var sample in batch.Samples) Sample(sample);
        }
        void Sample(MapFoliageSample sample)
        {
            if (!Add(sample, 248)) return;
            String(sample.Key.Address.LayerId); String(sample.Key.Address.SupportOwnerId);
            String(sample.OccupiedSpaceId); String(sample.AssetId);
            if (Add(sample.Key.Policy, 32)) String(sample.Key.Policy.Id);
            if (sample.Face is { } face)
            {
                String(face.OwnerId);
                if (face.Patch is { } patch) String(patch.SurfaceId);
            }
            var materials = sample.MaterialResourceIds;
            if (Add(materials, checked((materials is string[]? 0 : 24) + ReferenceArray(materials.Count))))
                foreach (string material in materials) String(material);
        }
    }
}
