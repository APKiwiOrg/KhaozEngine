using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    // The filled-overlay queue lives on a live Scene3D (its ctor needs a GPU device), so these run gated behind
    // KE_GPU_TESTS=1. They assert the queue accounting only (geometry correctness is covered headlessly in
    // DebugFillShapesTests; the on-screen blend + draw order is covered by the golden snapshot).
    public sealed class Scene3DFillTests
    {
        [GpuFact]
        public void FilledQuad_And_Circle_Queue_Then_Begin_Clears()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            var f = gd.Factory;
            using IGpuTexture tex = f.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, tex);
            using var scene = new Scene3D(gd, fb.Outputs);

            scene.Begin();
            Assert.Equal(0, scene.FillVertexCount);

            // Square ground tile = 2 triangles = 6 verts.
            scene.DebugFilledQuad(new Vector3(0, 0.05f, 0), halfSize: 0.5f, new Color(0.3f, 0.85f, 0.45f, 0.28f));
            Assert.Equal(6, scene.FillVertexCount);

            // Disc fan of 12 triangles = 36 verts; accumulates on top of the quad.
            scene.DebugFilledCircle(new Vector3(1, 0.05f, 1), Vector3.UnitY, 0.8f, new Color(0.85f, 0.4f, 0.3f, 0.3f), segments: 12);
            Assert.Equal(6 + 36, scene.FillVertexCount);

            // Next frame's Begin clears the queue.
            scene.Begin();
            Assert.Equal(0, scene.FillVertexCount);
        }

        [GpuFact]
        public void FilledFan_Queues_RimCountTriangles_When_Closed()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            var f = gd.Factory;
            using IGpuTexture tex = f.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, tex);
            using var scene = new Scene3D(gd, fb.Outputs);

            // A star-shaped visibility polygon: a centre plus a rim of 5 boundary points.
            var rim = new[]
            {
                new Vector3(1, 0.05f, 0), new Vector3(0.3f, 0.05f, 0.9f), new Vector3(-0.8f, 0.05f, 0.6f),
                new Vector3(-0.8f, 0.05f, -0.6f), new Vector3(0.3f, 0.05f, -0.9f),
            };

            scene.Begin();
            Assert.Equal(0, scene.FillVertexCount);

            // Closed fan = rim.Length triangles = rim.Length*3 verts.
            scene.DebugFilledFan(new Vector3(0, 0.05f, 0), rim, new Color(0.4f, 0.7f, 0.95f, 0.3f), closed: true);
            Assert.Equal(rim.Length * 3, scene.FillVertexCount);

            // Open fan would be one triangle fewer; verify the flag reaches the builder.
            scene.Begin();
            scene.DebugFilledFan(new Vector3(0, 0.05f, 0), rim, new Color(0.4f, 0.7f, 0.95f, 0.3f), closed: false);
            Assert.Equal((rim.Length - 1) * 3, scene.FillVertexCount);

            scene.Begin();
            Assert.Equal(0, scene.FillVertexCount);
        }
    }
}
