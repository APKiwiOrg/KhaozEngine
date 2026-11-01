using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using static KhaozEngine.Tests.Gpu.RendererPerf.RendererPerfGeometryTraceTests;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfNullTraceTests
{
    [Theory]
    [InlineData(true, "cold", 0)]
    [InlineData(false, "cold", 0)]
    [InlineData(true, "first-point-rows", 1)]
    [InlineData(false, "first-point-rows", 1)]
    [InlineData(true, "cached", 3)]
    [InlineData(false, "cached", 3)]
    [InlineData(true, "invalidated", 3)]
    [InlineData(false, "invalidated", 3)]
    public void Null_observer_preserves_render_trace(bool gpu, string state, int age)
    {
        using var rig = new GeometryRig(gpu);
        using var direct = new GeometryRig(gpu);
        for (int frame = 0; frame < age; frame++) { rig.Frame(); direct.Frame(); }
        bool invalidated = state == "invalidated";
        if (invalidated)
        {
            rig.Scene.Post.Atmosphere.GroundAlbedo = .4f;
            direct.Scene.Post.Atmosphere.GroundAlbedo = .4f;
        }
        var observer = new GeometryObserver(rig.Commands);
        rig.Scene.PassObserver = observer;
        float offset = invalidated ? .2f : 0f;
        rig.Frame(rigidOffset: offset);
        direct.Frame(rigidOffset: offset);
        observer.AssertClosed();
        Assert.Null(direct.Scene.PassObserver);
        Assert.NotEmpty(observer.Passes);
        Assert.Equal(age > 0 && state != "cached" ? 1 : 0, rig.Scene.LastShadowPassDiagnostics.PointStaticRebuilds);
        Assert.Equal(age > 0 ? 1 : 0, rig.Scene.LastShadowPassDiagnostics.PointDynamicRenders);
        Assert.Equal(age >= 3 ? 1 : 0, rig.Scene.LastShadowPassDiagnostics.PointTransientRowsRendered);
        Assert.Contains(observer.Passes, p => p.Key.Id == "shadow.directional");
        Assert.Contains(observer.Passes, p => p.Key.Id == (gpu ? "opaque.gpu-skinned" : "opaque.cpu-skinned"));
        AssertDraw(gpu ? ShaderSources.SkinnedModelMotionVert : ShaderSources.ModelCpuSkinnedMotionVert, fragment: false);
        if (age == 0) Assert.Contains(observer.Skipped, p => p.Id == "shadow.point");
        else
        {
            if (state == "cached") Assert.Contains(observer.Skipped, p => p.Id == "shadow.point.static");
            else Assert.Contains(observer.Passes, p => p.Key.Id == "shadow.point.static");
            Assert.Contains(observer.Passes, p => p.Key.Id == "shadow.point.dynamic");
            Assert.Equal(age >= 3, observer.Passes.Any(p => p.Key.Id == "shadow.point.transient"));
        }
        foreach (string id in new[] { "atmosphere.transmittance", "atmosphere.multiple-scattering" })
        {
            Assert.Equal(age == 0 || invalidated, observer.Passes.Any(p => p.Key.Id == id));
            Assert.Equal(age > 0 && !invalidated, observer.Skipped.Any(p => p.Id == id));
        }
        AssertDraw(ShaderSources.AtmosphereTransmittanceFrag, fragment: true, expected: age == 0 || invalidated);
        AssertDraw(ShaderSources.AtmosphereMultiScatteringFrag, fragment: true, expected: age == 0 || invalidated);
        AssertDraw(ShaderSources.AtmosphereSkyViewFrag, fragment: true);
        Assert.Equal(Trace(rig.Commands), Trace(direct.Commands));

        void AssertDraw(string shader, bool fragment, bool expected = true) =>
            Assert.Equal(expected, rig.Commands.Events.Any(e => e.Op.StartsWith("draw", System.StringComparison.Ordinal)
                && e.Pipeline is FakePipeline { Request: { } request }
                && (fragment ? request.FragmentGlsl : request.VertexGlsl) == shader));
    }

    static GeometryTraceCommands.Event[] Trace(GeometryTraceCommands commands) =>
        commands.Events.Select(e => e with { Owner = null, Pipeline = null }).ToArray();
}
