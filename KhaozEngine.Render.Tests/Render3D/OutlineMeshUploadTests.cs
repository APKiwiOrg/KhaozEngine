using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public sealed class OutlineMeshUploadTests
    {
        [Fact]
        public void Rigid_mesh_upload_owns_and_retires_a_compact_outline_normal_stream()
        {
            using var device = new FakeGpuDevice();
            var factory = (FakeGpuResourceFactory)device.Factory;
            using IGpuTexture target = factory.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));
            using IGpuFramebuffer framebuffer = factory.CreateFramebuffer(null, target);
            using var scene = new Scene3D(device, framebuffer.Outputs);
            int from = factory.Buffers.Count;

            MeshHandle handle = scene.LoadMesh(Triangle());

            List<FakeBuffer> buffers = factory.Buffers.GetRange(from, factory.Buffers.Count - from);
            Assert.Collection(buffers,
                positions => Assert.Equal(3 * ModelVertex.SizeInBytes, positions.SizeInBytes),
                normals => Assert.Equal(3u * sizeof(float) * 3u, normals.SizeInBytes),
                indices => Assert.Equal(3u * sizeof(ushort), indices.SizeInBytes));

            scene.UnloadMesh(handle);
            foreach (FakeBuffer buffer in buffers) Assert.False(buffer.Disposed);
            for (int i = 0; i < GpuRetireQueue.DefaultFrameDelay; i++) scene.Begin();
            foreach (FakeBuffer buffer in buffers) Assert.True(buffer.Disposed);
        }

        static GltfMesh Triangle()
        {
            var vertices = new[]
            {
                new ModelVertex(Vector3.Zero, Vector3.UnitZ, Vector4.One),
                new ModelVertex(Vector3.UnitX, Vector3.UnitZ, Vector4.One),
                new ModelVertex(Vector3.UnitY, Vector3.UnitZ, Vector4.One),
            };
            return new GltfMesh(vertices, new uint[] { 0, 1, 2 });
        }
    }
}
