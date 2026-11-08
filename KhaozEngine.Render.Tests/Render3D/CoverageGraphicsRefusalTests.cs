using System;
using System.Linq;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageGraphicsRefusalTests
{
    [Theory]
    [InlineData("model")]
    [InlineData("model-dissolve")]
    [InlineData("motion")]
    [InlineData("cascade")]
    [InlineData("outline")]
    [InlineData("point")]
    public void Vertex_only_refusal_cleans_partial_owners_and_survives_output_changes(string owner)
    {
        using var f = new CoveragePublicationFixture();
        // Prime point-shadow ownership without coverage graphics initialization.
        f.Begin(); f.Scene.AddLight(System.Numerics.Vector3.UnitY, Color.White, 10, 1, LightShadow.Dynamic);
        f.Record(); f.Submit();
        f.Scene.ForceTemporalForTests = owner == "motion";
        f.Device.RefuseVertex = (vertex, fragment) => owner switch
        {
            "model" => vertex == ShaderSources.CoverageModelVert,
            "model-dissolve" => vertex == ShaderSources.CoverageModelVert && fragment == ShaderSources.SkinnedModelDissolveFrag,
            "motion" => vertex == ShaderSources.CoverageModelMotionVert && fragment == ShaderSources.SkinnedModelDissolveMotionFrag,
            "cascade" => vertex == ShaderSources.CoverageCascadeDissolveVert,
            "outline" => vertex == ShaderSources.CoverageOutlineVert && fragment == ShaderSources.TargetOutlineVisibleMaskFrag,
            "point" => vertex == ShaderSources.CoveragePointDissolveVert,
            _ => throw new InvalidOperationException(),
        };
        f.Begin(); f.Queue(outlines: true); f.Record(); f.Submit();
        Assert.Empty(f.Evidence);
        Assert.Equal(CoverageFailureCode.UnsupportedAdapter, f.Scene.CoverageFailureFor(f.Host.Actor).Code);
        // The last layout belongs to the owner whose construction refused. It must not leak until teardown.
        Assert.True(f.Device.Layouts.Last().Disposed);
        var partialShaders = f.Device.Resources.ShaderSets.Where(s => owner switch
        {
            "outline" => s.VertexGlsl == ShaderSources.CoverageOutlineVert,
            "point" => s.VertexGlsl == ShaderSources.CoveragePointVert,
            _ => s.VertexGlsl == ShaderSources.CoverageModelVert || s.VertexGlsl == ShaderSources.CoverageCascadeVert,
        }).ToArray();
        if (owner != "model") Assert.NotEmpty(partialShaders);
        Assert.All(partialShaders, s => Assert.True(s.Disposed));
        foreach (string change in new[] { "temporal", "hdr", "msaa" })
        {
            f.Scene.ForceTemporalForTests = change == "temporal";
            f.Scene.Post.Hdr.Enabled = change == "hdr";
            f.Scene.Post.Quality.AntiAliasing = change == "msaa" ? AntiAliasing.Msaa(4) : AntiAliasing.Fxaa;
            f.Begin(); f.Queue(); f.Record(); f.Submit();
            Assert.Empty(f.Evidence); Assert.Null(f.History.SurfaceToken);
            Assert.Contains(f.Commands.PipelineBinds.OfType<FakePipeline>(), p => p.Request is { } r
                && (r.VertexGlsl == ShaderSources.SkinnedModelVert || r.VertexGlsl == ShaderSources.SkinnedModelMotionVert));
        }
        f.Scene.Dispose();
        Assert.NotEmpty(f.Device.Layouts);
        Assert.All(f.Device.Layouts, l => Assert.True(l.Disposed));
        Assert.NotEmpty(f.Device.Resources.ShaderSets);
        Assert.All(f.Device.Resources.ShaderSets, s => Assert.True(s.Disposed));
    }
}
