using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // On-device check that 32-bit (UInt32) mesh indices actually rasterize on a real backend (Metal here; the
    // same Veldrid GpuIndexFormat path drives D3D11 + Vulkan in the golden CI). The decisive trick: the visible
    // triangle is referenced ONLY by indices past the 16-bit ceiling (65536..65538). If the index buffer were
    // wrongly created/bound as 16-bit, those indices would truncate to 0,1,2 and reference the degenerate padding
    // verts at the origin, so nothing would cover the centre. A correct 32-bit index buffer renders the triangle.
    // The 16-bit case (same geometry at indices 0..2) is rendered too, so this is a multi-format check.
    // Skipped unless KE_GPU_TESTS=1 (needs a Metal device).
    public sealed class Uint32IndexGpuTests
    {
        const int W = 128, H = 128;

        // A big +Z-facing triangle whose 3 real verts sit at indices baseIndex..baseIndex+2; everything below
        // baseIndex is degenerate padding at the origin (never referenced by a triangle).
        static GltfMesh CameraFacingTriangle(uint baseIndex)
        {
            int n = (int)baseIndex + 3;
            var verts = new ModelVertex[n];
            for (uint i = 0; i < baseIndex; i++)
                verts[i] = new ModelVertex(Vector3.Zero, Vector3.UnitZ, Vector4.One);
            var col = new Vector4(0.85f, 0.35f, 0.2f, 1f);
            verts[baseIndex + 0] = new ModelVertex(new Vector3(-1.2f, -1.0f, 0f), Vector3.UnitZ, col);
            verts[baseIndex + 1] = new ModelVertex(new Vector3(1.2f, -1.0f, 0f), Vector3.UnitZ, col);
            verts[baseIndex + 2] = new ModelVertex(new Vector3(0.0f, 1.4f, 0f), Vector3.UnitZ, col);
            var idx = new[] { baseIndex + 0, baseIndex + 1, baseIndex + 2 };
            return new GltfMesh(verts, idx);
        }

        static (bool centreOpaque, bool centreLit, bool cornerClear) RenderCentre(GltfMesh mesh)
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);

            MeshHandle h = preview.Scene.LoadMesh(mesh);
            preview.Scene.Camera.Frame(Vector3.Zero, new Vector3(2f, 2f, 2f));

            Texture2D tex = preview.Capture(scene => scene.Draw(h, Matrix4x4.Identity, new Color(0.85f, 0.35f, 0.2f, 1f)));
            byte[] rgba = GpuReadback.ToRgba(gd, tex.Handle, W, H);

            int c = ((H / 2) * W + (W / 2)) * 4;
            int k = (3 * W + 3) * 4;
            return (rgba[c + 3] > 200, rgba[c + 0] + rgba[c + 1] + rgba[c + 2] > 40, rgba[k + 3] < 40);
        }

        [GpuFact]
        public void UInt32_Indexed_Triangle_Past_The_16bit_Ceiling_Rasterizes()
        {
            var mesh = CameraFacingTriangle(ushort.MaxValue + 1); // indices 65536..65538
            Assert.Equal(GpuIndexFormat.UInt32, mesh.IndexFormat);

            var (opaque, lit, cornerClear) = RenderCentre(mesh);
            Assert.True(opaque, "centre should be covered by the 32-bit-indexed triangle");
            Assert.True(lit, "centre should carry the triangle's colour, not background");
            Assert.True(cornerClear, "corner should stay transparent background");
        }

        [GpuFact]
        public void UInt16_And_UInt32_Render_The_Same_Triangle()
        {
            var small = CameraFacingTriangle(0);                  // indices 0..2 -> UInt16
            var large = CameraFacingTriangle(ushort.MaxValue + 1); // indices 65536..65538 -> UInt32
            Assert.Equal(GpuIndexFormat.UInt16, small.IndexFormat);
            Assert.Equal(GpuIndexFormat.UInt32, large.IndexFormat);

            var s = RenderCentre(small);
            var l = RenderCentre(large);
            Assert.Equal((s.centreOpaque, s.centreLit, s.cornerClear), (l.centreOpaque, l.centreLit, l.cornerClear));
            Assert.True(l.centreOpaque && l.centreLit && l.cornerClear, "32-bit path must match the 16-bit path");
        }
    }
}
