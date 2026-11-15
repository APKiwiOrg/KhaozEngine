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
        public readonly bool OpaqueFoliage;
        // The albedo set the shadow depth pass alpha-tests a MASK caster against (issue #15). Built at load only for
        // a mesh with a cutoff AND an albedo texture, so it is null for every opaque mesh and for a MASK mesh with
        // no albedo, both of which keep the depth-only pipeline. Names no shadow resource, so a shadow layout
        // replacement carries it over unchanged.
        public readonly IGpuResourceSet? ShadowCutoutSet;
        // The tile-ground program this mesh's slots need, chosen once at load. Plain for every other mesh.
        public readonly TileGroundProgram TileGroundProgram;

        public Mesh(IGpuBuffer vb, IGpuBuffer outlineNormalVb, IGpuBuffer ib, int indexCount,
            GpuIndexFormat indexFormat, in MeshBounds bounds, IGpuResourceSet? materialSet = null,
            IGpuResourceSet? outlineMaterialSet = null, int splatMaterial = -1, float alphaCutoff = 0f,
            int tileGroundMaterial = -1, IGpuResourceSet? shadowCutoutSet = null, bool opaqueFoliage = false,
            TileGroundProgram tileGroundProgram = TileGroundProgram.Plain)
        {
            TileGroundProgram = tileGroundProgram;
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
            ShadowCutoutSet = shadowCutoutSet;
            OpaqueFoliage = opaqueFoliage;
        }

        /// <summary>Whether this mesh's shadow alpha-tests (see <see cref="ShadowDepthSelection.MeshCutsOutShadow"/>).</summary>
        public bool CutsOutShadow => ShadowDepthSelection.MeshCutsOutShadow(AlphaCutoff, ShadowCutoutSet is not null);
    }
}
