using System;
using System.Linq;
using System.Numerics;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoveragePerfSceneTests
{
    [Fact]
    public void TypicalGeometryAndRoutesAreExplicit()
    {
        using var off = CoveragePerfSceneProbe.Open(1, covered: false);
        using var on = CoveragePerfSceneProbe.Open(1, covered: true);
        off.Frame(0); on.Frame(0);
        Assert.NotNull(on.Session.Typical);
        var scene = on.Session.Typical!;
        Assert.Equal(2000, scene.Geometry.Topology.SourceTriangles);
        Assert.Equal(200, scene.Geometry.Topology.EligibleTriangles);
        Assert.Equal(2, scene.Geometry.Topology.Commands);
        Assert.Equal(2, scene.Geometry.Topology.Materials);
        Assert.Equal("2", scene.Geometry.Topology.RefinementLevels);
        Assert.Equal(scene.Geometry.Topology.FinalVertices, scene.Actors[0].Conformer.Topology.VertexCount);
        Assert.Equal(scene.Geometry.Topology.FinalTriangles, scene.Actors[0].Conformer.Topology.TriangleCount);
        Assert.All(scene.Actors[0].Commands, c =>
        {
            Assert.Equal(.4f, c.CollarWidth); Assert.Equal(-2, c.MinZ); Assert.Equal(2, c.MaxZ);
            Assert.Equal(.02f, c.Clearance); Assert.Equal(.8f, c.MaxWorldRecession);
        });
        Assert.True(scene.Scene.UseGpuSkinning);
        Assert.True(off.Session.Typical!.Scene.UseGpuSkinning);
        Assert.Equal(1, scene.Scene.DispatchedHostPreparationCount);
        Assert.Equal(0, off.Session.Typical.Scene.DispatchedHostPreparationCount);
        Assert.Equal(2, off.Session.Typical.Scene.UncoveredGpuDrawsForTests);
        Assert.Equal(0, scene.Scene.UncoveredGpuDrawsForTests);
        Assert.Equal(0, scene.Scene.OrdinaryCpuSkinnedVerticesForTests);
        Assert.True(on.Session.EffectiveRoutesValid);
        Assert.True(off.Session.EffectiveRoutesValid);
        Assert.True(on.Session.ReadInfo().Synthetic);
        Assert.False(on.Session.ReadInfo().NativeDevice);
        Assert.Null(scene.Scene.CoverageVerificationForTests);
        Assert.Equal("coverage-typical-gpu/1", scene.Fixture);
        Assert.Equal("full-1280x720/1", scene.Profile);
        Assert.Equal("commands=4,regions=32,vertices=65536,triangles=65536",
            on.Session.ReadInfo().Environment.Single(e => e.Name == "gpuGuards").Value);
        Assert.Equal(off.Session.ReadInfo().FixtureFingerprint, on.Session.ReadInfo().FixtureFingerprint);
        Assert.Equal(off.Session.ReadInfo().ProfileFingerprint, on.Session.ReadInfo().ProfileFingerprint);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 32)]
    [InlineData(true, 32)]
    public void EveryActorChangesPoseAndRemainsVisible(bool covered, int actors)
    {
        using var run = CoveragePerfSceneProbe.Open(actors, covered);
        var first = run.Frame(0);
        Assert.Equal(actors, first.MainVisibleActors);
        var scene = run.Session.Typical!;
        var palettes = scene.Actors.Select(a => a.Palette.ToArray()).ToArray();
        var second = run.Frame(1);
        Assert.Equal(actors, second.MainVisibleActors);
        Assert.NotEqual(first.PoseFingerprint, second.PoseFingerprint);
        Assert.Equal(actors, scene.Actors.Select(a => a.Key).Distinct().Count());
        Assert.Equal(actors, scene.Actors.Select(a => a.Palette).Distinct().Count());
        for (int i = 0; i < actors; i++)
        {
            Assert.NotEqual(palettes[i][1], scene.Actors[i].Palette[1]);
            Assert.Equal((1 + i) % 2, scene.Actors[i].PoseId);
        }
        Assert.True(scene.Scene.FrustumCulling);
        Assert.Equal(.1f, scene.Scene.Camera.NearPlane);
        Assert.Equal(1280u, run.Session.Device.SwapchainFramebuffer!.Width);
        Assert.Equal(720u, run.Session.Device.SwapchainFramebuffer.Height);
        if (actors == 32)
        {
            Assert.Equal(new Vector3(-10.5f, -2.25f, 0), scene.Actors[0].World.Translation);
            Assert.Equal(new Vector3(10.5f, 2.25f, 0), scene.Actors[31].World.Translation);
            Assert.Equal(32, scene.Actors.SelectMany(a => a.Commands.Select(c => c.EquipmentInstanceId)).Distinct().Count());
        }
        run.Session.CompleteBatch([first, second]);
        Assert.NotNull(scene.Scene.PreviousFrameView);
    }

    [Fact]
    public void FullPassScheduleMatchesBetweenConditions()
    {
        using var off = CoveragePerfSceneProbe.Open(1, covered: false);
        using var on = CoveragePerfSceneProbe.Open(1, covered: true);
        var offWarmup = off.Warm(); var onWarmup = on.Warm();
        Assert.Equal(3, offWarmup[0].ProductionWaits);
        Assert.Equal(3, onWarmup[0].ProductionWaits);
        Assert.Equal(0, offWarmup[0].PointShadowFaces);
        Assert.Equal(0, onWarmup[0].PointShadowFaces);
        for (int frame = 0; frame < 3; frame++)
        {
            var a = off.Frame(CoveragePerfManifest.WarmupFrameCount + frame);
            var b = on.Frame(CoveragePerfManifest.WarmupFrameCount + frame);
            Assert.Equal(15, a.Passes);
            Assert.Equal(a.Passes, b.Passes);
            Assert.Equal(6, a.PointShadowFaces);
            Assert.Equal(a.PointShadowFaces, b.PointShadowFaces);
            Assert.Equal(a.PassFingerprint, b.PassFingerprint);
            Assert.True(off.Session.Typical!.Scene.ResolvedLastRenderForTests);
            Assert.True(on.Session.Typical!.Scene.ResolvedLastRenderForTests);
            Assert.Equal(4, on.Session.Typical.Scene.LastShadowPassDiagnostics.CascadeCount);
            Assert.Equal(1, on.Session.Typical.Scene.TargetOutlineGroupsRenderedForTests);
            Assert.True(b.StageComplete);
            Assert.False(b.Fallback);
            Assert.Equal(0, a.ProductionWaits);
            Assert.Equal(0, b.ProductionWaits);
            Assert.Equal(0, a.ResourceGrowth);
            Assert.Equal(0, b.ResourceGrowth);
            Assert.Equal(1, b.Submissions);
        }
    }
}
