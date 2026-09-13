using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public sealed class SilhouetteRendererShapeTests
    {
        [Fact]
        public void Silhouette_pipeline_reads_position_and_welded_normal_from_two_vertex_streams()
        {
            using var device = new FakeGpuDevice();
            var factory = (FakeGpuResourceFactory)device.Factory;
            var outputs = new GpuOutputDescription(
                GpuPixelFormat.D32FloatS8UInt,
                GpuPixelFormat.R8G8B8A8UNorm,
                GpuPixelFormat.R16G16B16A16Float,
                GpuPixelFormat.R16G16B16A16Float);

            using var renderer = new SilhouetteRenderer(device, outputs);

            FakeGraphicsPipelineRequest pipeline = factory.GraphicsPipelines.Single(
                request => request.VertexGlsl == ShaderSources.SilhouetteVert);
            Assert.Collection(pipeline.Description.VertexLayouts!,
                positions =>
                {
                    Assert.Equal(ModelVertex.SizeInBytes, positions.Stride);
                    Assert.Collection(positions.Elements,
                        element => Assert.Equal(GpuVertexElementFormat.Float3, element.Format));
                },
                normals =>
                {
                    Assert.Equal(0u, normals.Stride);
                    Assert.Collection(normals.Elements,
                        element => Assert.Equal(GpuVertexElementFormat.Float3, element.Format));
                });

            IGpuBuffer positions = factory.CreateBuffer(new GpuBufferDescription(3 * ModelVertex.SizeInBytes,
                GpuBufferUsage.VertexBuffer));
            IGpuBuffer normals = factory.CreateBuffer(new GpuBufferDescription(3 * sizeof(float) * 3,
                GpuBufferUsage.VertexBuffer));
            IGpuBuffer indices = factory.CreateBuffer(new GpuBufferDescription(3 * sizeof(ushort),
                GpuBufferUsage.IndexBuffer));
            using var commands = new CommandTallyGpuCommandList(factory.CreateCommandList());
            renderer.EnsureCapacity(1);
            renderer.BeginFrame(Matrix4x4.Identity);
            renderer.Enqueue(positions, normals, indices, 3, GpuIndexFormat.UInt16, 0, Matrix4x4.Identity,
                Color.White, 0.05f);

            renderer.Flush(commands);

            Assert.Equal(2, commands.Tally[GpuCommandKind.SetVertexBuffer]);
            positions.Dispose();
            normals.Dispose();
            indices.Dispose();
        }
    }
}
