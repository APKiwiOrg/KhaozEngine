using System;
using System.Linq;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageMixedRouteTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Capability_fallback_never_builds_the_pulled_vertex_programs(bool compute, bool failBuild)
    {
        using var h = new CoverageSceneFixture(supportsCompute: compute, failCoverageBuild: failBuild);
        h.Scene.UseGpuSkinning = false;
        h.Begin();
        var frame = h.Stage();
        var draw = h.HostDraw(frame);
        h.Scene.DrawSkinned(draw, h.Palette);
        h.Outline(draw);
        h.SubmitEquipment();
        h.Render();
        Assert.Equal(CoveragePresentation.UncoveredFallback, h.Scene.GetCoverageOutcome(frame).Presentation);
        Assert.Equal(1, h.Scene.UncoveredGpuDrawsForTests);
        Assert.DoesNotContain(h.Factory.ShaderRequests, s => s.VertexGlsl == ShaderSources.CoverageModelVert
            || s.VertexGlsl == ShaderSources.CoverageOutlineVert);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pulled_and_uncovered_gpu_draws_coexist(bool reverse)
    {
        using var h = new CoverageSceneFixture();
        h.Begin();
        var frame = h.Stage();
        var covered = h.HostDraw(frame);
        var uncovered = h.UncoveredDraw();
        if (reverse) h.Scene.DrawSkinned(uncovered, h.Palette);
        h.Scene.DrawSkinned(covered, h.Palette);
        if (!reverse) h.Scene.DrawSkinned(uncovered, h.Palette);
        h.SubmitEquipment();
        h.Outline(covered);
        h.Outline(uncovered);
        h.Render();
        Assert.Equal(1, h.Scene.DispatchedHostPreparationCount);
        Assert.Equal(1, h.Scene.UncoveredGpuDrawsForTests);
        h.AssertCanonicalDraws(frame);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pulled_and_uncovered_cpu_draws_keep_independent_ranges(bool reverse)
    {
        using var h = new CoverageSceneFixture();
        h.Scene.UseGpuSkinning = false;
        h.Begin();
        var frame = h.Stage();
        var covered = h.HostDraw(frame);
        var uncovered = h.UncoveredDraw();
        if (reverse) h.Scene.DrawSkinned(uncovered, h.Palette);
        h.Scene.DrawSkinned(covered, h.Palette);
        if (!reverse) h.Scene.DrawSkinned(uncovered, h.Palette);
        h.SubmitEquipment();
        h.Outline(covered);
        h.Outline(uncovered);
        h.Render();
        Assert.Equal(1, h.Scene.DispatchedHostPreparationCount);
        Assert.Equal(0, h.Scene.UncoveredGpuDrawsForTests);
        h.AssertCanonicalDraws(frame);
        var mesh = h.UncoveredMesh;
        Assert.Equal(mesh.Vertices.Length, h.Scene.OrdinaryCpuSkinnedVerticesForTests);
        var composed = h.Palette.Select((m, i) => SkinningMath.Compose(m, mesh.InverseBind[i])).ToArray();
        var expected = mesh.Vertices.Select(v => SkinningMath.SkinVertex(v, composed)).ToArray();
        byte[] bytes = MemoryMarshal.AsBytes(expected.AsSpan()).ToArray();
        Assert.Contains(h.Record.Uploads, u => u.Data.AsSpan().SequenceEqual(bytes));
        foreach (var upload in h.Record.Uploads.Where(u => u.Data.AsSpan().SequenceEqual(bytes)))
            Assert.All(h.Record.IndexedDraws.Where(d => ReferenceEquals(d.VertexBuffer, upload.Buffer)),
                draw => Assert.Equal(0, draw.VertexOffset));
    }
}
