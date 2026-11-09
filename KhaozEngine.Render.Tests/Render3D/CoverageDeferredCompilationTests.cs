using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageDeferredCompilationTests
{
    [Theory]
    [InlineData("compute")]
    [InlineData("model")]
    [InlineData("cascade")]
    [InlineData("outline")]
    [InlineData("point")]
    [InlineData("variant")]
    [InlineData("variant-legacy")]
    [InlineData("variant-late")]
    public void Deferred_compilation_refuses_inside_the_optional_transaction_before_binding(string owner)
    {
        bool Refuse(string vertex, string? fragment) => owner switch
        {
            "compute" => fragment is null,
            "model" => vertex == ShaderSources.CoverageModelVert,
            "cascade" => vertex == ShaderSources.CoverageCascadeVert,
            "outline" => vertex == ShaderSources.CoverageOutlineVert,
            "point" => vertex == ShaderSources.CoveragePointVert,
            "variant" or "variant-legacy" or "variant-late" => vertex == ShaderSources.CoverageModelMotionVert,
            _ => throw new InvalidOperationException(),
        };
        bool variant = owner.StartsWith("variant", StringComparison.Ordinal);
        using var fixture = new CoveragePublicationFixture(deferred: true, deferredFailure: variant ? null : Refuse);
        fixture.Scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
        fixture.Begin(); fixture.Scene.AddLight(Vector3.UnitY, Color.White, 10, 1, LightShadow.Dynamic);
        fixture.Record(); fixture.Submit();
        if (variant)
        {
            fixture.Begin(); fixture.Queue(outlines: true); fixture.Record(); fixture.Submit();
            Assert.NotEmpty(fixture.Evidence);
            fixture.Scene.ForceTemporalForTests = owner != "variant-late";
        }
        var history = fixture.History;
        fixture.Device.FailDeferredCompilation = Refuse;
        fixture.Begin(); fixture.Queue(outlines: true);
        fixture.Scene.AddLight(Vector3.UnitY, Color.White, 10, 1, LightShadow.Dynamic);
        if (owner == "variant-late") fixture.Record(false, () => fixture.Scene.ForceTemporalForTests = true);
        else fixture.Record(owner == "variant-legacy");
        Assert.Empty(fixture.Evidence);
        Assert.Equal(CoverageFailureCode.UnsupportedAdapter, fixture.Scene.CoverageFailureFor(fixture.Host.Actor).Code);
        Assert.Equal(history, fixture.History);
        var refused = fixture.Device.DeferredPrograms.Where(program => program.Fails).ToArray();
        Assert.NotEmpty(refused);
        Assert.All(refused, program => Assert.False(program.NativeConsumed));
        Assert.Contains(fixture.Commands.PipelineBinds.OfType<FakePipeline>(), pipeline => pipeline.Request is { } r
            && (r.VertexGlsl == ShaderSources.SkinnedModelVert || r.VertexGlsl == ShaderSources.SkinnedModelMotionVert));
        fixture.Submit();
        Assert.Equal(history, fixture.History);
        fixture.Device.FailDeferredCompilation = null;
        fixture.Begin(); fixture.Queue(); fixture.Record(); fixture.Submit();
        Assert.Empty(fixture.Evidence);
        Assert.Equal(CoverageFailureCode.UnsupportedAdapter, fixture.Scene.CoverageFailureFor(fixture.Host.Actor).Code);
        fixture.Scene.Dispose();
        Assert.NotEmpty(fixture.Device.DeferredPrograms);
        Assert.All(fixture.Device.DeferredPrograms, program => Assert.True(program.Disposed));
        Assert.NotEmpty(fixture.Device.Resources.Pipelines);
        Assert.All(fixture.Device.Resources.Pipelines, pipeline => Assert.True(pipeline.Disposed));
    }
}
