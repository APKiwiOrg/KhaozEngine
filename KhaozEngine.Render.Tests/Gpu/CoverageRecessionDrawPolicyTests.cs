using System.Linq;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageRecessionDrawPolicyTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Coverage_vertices_and_carry_keep_precise_policy_including_non_metal_inline_rows(bool inlineHelpers, bool temporal)
    {
        using var inert = new CoveragePublicationDevice();
        using var device = new CoverageRecessionObservedDevice(inert, inlineHelpers);
        using var scene = new CoverageRecessionNativeScene(device, new(4, 4), 1, true);
        scene.Scene.ForceTemporalForTests = temporal;
        using var list = device.Factory.CreateCommandList();
        for (int frame = 0; frame < 2; frame++)
        {
            scene.Prepare(frame);
            list.Begin(); scene.Render(list); list.End(); scene.Submission.Submit(list);
        }
        scene.AssertPublished();
        string[] vertices = [ShaderSources.CoverageModelVert, ShaderSources.CoverageModelMotionVert,
            ShaderSources.CoverageCascadeVert, ShaderSources.CoverageCascadeDissolveVert,
            ShaderSources.CoveragePointVert, ShaderSources.CoveragePointDissolveVert, ShaderSources.CoverageOutlineVert];
        foreach (string vertex in vertices.Where(v => temporal || v != ShaderSources.CoverageModelMotionVert))
        {
            var programs = device.GraphicsShaders.Where(p => p.Vertex == vertex).ToArray();
            Assert.True(programs.Length > 0, vertex);
            Assert.All(programs, p => Assert.True(p.Options.PreciseMath));
        }
        Assert.Equal(5, device.ComputeShaders.Count);
        Assert.All(device.ComputeShaders, p => Assert.True(p.Options.PreciseMath));
        Assert.All(device.GraphicsShaders.Where(p => !vertices.Contains(p.Vertex)), p => Assert.False(p.Options.PreciseMath));
    }
}
