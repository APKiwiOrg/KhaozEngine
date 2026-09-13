using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    readonly struct Mesh
    {
        public readonly IGpuBuffer Vb, OutlineNormalVb, Ib;
        public readonly int IndexCount;
        public readonly GpuIndexFormat IndexFormat;
        public readonly IGpuResourceSet? MaterialSet;
        public readonly IGpuResourceSet? OutlineMaterialSet;
        public readonly int SplatMaterial;
        public readonly int TileGroundMaterial;
        public readonly MeshBounds Bounds;
        public readonly float AlphaCutoff;

        public Mesh(IGpuBuffer vb, IGpuBuffer outlineNormalVb, IGpuBuffer ib, int indexCount,
            GpuIndexFormat indexFormat, in MeshBounds bounds, IGpuResourceSet? materialSet = null,
            IGpuResourceSet? outlineMaterialSet = null, int splatMaterial = -1, float alphaCutoff = 0f,
            int tileGroundMaterial = -1)
        {
            Vb = vb;
            OutlineNormalVb = outlineNormalVb;
            Ib = ib;
            IndexCount = indexCount;
            IndexFormat = indexFormat;
            Bounds = bounds;
            MaterialSet = materialSet;
            OutlineMaterialSet = outlineMaterialSet;
            SplatMaterial = splatMaterial;
            AlphaCutoff = alphaCutoff;
            TileGroundMaterial = tileGroundMaterial;
        }
    }
}
