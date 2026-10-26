using System;
using System.Collections.Generic;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Compact original instances and projected identities. No captured geometry or diagnostic samples are retained.</summary>
internal sealed class MapFoliageInstanceBatch
{
    readonly GroundCoverInstanceColumns? _columns;
    readonly int[]? _x, _z, _supportIndices;
    readonly string?[]? _supports;
    readonly MapFoliageSampleKey[]? _fullKeys;
    readonly MapFoliagePolicy? _policy;
    internal string LayerId { get; }
    internal MapFoliageChunkKey Chunk { get; }
    internal MapFoliageStatus Status { get; }
    internal string DependencyDigest { get; }
    internal int DistributionVersion { get; }
    internal MapFoliageWork Work { get; }
    internal int Count => _columns?.Count ?? 0;
    internal GroundCoverInstanceColumns Columns => _columns ?? throw new InvalidOperationException("No complete compact product.");
    internal MapFoliagePolicy Policy => _policy ?? throw new InvalidOperationException("No complete compact product.");
    internal ReadOnlySpan<string?> SupportIds => _supports;
    internal int MetadataReferenceCapacity => checked(4 + Columns.ModelCount + (_supports?.Length ?? 0) + 4 * (_fullKeys?.Length ?? 0));
    internal object[] CollectMetadata()
    {
        var values = new HashSet<object>(MetadataReferenceCapacity, ReferenceEqualityComparer.Instance);
        void Text(string? value) { if (value is not null) values.Add(value); }
        void PolicyValue(MapFoliagePolicy? value) { if (value is not null) { values.Add(value); Text(value.Id); } }
        Text(LayerId); Text(DependencyDigest); PolicyValue(_policy);
        foreach (var id in Columns.BorrowModelIds) Text(id);
        if (_supports is not null) foreach (var id in _supports) Text(id);
        if (_fullKeys is not null)
            foreach (var key in _fullKeys) { Text(key.Address.LayerId); Text(key.Address.SupportOwnerId); PolicyValue(key.Policy); }
        var result = new object[values.Count]; values.CopyTo(result); return result;
    }
    internal MapFoliageSampleKey Key(int index) => _fullKeys is not null ? _fullKeys[index] :
        new(new(LayerId, _supports![_supportIndices![index]], _x![index], _z![index]), DistributionVersion, Policy);

    internal MapFoliageInstanceBatch(string layerId, MapFoliageChunkKey chunk, MapFoliageStatus status, MapFoliageWork work)
    {
        LayerId = layerId; Chunk = chunk; Status = status; Work = work; DependencyDigest = "";
        _x = []; _z = []; _supportIndices = []; _supports = [];
    }
    internal MapFoliageInstanceBatch(string layerId, MapFoliageChunkKey chunk, string digest, MapFoliagePolicy? policy,
        GroundCoverInstanceColumns columns, int[]? x, int[]? z, int[]? supportIndices, string?[]? supports, MapFoliageWork work,
        int distributionVersion = 1, MapFoliageSampleKey[]? fullKeys = null)
    {
        LayerId = layerId; Chunk = chunk; Status = MapFoliageStatus.Complete; DependencyDigest = digest;
        _policy = policy; _columns = columns; _x = x; _z = z; _supportIndices = supportIndices; _supports = supports;
        Work = work; DistributionVersion = distributionVersion; _fullKeys = fullKeys;
    }
}
