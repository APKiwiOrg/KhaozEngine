using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

/// <summary>One completed CPU build. The mesh here is the very instance submitted for upload.
/// Retaining Surface never retains the much larger mutable render mesh.</summary>
internal sealed record TileGroundCpuBuild(GltfMesh? Mesh, TileGroundSurfaceSnapshot Surface)
{
    internal static TileGroundCpuBuild Build(TileWorldDocument document, TileWorldCatalogs catalogs,
        RegionCoord region, int plane, TileGroundLod lod, TileGroundMesherOptions options)
    {
        var audit = new TileGroundSurfaceAudit.Builder(document, catalogs, plane, options);
        GltfMesh? mesh = TileGroundMesher.Build(document, catalogs, region, plane, lod, options,
            memoiseCorners: true, audit);
        return new TileGroundCpuBuild(mesh,
            new TileGroundSurfaceSnapshot(document, region, plane, lod, mesh, audit.Complete()));
    }
}
