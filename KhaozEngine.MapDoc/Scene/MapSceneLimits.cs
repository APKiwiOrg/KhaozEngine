using System;

namespace KhaozEngine.MapDoc.Scene;

public sealed record MapSceneLimits(int MaxTiles = 65536, int MaxMetadataRows = 65536, int MaxMemberships = 262144,
    int MaxMembershipsPerResource = 4096, long MaxScannedBytes = 134217728, long MaxMetadataBytes = 67108864,
    int MaxSelectedResources = 4096, int MaxPatchScopes = 256, int MaxPreparedFaces = 262144, long MaxPreparedBytes = 67108864)
{
    internal void Validate()
    {
        if (MaxTiles < 0 || MaxMetadataRows < 0 || MaxMemberships < 0 || MaxMembershipsPerResource < 0 ||
            MaxScannedBytes < 0 || MaxMetadataBytes < 0 || MaxSelectedResources < 0 || MaxPatchScopes < 0 ||
            MaxPreparedFaces < 0 || MaxPreparedBytes < 0) throw new ArgumentException("Invalid scene limits.");
    }
}

/// <summary>Logical acquisitions include cache hits. Index visits include every tested rejected sibling node.</summary>
public sealed record MapSceneWork(long TileReads, long MetadataRows, long Memberships, long ScannedBytes,
    long RetainedBytes, long PreparedFaces, long PayloadReads, long IndexNodesVisited);
