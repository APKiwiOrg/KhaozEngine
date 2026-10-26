using System;
using System.Numerics;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Detaches imported full samples into minimal columns, retaining complete keys when their headers differ.</summary>
internal static class MapFoliageInstanceImport
{
    internal static MapFoliageInstanceBatch Create(MapFoliageSampleBatch source, Action<long> allocate, Action<long> release)
    {
        int count = source.Samples.Count;
        if (source.Status != MapFoliageStatus.Complete || count == 0) throw new ArgumentException("Import needs nonempty complete samples.", nameof(source));
        var first = source.Samples[0].Key;
        bool uniform = first.Policy is not null;
        foreach (var sample in source.Samples)
            uniform &= ReferenceEquals(sample.Key.Address.LayerId, source.LayerId) && sample.Key.DistributionVersion == first.DistributionVersion &&
                ReferenceEquals(sample.Key.Policy, first.Policy);
        var budget = new MapFoliageColumnBudget(long.MaxValue, allocate, release);
        long controls = MapFoliageInventory.ColumnBudgetControl + 2 * MapFoliageInventory.OrdinalControl;
        budget.Take(controls);
        var models = new MapFoliageOrdinalTable(budget); var supports = new MapFoliageOrdinalTable(budget);
        bool complete = false;
        try
        {
            var matrices = Allocate<Matrix4x4>(count, 64, budget); var positions = Allocate<Vector3>(count, 12, budget);
            var ranks = Allocate<float>(count, 4, budget); var modelIndices = Allocate<int>(count, 4, budget);
            var xs = uniform ? Allocate<int>(count, 4, budget) : null;
            var zs = uniform ? Allocate<int>(count, 4, budget) : null;
            var supportIndices = uniform ? Allocate<int>(count, 4, budget) : null;
            var keys = uniform ? null : Allocate<MapFoliageSampleKey>(count, 40, budget);
            for (int i = 0; i < count; i++)
            {
                var sample = source.Samples[i];
                matrices[i] = sample.Transform; positions[i] = sample.Position; ranks[i] = sample.ThinningRank;
                modelIndices[i] = models.Intern(sample.AssetId);
                if (uniform)
                { xs![i] = sample.Key.Address.X; zs![i] = sample.Key.Address.Z; supportIndices![i] = supports.Intern(sample.Key.Address.SupportOwnerId); }
                else keys![i] = sample.Key;
            }
            var modelIds = models.FreezeModels(); var supportIds = uniform ? supports.Freeze() : null;
            int references = checked(4 + modelIds.Length + (supportIds?.Length ?? 0) + 4 * (keys?.Length ?? 0));
            // HashSet entries are 16 bytes, buckets 4. Its prime capacity is below twice this explicit request.
            long metadataWorkspace = checked(128L + 40L * Math.Max(3, references));
            budget.Take(metadataWorkspace);
            long metadata = MapFoliageInventory.InstanceMetadata(source.LayerId, first.Policy, source.DependencyDigest, modelIds,
                supportIds, keys, Math.Max(3, references));
            budget.Release(metadataWorkspace);
            budget.Take(checked(MapFoliageInventory.FrozenControls + metadata));
            var columns = GroundCoverInstanceColumns.TakeOwnership(matrices, positions, ranks, modelIndices, modelIds);
            models.Clear(); supports.Clear(); budget.Release(controls);
            var result = new MapFoliageInstanceBatch(source.LayerId, source.Chunk, source.DependencyDigest, first.Policy, columns,
                xs, zs, supportIndices, supportIds, source.Work with { RetainedBytes = budget.Live }, first.DistributionVersion, keys);
            complete = true; return result;
        }
        finally
        {
            models.Clear(); supports.Clear();
            if (!complete) budget.Release(budget.Live);
        }
    }
    static T[] Allocate<T>(int count, int size, MapFoliageColumnBudget budget)
    { budget.Take(MapFoliageInventory.Array(count, size)); return new T[count]; }
}
