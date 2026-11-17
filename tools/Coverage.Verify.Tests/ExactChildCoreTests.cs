using System.Linq;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

public sealed class ExactChildCoreTests
{
    // At level 4 the centre square's refined vertices form a 5 by 5 grid at spacing 0.5. Nine of the 25 lie under the
    // support square [-0.6, 0.6], so a whole core centre has 16 core position groups the support cannot hold.
    [Fact]
    public void Whole_core_triangles_escape_where_no_source_triangle_fits_the_support()
    {
        var rig = new ExactChildCoreRig(CoverageChildCoreFixture.Map(CoverageTriangleRole.Core, []));

        Assert.False(rig.TryConstruct(out _, out var failure));
        Assert.Equal(CoverageFailureCode.CoreEscaped, failure.Code);
        Assert.Equal(16, failure.Count);
        Assert.Equal(23u, failure.CommandId);
        Assert.Equal(17ul, failure.EquipmentInstanceId);
        Assert.Equal("synthetic-host", failure.HostIdentity);
    }

    [Fact]
    public void Child_core_cells_certify_and_one_escaping_cell_refuses()
    {
        var rig = new ExactChildCoreRig(CoverageChildCoreFixture.Map(CoverageTriangleRole.Collar,
            CoverageChildCoreFixture.CoreCells()));

        Assert.True(rig.TryConstruct(out var final, out var failure), failure.ToString());
        Assert.True(rig.Certify(final, out failure), failure.ToString());
        Assert.Equal(8, rig.Topology.Triangles.Count(t => t.Role == CoverageTriangleRole.Core));
        Assert.Equal(9, Enumerable.Range(0, rig.Topology.VertexCount).Count(rig.Topology.IsCoreVertex));
        var before = rig.Before();
        Assert.All(Enumerable.Range(0, before.Length).Where(rig.Topology.IsCoreVertex),
            v => Assert.True(final[v].Position.Z < before[v].Position.Z));

        // Ta cell 5 has its corner (1, 0.5) outside the support, so that one position group escapes.
        var escaping = new ExactChildCoreRig(CoverageChildCoreFixture.Map(CoverageTriangleRole.Collar,
            CoverageChildCoreFixture.CoreCells().Append(new(0, 0, CoverageChildCoreFixture.TriangleA, 4,
                CoverageChildCoreFixture.EscapeCellA, CoverageTriangleRole.Core))));
        Assert.False(escaping.TryConstruct(out _, out failure));
        Assert.Equal(CoverageFailureCode.CoreEscaped, failure.Code);
        Assert.Equal(1, failure.Count);
    }
}
