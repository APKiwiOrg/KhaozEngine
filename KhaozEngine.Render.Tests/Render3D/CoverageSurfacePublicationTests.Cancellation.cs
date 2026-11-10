using System;
using System.Linq;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed partial class CoverageSurfacePublicationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void A_cancelled_publication_cannot_change_the_survivors_reserved_group(bool gpu, bool unload)
    {
        using var fixture = new CoverageMultiActorFixture();
        fixture.Scene.UseGpuSkinning = gpu;
        fixture.Scene.ForceTemporalForTests = true;
        fixture.Scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
        var read = new CoverageRecordedBindings(fixture);
        fixture.Begin(); fixture.Queue(fixture.First); fixture.Queue(fixture.Second);
        AddActorLights(fixture); fixture.Record(); var previous = read.Outputs(); fixture.Submit();
        fixture.Begin(); fixture.Queue(fixture.First); fixture.Queue(fixture.Second); AddActorLights(fixture);
        fixture.Scene.PrepareFrame(128, 96);
        if (unload)
            foreach (var mesh in fixture.First.Meshes) fixture.Scene.UnloadSkinnedMesh(mesh);
        else fixture.First.Binding.Dispose();
        fixture.Record();
        Assert.Contains(fixture.Root.Evidence, e => e.Actor == fixture.Second.Key);
        Assert.DoesNotContain(fixture.Root.Evidence, e => e.Actor == fixture.First.Key);
        var outputs = read.Outputs(); var draws = read.Draws();
        Assert.Single(outputs); Assert.NotEmpty(draws);
        Assert.All(draws, d => read.AssertActor(d, fixture.Second, outputs[fixture.Second.Key],
            previous[fixture.Second.Key], fixture.Second.Model));
        fixture.Submit();
        Assert.NotNull(fixture.Scene.CoverageHistoryForTests(fixture.Second.Key).SurfaceToken);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Removing_an_earlier_ordinary_outline_part_cannot_shift_coverage_bindings(bool gpu)
    {
        using var fixture = new CoverageMultiActorFixture();
        fixture.Scene.UseGpuSkinning = gpu;
        fixture.Begin();
        var group = fixture.Scene.BeginMeshOutline(KhaozEngine.Primitives.Color.White, 2);
        fixture.Scene.DrawSkinnedOutline(group, fixture.Root.Host.Hosts[0], fixture.Root.Host.Palette,
            System.Numerics.Matrix4x4.Identity);
        fixture.Queue(fixture.First); fixture.Queue(fixture.Second);
        fixture.Scene.PrepareFrame(128, 96);
        fixture.Scene.UnloadSkinnedMesh(fixture.Root.Host.Hosts[0]);
        fixture.Record();
        var read = new CoverageRecordedBindings(fixture);
        var outputs = read.Outputs(); var draws = read.Draws();
        Assert.Equal(2, outputs.Count); Assert.NotEmpty(draws);
        foreach (var actor in new[] { fixture.First, fixture.Second })
        {
            var masks = draws.Where(d => d.Model == actor.Model && d.Pass is "full" or "visible").ToArray();
            Assert.NotEmpty(masks);
            Assert.All(masks, d => read.AssertActor(d, actor, outputs[actor.Key], outputs[actor.Key], actor.Model));
        }
        fixture.Submit();
    }

}
