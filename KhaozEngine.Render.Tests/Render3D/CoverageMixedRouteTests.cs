using System.Linq;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageMixedRouteTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Covered_cpu_and_uncovered_gpu_draws_coexist(bool reverse)
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
        Assert.Equal(1, h.Scene.CoverageDiagnosticsForTests.CoveredCpuPreparations);
        Assert.Equal(1, h.Scene.CoverageDiagnosticsForTests.UncoveredGpuDraws);
        Assert.Equal(h.Expected, h.Scene.CoverageVerticesForTests(frame).ToArray());
        h.AssertCanonicalDraws(frame);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Covered_and_uncovered_cpu_draws_keep_independent_ranges(bool reverse)
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
        Assert.Equal(1, h.Scene.CoverageDiagnosticsForTests.CoveredCpuPreparations);
        Assert.Equal(0, h.Scene.CoverageDiagnosticsForTests.UncoveredGpuDraws);
        h.AssertCanonicalDraws(frame);
        var route = h.Scene.CoverageSliceForTests(frame, 0);
        var mesh = h.UncoveredMesh;
        int start = h.Conformer.Topology.VertexCount;
        var draws = h.Record.IndexedDraws.Where(d => ReferenceEquals(d.VertexBuffer, route.Vertices)
            && !h.IsHostIndexBuffer(d.IndexBuffer)).ToArray();
        Assert.NotEmpty(draws);
        Assert.Single(draws.Select(d => d.IndexBuffer).Distinct());
        Assert.All(draws, d =>
        {
            Assert.Equal(start, d.VertexOffset);
            Assert.Equal((uint)mesh.Indices32.Length, d.IndexCount);
        });
        var composed = h.Palette.Select((m, i) => SkinningMath.Compose(m, mesh.InverseBind[i])).ToArray();
        var expected = mesh.Vertices.Select(v => SkinningMath.SkinVertex(v, composed)).ToArray();
        var upload = Assert.Single(h.Record.Uploads, u => ReferenceEquals(u.Buffer, route.Vertices));
        Assert.Equal(expected, MemoryMarshal.Cast<byte, ModelVertex>(upload.Data).Slice(start, expected.Length).ToArray());
    }
}
