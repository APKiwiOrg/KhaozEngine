using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

internal static class CoverageFilteredQueueFixture
{
    internal static readonly Vector3[] Host = [new(-.25f, -1, 1), new(.25f, -1, 1), new(0, -.5f, 1)];
    internal static CoverageReferenceCase Case()
    {
        var fixture = CoverageFixture.CurvedPanel(false, 3, secondRegion: true);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        return new("queue-boundary", topology, [], [], fixture.Posed, Matrix4x4.Identity,
            CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity));
    }

    internal static int[] Faces(CoverageReferenceCase c) => Enumerable.Range(0, c.Topology.TriangleCount)
        .Where(t => c.Topology.Triangles[t].Role == CoverageTriangleRole.Core).Take(131).ToArray();

    internal static CoverageSupportSurface FlatSupport()
    {
        var support = CoverageCommands.Plate(CoverageFixture.CurvedPanel(false, 1));
        for (int i = 0; i < support.ReferenceData.Positions.Length; i++) support.ReferenceData.Positions[i].Z = 0;
        return support;
    }

    // Deliberate query ABI faults after valid face preparation. Pair 0 contributes 1, pair 1 fails,
    // pair 2 independently contributes 9 and pair 3 also fails. Ordered replay must retain 1.
    internal static CoverageSupportSurface Support(bool fail)
    {
        var support = CoverageCommands.Plate(CoverageFixture.CurvedPanel(false, 1));
        var data = support.ReferenceData;
        Vector3[] xy = [new(-2, -2, 0), new(2, -2, 0), new(0, 2, 0)];
        for (int i = 0; i < 3; i++) { data.Positions[i] = xy[i]; data.Positions[i + 3] = xy[i] with { Z = -8 }; }
        for (int pair = 0; pair < 4; pair++)
        {
            int v = pair == 2 ? 3 : 0;
            data.Triangles[pair] = data.Triangles[pair] with { A = v, B = v + 1, C = fail && pair % 2 == 1 ? v + 1 : v + 2 };
        }
        return support;
    }
}

public sealed class CoverageFilteredQueueFixtureTests
{
    [Fact]
    public void Deliberate_queue_fixture_spans_three_groups_and_retains_failed_prefix()
    {
        var c = CoverageFilteredQueueFixture.Case();
        int[] faces = CoverageFilteredQueueFixture.Faces(c);
        Assert.Equal(131, faces.Length);
        Assert.Equal(144, c.Topology.Triangles.Count(t => t.Role == CoverageTriangleRole.Core));
        Assert.True(CoverageFilteredQueueFixture.FlatSupport().ReferenceData.TryHeight(new CoverageReferenceArithmetic(),
            System.Numerics.Vector2.Zero, out float height));
        Assert.Equal(0f, height);
        Assert.Equal(CoverageGpuUpload.Support(c.Commands[0].Support).Words.Length,
            CoverageGpuUpload.Support(CoverageFilteredQueueFixture.Support(true)).Words.Length);
        Assert.True(faces.Select(t => t / 64).Distinct().Count() >= 3);
        Assert.Equal(12, faces.Length * 4 % 64);
        var scratch = CoverageGpuConformTestData.Prepare(c);
        var expected = new ModelVertex[c.Topology.VertexCount];
        Assert.True(CoverageReferenceConstructor.TryConstruct(c.Topology, c.Posed, c.Model, c.Commands, scratch, expected, out var failure), failure.ToString());
        var f = new CoverageReferenceArithmetic();
        Assert.False(CoverageFilteredQueueFixture.Support(true).ReferenceData.TryOverlayMaximum(f, CoverageFilteredQueueFixture.Host, out float prefix));
        Assert.Equal(1f, prefix);
        Assert.True(CoverageFilteredQueueFixture.Support(false).ReferenceData.TryOverlayMaximum(f, CoverageFilteredQueueFixture.Host, out float complete));
        Assert.Equal(9f, complete);
        var data = CoverageFilteredQueueFixture.Support(true).ReferenceData;
        Assert.Equal(data.Triangles[1].B, data.Triangles[1].C);
        Assert.Equal(data.Triangles[3].B, data.Triangles[3].C);
        Assert.NotEqual(data.Triangles[2].B, data.Triangles[2].C);
    }
}
