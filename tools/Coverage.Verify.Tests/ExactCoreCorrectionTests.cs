using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Render3D;
using KhaozEngine.Tools.Coverage;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

public sealed class ExactCoreCorrectionTests
{
    [Fact]
    public void Incident_core_deficit_moves_a_pointwise_idle_vertex()
    {
        var rig = new ExactPanelFixture(level: 3);
        var scratch = rig.PointwiseScratch();
        var pointwise = scratch.After.ToArray();
        var final = rig.Construct();
        var core = CoreVertices(rig.Topology);
        Assert.NotEmpty(core);
        Assert.Contains(core, i => pointwise[i].Position == scratch.Before[i].Position
            && final[i].Position.Z < scratch.Before[i].Position.Z);
        Assert.True(CoverageReferenceCorrection.TryApplyPrepared(rig.Topology, [rig.Command], scratch, out var failure), failure.ToString());
        Assert.Contains(scratch.FaceCorrections, extra => extra > 0);
        rig.AssertCertified(final);
        Assert.NotEmpty(final);
        for (int i = 0; i < final.Length; i++)
        {
            Assert.Equal(scratch.After[i].Position, final[i].Position);
            if (!core.Contains(i)) Assert.Equal(pointwise[i].Position, final[i].Position);
        }
    }

    [Fact]
    public void Shared_group_correction_is_applied_once_across_seam_aliases()
    {
        var rig = new ExactPanelFixture(level: 3);
        var plain = new ExactPanelFixture(level: 3, seams: false);
        var before = rig.Before();
        var plainBefore = plain.Before();
        var final = rig.Construct();
        var plainFinal = plain.Construct();
        var groups = Enumerable.Range(0, final.Length).GroupBy(i => rig.Topology.Vertices[i].PositionGroup).ToArray();
        Assert.NotEmpty(groups);
        int movedAliases = 0;
        foreach (var group in groups)
        {
            int first = group.First();
            foreach (int i in group) Assert.Equal(final[first].Position, final[i].Position);
            int match = Array.FindIndex(plainBefore, v => v.Position == before[first].Position);
            Assert.True(match >= 0);
            Assert.Equal(plainFinal[match].Position, final[first].Position);
            if (group.Count() > 1 && final[first].Position != before[first].Position) movedAliases++;
        }
        Assert.True(movedAliases > 0);
        rig.AssertCertified(final);
        plain.AssertCertified(plainFinal);
    }

    static HashSet<int> CoreVertices(CoverageRefinement topology)
    {
        var result = new HashSet<int>();
        for (int t = 0; t < topology.TriangleCount; t++)
            if (topology.Triangles[t].Role == CoverageTriangleRole.Core)
                for (int corner = 0; corner < 3; corner++) result.Add((int)topology.Indices[t * 3 + corner]);
        return result;
    }
}
