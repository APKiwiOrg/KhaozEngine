using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

/// <summary>One completed CPU build. The mesh here is the very instance submitted for upload.
/// Surface is absent when optical capture was not requested. An opted-in null mesh has a known-empty
/// surface. Retaining Surface never retains the much larger mutable render mesh.</summary>
internal sealed record TileGroundCpuBuild(GltfMesh? Mesh, TileGroundSurfaceSnapshot? Surface, int AuditReadCount = 0)
{
    internal static TileGroundCpuBuild Build(TileWorldDocument document, TileWorldCatalogs catalogs,
        RegionCoord region, int plane, TileGroundLod lod, TileGroundMesherOptions options, bool captureOpticalSurface)
    {
        var audit = captureOpticalSurface ? new TileGroundSurfaceAudit.Builder(document, catalogs, plane, options) : null;
        GltfMesh? mesh = TileGroundMesher.Build(document, catalogs, region, plane, lod, options,
            memoiseCorners: true, audit);
        return new TileGroundCpuBuild(mesh, audit is null ? null :
            new TileGroundSurfaceSnapshot(document, region, plane, lod, mesh, audit.Complete()), audit?.ReadCount ?? 0);
    }
}
