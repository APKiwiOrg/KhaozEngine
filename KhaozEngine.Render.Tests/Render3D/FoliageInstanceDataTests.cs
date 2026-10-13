using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class FoliageInstanceDataTests
{
    [Fact]
    public void InstanceStrideAndLocationTenPackTheProfile()
    {
        Assert.Equal(96, Marshal.SizeOf<ModelRenderer.FoliageInstanceData>());
        Assert.Equal(80, (int)Marshal.OffsetOf<ModelRenderer.FoliageInstanceData>("Motion"));
        Assert.Equal(96u, ModelRenderer.FoliageInstanceData.SizeInBytes);
        GpuVertexLayoutDescription[] layouts = FoliageVertexLayouts.Create();
        Assert.Equal(2, layouts.Length);
        Assert.Equal(5, layouts[0].Elements.Length);
        GpuVertexLayoutDescription layout = layouts[1];
        Assert.Equal(96u, layout.Stride);
        Assert.Equal(1u, layout.InstanceStepRate);
        Assert.Equal(6, layout.Elements.Length);
        Assert.Equal("FoliageMotion", layout.Elements[5].Name);
        Assert.Equal(GpuVertexElementFormat.Float4, layout.Elements[5].Format);
        Assert.Contains("layout(location=10) in vec4 FoliageMotion;", ShaderSources.FoliageVert);
        Assert.Contains("layout(location=10) in vec4 FoliageMotion;", ShaderSources.FoliageMotionVert);
    }

    [Fact]
    public void RetainedInstancesPackDefaultAndAuthoredCoefficients()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        MeshHandle mesh = scene.LoadMesh(MeshPrimitives.Box(1f));
        using FoliageBatch batch = scene.CreateFoliageBatch(new[]
        {
            new FoliageInstance(mesh, Matrix4x4.Identity, .1f),
            new FoliageInstance(mesh, Matrix4x4.CreateTranslation(1f, 0f, 0f), .2f,
                new FoliageMotionProfile(.3f, .15f, .4f, .1f)),
        });

        Assert.NotNull(batch.Pending);
        Assert.Equal(2, batch.Pending.Length);
        Assert.Equal(new Vector4(1f, .65f, 1f, 0f), batch.Pending[0].Motion);
        Assert.Equal(new Vector4(.3f, .15f, .4f, .1f), batch.Pending[1].Motion);
    }
}
