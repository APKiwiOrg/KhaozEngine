using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeMapRenderingCompatibilityTests
{
    [Theory]
    [InlineData(false, 8)]
    [InlineData(true, 8)]
    [InlineData(true, 4)]
    public void PreparedFoliage_WritesTheTemporalVisibilityAttachment(bool temporal, int attachments)
    {
        using var host = new NativeUploadTestScene(new FakeGpuDevice(maxColorAttachments: attachments));
        var scene = host.Scene;
        NativeResourceUploadTests.LoadParts(scene);
        var handle = NativeResourceUploadTests.Publish(scene,
            new(null, null, NativeRenderAssetFixtures.FoliageSource(), default));
        scene.ForceTemporalForTests = temporal;
        scene.Begin();
        scene.DrawPreparedFoliage(handle, Vector3.Zero, new() { DrawRadius = 100, DistantDensity = 1 }, default);
        host.Render();
        var factory = (FakeGpuResourceFactory)host.Device.Factory;
        var prepared = factory.GraphicsPipelines.Where(p => p.VertexGlsl.Contains("uniform SceneAnchor"));
        var pipeline = Assert.Single(prepared);
        bool visibility = temporal && attachments >= 5;
        Assert.Equal(3 + (temporal ? 1 : 0) + (visibility ? 1 : 0), pipeline.Description.Outputs.Colour.Length);
        Assert.Equal(pipeline.Description.Outputs.Colour.Length, pipeline.Description.BlendAttachments.Length);
        if (visibility)
        {
            Assert.Contains("layout(location=4) out float oKeyVisibility;", pipeline.FragmentGlsl);
            Assert.Contains("oKeyVisibility = keyShadow;", pipeline.FragmentGlsl);
        }
        else Assert.DoesNotContain("oKeyVisibility", pipeline.FragmentGlsl);
        scene.ReleaseResource(handle);
    }

    [Theory]
    [InlineData(80.8f, 0f, false)]
    [InlineData(81.24f, .031f, true)]
    [InlineData(100f, .5f, true)]
    [InlineData(118.76f, .969f, true)]
    [InlineData(118.84f, 0f, false)]
    [InlineData(120f, 0f, false)]
    public void NativeHlod_UsesComplementaryColourAndShadowCoverageAtBothSkipGates(float distance, float dissolve, bool complement)
    {
        var mesh = NativeResourceUploadTests.Triangle();
        var instances = new[] { new NativePropInstance("panel", 1, "panel", Matrix4x4.Identity, default) };
        var lods = new Dictionary<string, IReadOnlyList<NativePropLod>>
        {
            ["panel"] = new[] { new NativePropLod(0, new[] { new MeshHandle(1) }) }
        };
        var layer = PropLayer.PlacementLayer(instances, lods, 500, System.Array.Empty<float>())
            .WithHlod(new Dictionary<string, GltfMesh> { ["panel"] = mesh }, 100, 0, 40);
        var request = new NativePropClusterRequest(new("panels", 0, 0, 0), 1,
            new RectArea(-1, -1, 1, 1), layer, instances, default);
        var coverage = PropClusterRenderer.NativeDrawState(request, true, new(distance, 0, 0));
        Assert.Equal(dissolve, coverage.IndividualDissolveFloor, 4);
        Assert.Equal(dissolve, coverage.MergedDissolve, 4);
        Assert.Equal(complement, coverage.MergedComplement);
        var draw = PropClusterRenderer.MergedDraw(new MeshHandle(2), layer, coverage.MergedDissolve,
            coverage.MergedComplement);
        Assert.False(draw.InvertShadowDissolve);
        Assert.Equal(complement ? 1f : 0f, draw.DissolveComplement);
    }
}
