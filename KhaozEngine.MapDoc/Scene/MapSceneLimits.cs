using System;

namespace KhaozEngine.MapDoc.Scene;

/// <param name="MaxTiles">Demand sites: MapSceneSource. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxMetadataRows">Demand sites: MapSceneMetadataReader, MapSceneSource. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxMemberships">Demand sites: MapSceneMetadataReader. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxMembershipsPerResource">Demand sites: MapSceneMetadataReader. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxScannedBytes">Demand sites: MapSceneMetadataReader. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxMetadataBytes">Demand sites: MapSceneMetadataReader, MapSceneSource. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxSelectedResources">Demand sites: MapCaptureDependencies, MapSceneAcquisition, MapSceneAdmission, MapSceneSpatialIndex, NativeMapFoliage. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxPatchScopes">Demand sites: MapSceneAdmission. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxPreparedFaces">Demand sites: MapRoofEvaluationBuffer, MapSceneAdmission. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxPreparedBytes">Demand sites: MapPreparationLedger.Demand, MapRoofEvaluationBuffer, MapSceneAdmission. See docs/CAPACITY-DEMAND.md.</param>
public sealed record MapSceneLimits(int MaxTiles = 65536, int MaxMetadataRows = 65536, int MaxMemberships = 262144,
    int MaxMembershipsPerResource = 4096, long MaxScannedBytes = 134217728, long MaxMetadataBytes = 67108864,
    int MaxSelectedResources = 4096, int MaxPatchScopes = 256, int MaxPreparedFaces = 262144, long MaxPreparedBytes = 67108864)
{
    public MapScenePreparationLimits? Preparation { get; init; }

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
