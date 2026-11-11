using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Tools.Coverage;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

public sealed class ExactOverlayTests
{
    readonly CoverageReferenceScratch scratch = new(new ExactPanelFixture().Topology, 2);

    [Fact]
    public void Float_overlay_rejects_false_endpoint_contact_on_overlapping_nearly_collinear_edges()
    {
        // Nearly collinear host and sheet edges have overlapping XY bounds, while the host corner lies
        // outside the sheet edge's bounds. Rounded endpoint classification must not invent contact.
        Vector3[] sheet = [new(.25f, -.25f, .04f), new(.25f, .25f, .04f), new(0, 0, .02f)];
        Vector3[] host = [new(-0.050000011920928955f, -0.04999999701976776f, 0),
            new(0.10000000149011612f, 0.10000000894069672f, 0), new(-.2f, .2f, 0)];
        Assert.Equal(-1, CoverageReferencePredicates.Edge(sheet[1], sheet[2], host[0]).Sign);
        Assert.Equal(-1, CoverageReferencePredicates.Edge(sheet[1], sheet[2], host[1]).Sign);
        Assert.Equal(-1, CoverageReferencePredicates.Edge(host[0], host[1], sheet[1]).Sign);
        Assert.Equal(-1, CoverageReferencePredicates.Edge(host[0], host[1], sheet[2]).Sign);
        Assert.True(MathF.Max(host[0].X, host[1].X) >= MathF.Min(sheet[1].X, sheet[2].X)
            && MathF.Max(host[0].Y, host[1].Y) >= MathF.Min(sheet[1].Y, sheet[2].Y));
        Assert.True(host[0].X < MathF.Min(sheet[1].X, sheet[2].X));
        CoverageAffinePoint Exact(Vector3 p) => CoverageAffinePoint.FromDouble(p.X, p.Y, p.Z);
        Assert.True(CoverageFaceOverlay.TryMaximum(host.Select(Exact).ToArray(), sheet.Select(Exact).ToArray(), out double exactPair));
        Assert.True(Triangle(sheet).TryOverlayMaximum(scratch.Arithmetic, host, out float pair));
        Assert.Equal(exactPair, pair);
        Assert.Equal(float.NegativeInfinity, pair);
    }

    [Fact]
    public void Float_overlay_keeps_the_near_collinear_proper_crossing_at_half_parameter()
    {
        Vector3[] sheet = [new(.25f, -.25f, .04f), new(.25f, .25f, .04f), new(0, 0, .02f)];
        Vector3[] host = [new(-0.050000011920928955f, -0.04999999701976776f, 0),
            new(0.20000001788139343f, 0.20000000298023224f, 0), new(-.2f, .2f, 0)];
        CoverageAffinePoint Exact(Vector3 p) => CoverageAffinePoint.FromDouble(p.X, p.Y, p.Z);
        var ca = CoverageReferencePredicates.Edge(sheet[1], sheet[2], host[0]);
        var cb = CoverageReferencePredicates.Edge(sheet[1], sheet[2], host[1]);
        Assert.Equal(-Math.ScaleB(1, -28), ca.Value);
        Assert.Equal(Math.ScaleB(1, -28), cb.Value);
        Assert.Equal(.5, ca.Value / (ca.Value - cb.Value));
        Assert.True(CoverageFaceOverlay.TryMaximum(host.Select(Exact).ToArray(), sheet.Select(Exact).ToArray(), out double exact));
        Assert.InRange(exact, -.026000001, -.025999998);
        Assert.True(Triangle(sheet).TryOverlayMaximum(scratch.Arithmetic, host, out float reference));
        Assert.InRange(Math.Abs(reference - exact), 0, 1e-8);
    }

    [Fact]
    public void Float_overlay_includes_edge_crossings_not_only_corners()
    {
        // The triangles overlap strictly through edge crossings. Neither triangle contains the other's corners.
        Vector3[] host = [new(0, 0, .2f), new(2, 0, .3f), new(1, 2, .1f)];
        Vector3[] sheet = [new(0, 1.25f, 0), new(2, 1.25f, .1f), new(1, -.75f, .05f)];
        var support = Triangle(sheet);
        Assert.Equal(3, host.Length);
        Assert.Equal(3, sheet.Length);
        Assert.All(host, p => Assert.False(Inside(sheet, p)));
        Assert.All(sheet, p => Assert.False(Inside(host, p)));
        float cornerOnly = float.NegativeInfinity;
        Assert.True(support.TryOverlayMaximum(scratch.Arithmetic, host, out float maximum));
        // The sheet is z=.05*x, host z=.2+.05*x-.075*y. The overlap reaches y=0.
        Assert.Equal(.2f, maximum, 6);
        Assert.True(cornerOnly < maximum);
        Assert.True(support.TryOverlayMaximum(scratch.Arithmetic, [host[0], host[2], host[1]], out float reversed));
        Assert.Equal(maximum, reversed, 6);
    }

    static CoverageReferenceSupport Triangle(Vector3[] points) => new(CoverageSupportSurface.Compile(
        ExactGeometryFixtures.Solid("overlay-triangle", points, [0, 1, 2]), [new(0, 0, 0, 1)], Matrix4x4.Identity));

    static bool Inside(Vector3[] t, Vector3 p)
    {
        double Edge(Vector3 a, Vector3 b) => ((double)b.X - a.X) * ((double)p.Y - a.Y) - ((double)b.Y - a.Y) * ((double)p.X - a.X);
        double a = Edge(t[0], t[1]), b = Edge(t[1], t[2]), c = Edge(t[2], t[0]);
        return a >= 0 && b >= 0 && c >= 0 || a <= 0 && b <= 0 && c <= 0;
    }

}
