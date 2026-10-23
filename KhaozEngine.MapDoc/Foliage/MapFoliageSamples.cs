using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Foliage;

public readonly record struct MapFoliageAddress(string LayerId, string? SupportOwnerId, int X, int Z);
public readonly record struct MapFoliageComparisonAddress(string LayerId, int X, int Z);
public readonly record struct MapFoliageSampleKey(MapFoliageAddress Address, int DistributionVersion, MapFoliagePolicy Policy);

/// <summary>An immutable CPU sample. Transform is the candidate's original matrix, Scale its trace channel and
/// Orientation the normalized decomposition rotation. Context-owned material tables are shared without text copies.</summary>
public sealed record MapFoliageSample
{
    public MapFoliageSample(MapFoliageSampleKey Key, string? OccupiedSpaceId, MapFaceKey? Face, string AssetId,
        IReadOnlyList<string> MaterialResourceIds, ushort VisibleMaterial, Vector3 Position, Quaternion Orientation,
        float Scale, Matrix4x4 Transform, float ThinningRank)
        : this(Key, OccupiedSpaceId, Face, AssetId, Array.AsReadOnly(MaterialResourceIds.ToArray()), VisibleMaterial,
            Position, Orientation, Scale, Transform, ThinningRank, true)
    { }

    internal MapFoliageSample(MapFoliageSampleKey key, string? occupiedSpaceId, MapFaceKey? face, string assetId,
        IReadOnlyList<string> materials, ushort visibleMaterial, Vector3 position, Quaternion orientation,
        float scale, Matrix4x4 transform, float thinningRank, bool shared)
    {
        Key = key; OccupiedSpaceId = occupiedSpaceId; Face = face; AssetId = assetId; MaterialResourceIds = shared ? materials : Array.AsReadOnly(materials.ToArray());
        VisibleMaterial = visibleMaterial; Position = position; Orientation = orientation; Scale = scale;
        Transform = transform; ThinningRank = thinningRank;
    }

    public MapFoliageSampleKey Key { get; }
    public string? OccupiedSpaceId { get; }
    public MapFaceKey? Face { get; }
    public string AssetId { get; }
    public IReadOnlyList<string> MaterialResourceIds { get; }
    public ushort VisibleMaterial { get; }
    public Vector3 Position { get; }
    public Quaternion Orientation { get; }
    public float Scale { get; }
    public Matrix4x4 Transform { get; }
    public float ThinningRank { get; }
}

/// <summary>Only Complete exposes samples and a dependency digest. Observer rows are provisional until completion.</summary>
public sealed record MapFoliageSampleBatch
{
    public MapFoliageSampleBatch(string LayerId, MapFoliageChunkKey Chunk, MapFoliageStatus Status, string DependencyDigest,
        IReadOnlyList<MapFoliageSample> Samples, MapFoliageWork Work)
    {
        this.LayerId = LayerId; this.Chunk = Chunk; this.Status = Status; this.Work = Work;
        this.DependencyDigest = Status == MapFoliageStatus.Complete ? DependencyDigest : "";
        this.Samples = Array.AsReadOnly(Status == MapFoliageStatus.Complete ? Samples.ToArray() : []);
    }
    public string LayerId { get; }
    public MapFoliageChunkKey Chunk { get; }
    public MapFoliageStatus Status { get; }
    public string DependencyDigest { get; }
    public IReadOnlyList<MapFoliageSample> Samples { get; }
    public MapFoliageWork Work { get; }
}

public sealed record MapFoliageDecision(MapFoliageAddress Address, string? OccupiedSpaceId, MapFoliageReason Reason,
    GroundCoverDecision Distribution, MapFoliageSample? Sample)
{
    // Rejected rows also retain the sampled material and face for diagnostic comparison, without a sample allocation.
    internal MapFoliageSurfacePoint? Surface { get; init; }
}

public sealed record MapFoliageQueryResult
{
    public MapFoliageQueryResult(MapFoliageStatus Status, IReadOnlyList<MapFoliageSampleBatch> Batches, MapFoliageWork Work)
    {
        this.Status = Status; this.Work = Work;
        this.Batches = Array.AsReadOnly(Status == MapFoliageStatus.Complete ? Batches.ToArray() : []);
    }
    public MapFoliageStatus Status { get; }
    public IReadOnlyList<MapFoliageSampleBatch> Batches { get; }
    public MapFoliageWork Work { get; }
}
