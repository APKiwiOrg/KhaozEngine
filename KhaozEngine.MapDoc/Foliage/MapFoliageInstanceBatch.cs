using System;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Compact original instances and projected identities. No captured geometry or diagnostic samples are retained.</summary>
internal sealed class MapFoliageInstanceBatch
{
    readonly GroundCoverInstanceColumns? _columns;
    readonly int[] _x, _z, _supportIndices;
    readonly string?[] _supports;
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
    internal MapFoliageSampleKey Key(int index) => new(new(LayerId, _supports[_supportIndices[index]], _x[index], _z[index]), DistributionVersion, Policy);

    internal MapFoliageInstanceBatch(string layerId, MapFoliageChunkKey chunk, MapFoliageStatus status, MapFoliageWork work)
    {
        LayerId = layerId; Chunk = chunk; Status = status; Work = work; DependencyDigest = "";
        _x = []; _z = []; _supportIndices = []; _supports = [];
    }
    internal MapFoliageInstanceBatch(string layerId, MapFoliageChunkKey chunk, string digest, MapFoliagePolicy policy,
        GroundCoverInstanceColumns columns, int[] x, int[] z, int[] supportIndices, string?[] supports, MapFoliageWork work,
        int distributionVersion = 1)
    {
        LayerId = layerId; Chunk = chunk; Status = MapFoliageStatus.Complete; DependencyDigest = digest;
        _policy = policy; _columns = columns; _x = x; _z = z; _supportIndices = supportIndices; _supports = supports;
        Work = work; DistributionVersion = distributionVersion;
    }
}
