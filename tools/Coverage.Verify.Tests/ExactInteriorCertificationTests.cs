using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Tools.Coverage;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

public sealed class ExactInteriorCertificationTests
{
    [Fact]
    public void Clear_vertices_do_not_certify_an_uncleared_triangle_interior()
    {
        var rig = new ExactPanelFixture(level: 2);
        var before = rig.Before();
        var candidate = before.ToArray();
        float clearance = rig.Command.Clearance;
        int moved = 0;
        for (int i = 0; i < candidate.Length; i++)
        {
            if (rig.Topology.Vertices[i].Strength < 1) continue;
            float target = (float)(SheetHeight(rig, candidate[i].Position.X, candidate[i].Position.Y)
                - clearance * Math.Sqrt(1 + .08 * .08)) - 1e-6f;
            if (candidate[i].Position.Z > target) { candidate[i].Position.Z = target; moved++; }
            Assert.True(SheetHeight(rig, candidate[i].Position.X, candidate[i].Position.Y) - candidate[i].Position.Z >= clearance);
        }
        Assert.True(moved > 1);
        // Independently evaluate interior barycentric samples against the authored piecewise-linear sheet.
        // One interior witness of insufficient clearance disproves acceptance, even though every core vertex cleared.
        int intrusions = 0;
        for (int t = 0; t < rig.Topology.TriangleCount; t++)
        {
            if (rig.Topology.Triangles[t].Role != CoverageTriangleRole.Core) continue;
            var a = candidate[(int)rig.Topology.Indices[t * 3]].Position;
            var b = candidate[(int)rig.Topology.Indices[t * 3 + 1]].Position;
            var c = candidate[(int)rig.Topology.Indices[t * 3 + 2]].Position;
            for (int u = 1; u < 7; u++)
                for (int v = 1; v < 8 - u; v++)
                {
                    double x = (a.X * (double)u + b.X * (double)v + c.X * (8 - u - v)) / 8;
                    double y = (a.Y * (double)u + b.Y * (double)v + c.Y * (8 - u - v)) / 8;
                    double z = (a.Z * (double)u + b.Z * (double)v + c.Z * (8 - u - v)) / 8;
                    if (SheetHeight(rig, x, y) - z < clearance) intrusions++;
                }
        }
        Assert.True(intrusions > 0);
        Assert.False(CoverageCertification.ValidateGeometry(rig.Topology, before, candidate, Matrix4x4.Identity,
            [rig.Command], new CoverageScratch(rig.Topology), out var failure));
        Assert.Equal(CoverageFailureCode.EquipmentIntersection, failure.Code);
        rig.Context(failure);
        Assert.True(failure.Count > 0);
        rig.AssertCertified(rig.Construct());
    }

    static double SheetHeight(ExactPanelFixture rig, double x, double y)
    {
        var fixture = rig.Data;
        for (int s = 0; s < fixture.SupportIndices.Length; s += 3)
        {
            Vector3 a = fixture.RigidPositions[fixture.SupportIndices[s]], b = fixture.RigidPositions[fixture.SupportIndices[s + 1]],
                c = fixture.RigidPositions[fixture.SupportIndices[s + 2]];
            double area = Orient(a.X, a.Y, b.X, b.Y, c.X, c.Y);
            double u = Orient(b.X, b.Y, c.X, c.Y, x, y) / area, v = Orient(c.X, c.Y, a.X, a.Y, x, y) / area,
                w = Orient(a.X, a.Y, b.X, b.Y, x, y) / area;
            if (u >= -1e-12 && v >= -1e-12 && w >= -1e-12) return u * a.Z + v * b.Z + w * c.Z;
        }
        throw new InvalidOperationException("Point is outside the synthetic support sheet.");
        static double Orient(double ax, double ay, double bx, double by, double cx, double cy) => (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
    }
}
